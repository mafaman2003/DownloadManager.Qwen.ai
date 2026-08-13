using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DownloadManager.Models;

namespace DownloadManager.Services;

public sealed class DownloadEngine
{
    private sealed class ActiveDownload
    {
        public CancellationTokenSource Cts { get; } = new();
        public List<SegmentInfo> Segments { get; set; } = new();
        public SafeFileHandle? Handle { get; set; }
        public List<string> Urls { get; set; } = new();
        public int UrlIndex;
    }

    private readonly ConcurrentDictionary<string, ActiveDownload> _active = new();
    private readonly ThrottleManager _throttle = new();
    private HttpClient _http;

    public int MaxRetries { get; private set; } = 8;

    public event Action<string, long>? BytesReceived;
    public event Action<string, string?>? StatusNoteChanged;

    public DownloadEngine(AppSettings settings)
    {
        _http = HttpFactory.Create(settings);
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings settings)
    {
        MaxRetries = Math.Clamp(settings.MaxRetries, 0, 30);
        _throttle.Configure(settings.GlobalSpeedLimit, settings.HostSpeedLimits);

        // Swap the client; keep the old one alive briefly for in-flight streams.
        var old = Interlocked.Exchange(ref _http, HttpFactory.Create(settings));
        _ = Task.Delay(TimeSpan.FromMinutes(5))
                .ContinueWith(_ => { try { old.Dispose(); } catch { } });
    }

    public bool IsActive(string id) => _active.ContainsKey(id);

    public void Cancel(string id)
    {
        if (_active.TryGetValue(id, out var d)) d.Cts.Cancel();
    }

    public void CancelAll()
    {
        foreach (var d in _active.Values) d.Cts.Cancel();
    }

    public bool TryGetDownloaded(string id, out long bytes)
    {
        bytes = 0;
        if (!_active.TryGetValue(id, out var d)) return false;
        long sum = 0;
        foreach (var s in d.Segments) sum += s.Downloaded;
        bytes = sum;
        return true;
    }

    private static string NextUrl(ActiveDownload act)
    {
        var idx = Interlocked.Increment(ref act.UrlIndex);
        return act.Urls[(idx & int.MaxValue) % act.Urls.Count];
    }

    private static bool IsTransientStatus(HttpStatusCode code) => code is
        HttpStatusCode.RequestTimeout or            // 408
        HttpStatusCode.TooManyRequests or           // 429
        HttpStatusCode.InternalServerError or       // 500
        HttpStatusCode.BadGateway or                // 502
        HttpStatusCode.ServiceUnavailable or        // 503
        HttpStatusCode.GatewayTimeout;              // 504

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex switch
        {
            ResumeNotSupportedException => false,
            TransientHttpException => true,
            HttpRequestException => true,
            TimeoutException => true,
            IOException => true,
            TaskCanceledException => true,          // HTTP timeout
            _ => false
        };
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        double seconds = Math.Min(30, Math.Pow(2, attempt - 1));   // 1,2,4,8,16,30…
        seconds += Random.Shared.NextDouble() * 0.5;               // jitter
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Probes the URL list (primary + mirrors) until one answers.</summary>
    public async Task<(long Size, bool Resumable, string? FileName)> ProbeAsync(IReadOnlyList<string> urls)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try { return await ProbeSingleAsync(url).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new IOException("No valid URLs provided.");
    }

    private async Task<(long Size, bool Resumable, string? FileName)> ProbeSingleAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (IsTransientStatus(response.StatusCode))
            throw new TransientHttpException(response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Server returned HTTP {(int)response.StatusCode}.");

        long size = -1;
        bool resumable = false;

        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange?.Length is long total)
        {
            size = total;
            resumable = true;
        }
        else
        {
            size = response.Content.Headers.ContentLength ?? -1;
            resumable = response.Headers.AcceptRanges.Contains("bytes");
        }

        string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName;
        fileName = fileName?.Trim('"');

        return (size, resumable, string.IsNullOrWhiteSpace(fileName) ? null : fileName);
    }

    /// <summary>Runs (or resumes) a download with mirrors, retries and throttling.</summary>
    public async Task RunAsync(DownloadItem item)
    {
        var act = new ActiveDownload { Urls = item.AllUrls.ToList() };
        if (!_active.TryAdd(item.Id, act))
            throw new InvalidOperationException("This download is already running.");

        try
        {
            var (size, resumable, _) = await ProbeAsync(act.Urls).ConfigureAwait(false);
            item.SupportsResume = resumable;

            if (size > 0 && item.TotalSize != size)
            {
                item.TotalSize = size;
                item.Segments.Clear();
                item.Downloaded = 0;
            }

            Directory.CreateDirectory(item.SaveDirectory);
            string path = Path.Combine(item.SaveDirectory, item.FileName);
            var ct = act.Cts.Token;

            if (item.TotalSize > 0)
            {
                int count = resumable ? Math.Clamp(item.SegmentCount, 1, 32) : 1;
                if (!AreSegmentsValid(item.Segments, item.TotalSize))
                {
                    item.Segments = BuildSegments(item.TotalSize, count);
                    item.Downloaded = 0;
                }
                act.Segments = item.Segments;

                act.Handle = File.OpenHandle(path, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
                RandomAccess.SetLength(act.Handle, item.TotalSize);

                var tasks = item.Segments
                    .Select(s => DownloadSegmentWithRetryAsync(item, s, act, ct))
                    .ToList();

                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch
                {
                    act.Cts.Cancel();                       // stop sibling segments
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                    throw;
                }
            }
            else
            {
                item.Segments = new List<SegmentInfo> { new() { Index = 0, Start = 0, End = -1 } };
                item.Downloaded = 0;
                act.Segments = item.Segments;

                act.Handle = File.OpenHandle(path, FileMode.Create,
                    FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
                await DownloadSegmentWithRetryAsync(item, item.Segments[0], act, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            act.Handle?.Dispose();
            _active.TryRemove(item.Id, out _);
        }
    }

    private async Task DownloadSegmentWithRetryAsync(DownloadItem item, SegmentInfo segment,
                                                     ActiveDownload act, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadSegmentAttemptAsync(item, segment, act, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt <= MaxRetries)
            {
                var delay = BackoffDelay(attempt);
                StatusNoteChanged?.Invoke(item.Id,
                    $"Network error — retry {attempt}/{MaxRetries} in {(int)delay.TotalSeconds + 1}s ({NextUrl(act)})");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                StatusNoteChanged?.Invoke(item.Id, null);
            }
        }
    }

    private async Task DownloadSegmentAttemptAsync(DownloadItem item, SegmentInfo segment,
                                                   ActiveDownload act, CancellationToken ct)
    {
        long from = segment.Start + segment.Downloaded;
        if (segment.End >= 0 && from > segment.End) return;

        string url = NextUrl(act);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = segment.End >= 0
            ? new RangeHeaderValue(from, segment.End)
            : new RangeHeaderValue(from, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                        .ConfigureAwait(false);

        if (IsTransientStatus(response.StatusCode))
            throw new TransientHttpException(response.StatusCode);

        if (response.StatusCode == HttpStatusCode.OK && from > segment.Start)
            throw new ResumeNotSupportedException();

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var handle = act.Handle!;
        var buffer = new byte[64 * 1024];
        long offset = from;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;

            await _throttle.WaitAsync(item.Host, read, ct).ConfigureAwait(false);   // per-site + global limit
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct).ConfigureAwait(false);

            offset += read;
            segment.Downloaded += read;
            BytesReceived?.Invoke(item.Id, read);
        }
    }

    private static bool AreSegmentsValid(List<SegmentInfo> segments, long totalSize)
        => segments.Count > 0
           && segments[0].Start == 0
           && segments[^1].End == totalSize - 1
           && segments.Sum(s => s.Downloaded) <= totalSize;

    private static List<SegmentInfo> BuildSegments(long totalSize, int count)
    {
        count = (int)Math.Clamp(count, 1, Math.Max(1, totalSize / (256 * 1024)));
        var list = new List<SegmentInfo>(count);
        long chunk = totalSize / count;
        long pos = 0;
        for (int i = 0; i < count; i++)
        {
            long end = i == count - 1 ? totalSize - 1 : pos + chunk - 1;
            list.Add(new SegmentInfo { Index = i, Start = pos, End = end });
            pos = end + 1;
        }
        return list;
    }
}