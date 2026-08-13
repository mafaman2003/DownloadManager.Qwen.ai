using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DownloadManager.Services;

/// <summary>Simple token-bucket rate limiter. Rate in bytes/sec; 0 = unlimited.</summary>
public sealed class TokenBucket
{
    private long _rate;
    public long Rate
    {
        get => Interlocked.Read(ref _rate);
        set => Interlocked.Exchange(ref _rate, value);
    }

    private readonly object _gate = new();
    private double _tokens;
    private DateTime _last = DateTime.UtcNow;

    public async Task WaitAsync(long bytes, CancellationToken ct)
    {
        var rate = Rate;
        if (rate <= 0) return;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double waitSeconds;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                _tokens += (now - _last).TotalSeconds * rate;
                if (_tokens > rate) _tokens = rate;   // cap burst at ~1 second
                _last = now;

                if (_tokens >= bytes) { _tokens -= bytes; return; }
                waitSeconds = (bytes - _tokens) / (double)rate;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(waitSeconds, 0.5)), ct);
        }
    }
}

public sealed class ThrottleManager
{
    private readonly TokenBucket _global = new();
    private readonly ConcurrentDictionary<string, TokenBucket> _hosts = new();

    public void Configure(long globalBytesPerSec, IDictionary<string, long> hostLimits)
    {
        _global.Rate = globalBytesPerSec;
        _hosts.Clear();
        foreach (var kv in hostLimits)
            if (kv.Value > 0)
                _hosts[kv.Key] = new TokenBucket { Rate = kv.Value };
    }

    public Task WaitAsync(string host, int bytes, CancellationToken ct)
    {
        if (host.Length > 0 && _hosts.TryGetValue(host, out var bucket))
            return WaitBothAsync(bucket, bytes, ct);
        return _global.WaitAsync(bytes, ct);
    }

    private async Task WaitBothAsync(TokenBucket hostBucket, int bytes, CancellationToken ct)
    {
        await hostBucket.WaitAsync(bytes, ct);
        await _global.WaitAsync(bytes, ct);
    }
}