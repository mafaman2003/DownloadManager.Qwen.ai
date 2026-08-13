using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Services;
using DownloadManager.Views;

namespace DownloadManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] CaptureExtensions =
    {
        ".zip",".rar",".7z",".tar",".gz",".xz",".iso",".exe",".msi",".apk",".dmg",".deb",".rpm",
        ".mp4",".mkv",".avi",".mov",".webm",".mp3",".flac",".wav",".pdf",".epub",".doc",".docx",
        ".xls",".xlsx",".ppt",".pptx",".jpg",".png",".gif",".webp"
    };

    private AppSettings _settings;
    private readonly DownloadEngine _engine;
    private readonly ClipboardMonitor _clipboard = new();

    private readonly ConcurrentDictionary<string, long> _received = new();
    private readonly ConcurrentDictionary<string, long> _lastTick = new();
    private readonly HashSet<string> _pausedByUser = new();
    private readonly DispatcherTimer _timer;

    private int _tickCount;
    private bool _dialogOpen;
    private DateTime? _scheduleStartFired, _scheduleStopFired;

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand StartAllCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand SettingsCommand { get; }

    private string _totalSpeedText = "Idle";
    public string TotalSpeedText { get => _totalSpeedText; private set => Set(ref _totalSpeedText, value); }

    private string _countsText = "";
    public string CountsText { get => _countsText; private set => Set(ref _countsText, value); }

    private bool _isEmpty = true;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public MainViewModel() : this(AppStore.LoadSettings()) { }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _engine = new DownloadEngine(_settings);

        _engine.BytesReceived += (id, n) => _received.AddOrUpdate(id, n, (_, v) => v + n);
        _engine.StatusNoteChanged += (id, note) => Dispatcher.UIThread.Post(() =>
        {
            var item = Downloads.FirstOrDefault(d => d.Id == id);
            if (item != null) item.StatusNote = note;
        });

        _clipboard.Filter = IsDownloadableUrl;
        _clipboard.UrlDetected += url => _ = OnClipboardUrlAsync(url);
        if (_settings.MonitorClipboard) _clipboard.Start();

        foreach (var item in AppStore.LoadDownloads())
        {
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
                item.Status = DownloadStatus.Paused;
            Wire(item);
            Downloads.Add(item);
        }
        IsEmpty = Downloads.Count == 0;

        AddCommand = new RelayCommand(async _ => await AddWithDialogAsync());
        StartAllCommand = new RelayCommand(_ => StartAll());
        PauseAllCommand = new RelayCommand(_ => PauseAll());
        ClearFinishedCommand = new RelayCommand(_ => ClearFinished());
        SettingsCommand = new RelayCommand(async _ => await OpenSettingsAsync());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ---------- wiring ----------

    private void Wire(DownloadItem item)
    {
        item.StartCommand = new RelayCommand(_ => Start(item));
        item.PauseCommand = new RelayCommand(_ => Pause(item));
        item.DeleteCommand = new RelayCommand(_ => Delete(item));
        item.OpenFolderCommand = new RelayCommand(_ => OpenFolder(item));
        item.VerifyCommand = new RelayCommand(async _ => await VerifyChecksumAsync(item));
    }

    private bool IsDownloadableUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (_settings.CaptureAllUrls) return true;

        var path = uri.AbsolutePath;
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1) return false;
        return CaptureExtensions.Contains(path[dot..].ToLowerInvariant());
    }

    // ---------- queue ----------

    private int ActiveCount => Downloads.Count(i => i.Status == DownloadStatus.Downloading);

    /// <summary>Promotes queued items while a concurrency slot is free.</summary>
    private void ProcessQueue()
    {
        foreach (var item in Downloads.Where(i => i.Status == DownloadStatus.Queued).ToList())
        {
            if (ActiveCount >= _settings.MaxConcurrentDownloads)
            {
                item.StatusNote ??= "Waiting in queue…";
                continue;
            }
            _ = RunAsync(item);
        }
    }

    public void Start(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Downloading or DownloadStatus.Completed) return;
        _pausedByUser.Remove(item.Id);
        item.Error = null;
        item.StatusNote = null;
        item.Status = DownloadStatus.Queued;
        ProcessQueue();
    }

    private void StartAll()
    {
        foreach (var item in Downloads.Where(i => i.CanStart).ToList())
        {
            _pausedByUser.Remove(item.Id);
            item.Error = null;
            item.StatusNote = null;
            item.Status = DownloadStatus.Queued;
        }
        ProcessQueue();
    }

    public void Pause(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Downloading)
        {
            _pausedByUser.Add(item.Id);
            _engine.Cancel(item.Id);
        }
        if (item.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
        {
            item.Status = DownloadStatus.Paused;
            item.StatusNote = null;
            item.Speed = 0;
            SaveNow();
            ProcessQueue();
        }
    }

    private void PauseAll()
    {
        foreach (var item in Downloads.Where(i => i.IsDownloading).ToList())
        {
            _pausedByUser.Add(item.Id);
            _engine.Cancel(item.Id);
            item.Status = DownloadStatus.Paused;
            item.StatusNote = null;
            item.Speed = 0;
        }
        foreach (var item in Downloads.Where(i => i.Status == DownloadStatus.Queued).ToList())
        {
            item.Status = DownloadStatus.Paused;
            item.StatusNote = null;
        }
        SaveNow();
    }

    public void Delete(DownloadItem item)
    {
        _engine.Cancel(item.Id);
        _pausedByUser.Remove(item.Id);
        Downloads.Remove(item);
        IsEmpty = Downloads.Count == 0;
        try
        {
            var path = Path.Combine(item.SaveDirectory, item.FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        SaveNow();
        ProcessQueue();
    }

    private void ClearFinished()
    {
        var finished = Downloads.Where(i => i.Status == DownloadStatus.Completed).ToList();
        foreach (var i in finished) Downloads.Remove(i);
        IsEmpty = Downloads.Count == 0;
        SaveNow();
    }

    // ---------- add / run ----------

    private async Task AddWithDialogAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new AddDownloadWindow(null, _settings.DefaultSaveDirectory, _settings.DefaultSegments);
            if (await dialog.ShowDialog<bool>(MainWindow!))
                await AddAsync(dialog.Url, dialog.SaveDirectory, dialog.FileName, dialog.Segments,
                               dialog.Mirrors, dialog.ChecksumAlgorithm, dialog.ExpectedChecksum);
        }
        finally { _dialogOpen = false; }
    }

    private async Task OnClipboardUrlAsync(string url)
    {
        if (_dialogOpen || !IsDownloadableUrl(url)) return;
        _dialogOpen = true;
        try
        {
            var dialog = new AddDownloadWindow(url, _settings.DefaultSaveDirectory, _settings.DefaultSegments);
            if (await dialog.ShowDialog<bool>(MainWindow!))
                await AddAsync(dialog.Url, dialog.SaveDirectory, dialog.FileName, dialog.Segments,
                               dialog.Mirrors, dialog.ChecksumAlgorithm, dialog.ExpectedChecksum);
        }
        catch { }
        finally { _dialogOpen = false; }
    }

    public async Task AddAsync(string url, string directory, string? fileName, int segments,
                               List<string> mirrors, string? checksumAlgorithm, string? expectedChecksum)
    {
        var item = new DownloadItem
        {
            Url = url,
            Mirrors = mirrors,
            SaveDirectory = directory,
            SegmentCount = segments,
            ChecksumAlgorithm = checksumAlgorithm,
            ExpectedChecksum = expectedChecksum,
            Status = DownloadStatus.Queued
        };

        try
        {
            var (size, resumable, serverName) = await _engine.ProbeAsync(item.AllUrls);
            item.TotalSize = size;
            item.SupportsResume = resumable;
            item.FileName = !string.IsNullOrWhiteSpace(fileName) ? fileName!
                          : !string.IsNullOrWhiteSpace(serverName) ? serverName!
                          : GuessFileName(url);
        }
        catch (Exception ex)
        {
            item.FileName = string.IsNullOrWhiteSpace(fileName) ? GuessFileName(url) : fileName!;
            item.Error = ex.Message;
            item.Status = DownloadStatus.Error;
        }

        Wire(item);
        Downloads.Insert(0, item);
        IsEmpty = false;
        SaveNow();

        if (item.Status != DownloadStatus.Error)
            ProcessQueue();
    }

    private async Task RunAsync(DownloadItem item)
    {
        item.Status = DownloadStatus.Downloading;
        item.StatusNote = null;
        try
        {
            await Task.Run(() => _engine.RunAsync(item));
            if (item.TotalSize > 0) item.Downloaded = item.TotalSize;

            if (!string.IsNullOrWhiteSpace(item.ExpectedChecksum))
            {
                item.StatusNote = "Verifying checksum…";
                try
                {
                    string path = Path.Combine(item.SaveDirectory, item.FileName);
                    string actual = await ChecksumService.ComputeAsync(item.ChecksumAlgorithm!, path);
                    if (string.Equals(actual, item.ExpectedChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        item.ChecksumVerified = true;
                        item.StatusNote = "Checksum verified ✓";
                        item.Status = DownloadStatus.Completed;
                    }
                    else
                    {
                        item.StatusNote = null;
                        item.Error = $"Checksum mismatch — expected {item.ExpectedChecksum}, got {actual}.";
                        item.Status = DownloadStatus.Error;
                    }
                }
                catch (Exception ex)
                {
                    item.StatusNote = null;
                    item.Error = $"Checksum verification failed: {ex.Message}";
                    item.Status = DownloadStatus.Error;
                }
            }
            else
            {
                item.Status = DownloadStatus.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            item.StatusNote = null;
            item.Status = _pausedByUser.Contains(item.Id)
                ? DownloadStatus.Paused
                : DownloadStatus.Canceled;
        }
        catch (Exception ex)
        {
            item.StatusNote = null;
            item.Status = DownloadStatus.Error;
            item.Error = ex.Message;
        }
        finally
        {
            item.Speed = 0;
            SaveNow();
            ProcessQueue();   // free slot → promote next queued item
        }
    }

    public async Task VerifyChecksumAsync(DownloadItem item)
    {
        if (!item.HasChecksum) return;
        string path = Path.Combine(item.SaveDirectory, item.FileName);
        if (!File.Exists(path)) { item.StatusNote = "File not found."; return; }

        item.StatusNote = "Verifying checksum…";
        try
        {
            string actual = await ChecksumService.ComputeAsync(item.ChecksumAlgorithm!, path);
            item.ChecksumVerified = string.Equals(actual, item.ExpectedChecksum, StringComparison.OrdinalIgnoreCase);
            item.StatusNote = item.ChecksumVerified
                ? "Checksum verified ✓"
                : $"Checksum mismatch — got {actual}";
        }
        catch (Exception ex)
        {
            item.StatusNote = $"Verify failed: {ex.Message}";
        }
        SaveNow();
    }

    // ---------- settings ----------

    private async Task OpenSettingsAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new SettingsWindow(_settings);
            var result = await dialog.ShowDialog<AppSettings?>(MainWindow!);
            if (result != null)
            {
                _settings = result;
                AppStore.SaveSettings(_settings);
                ApplySettings();
            }
        }
        finally { _dialogOpen = false; }
    }

    private void ApplySettings()
    {
        _engine.ApplySettings(_settings);
        if (_settings.MonitorClipboard) _clipboard.Start(); else _clipboard.Stop();
        
        // Apply theme change
        if (Application.Current is App app)
            app.UpdateTheme(_settings.SelectedTheme);
        
        // Apply compact mode
        if (MainWindow != null)
        {
            if (_settings.UseCompactMode)
                MainWindow.Classes.Add("compact");
            else
                MainWindow.Classes.Remove("compact");
        }
        
        ProcessQueue();
    }

    // ---------- timer: speed, autosave, scheduler ----------

    private void OnTick(object? sender, EventArgs e)
    {
        long totalSpeed = 0;
        int active = 0, queued = 0, completed = 0;

        foreach (var item in Downloads)
        {
            switch (item.Status)
            {
                case DownloadStatus.Downloading:
                    active++;
                    if (_engine.TryGetDownloaded(item.Id, out var downloaded))
                        item.Downloaded = downloaded;

                    long cum = _received.GetValueOrDefault(item.Id);
                    long prev = _lastTick.GetValueOrDefault(item.Id);
                    long instant = Math.Max(0, cum - prev);
                    _lastTick[item.Id] = cum;

                    item.Speed = item.Speed <= 0 ? instant : (long)(item.Speed * 0.6 + instant * 0.4);
                    totalSpeed += item.Speed;
                    break;
                case DownloadStatus.Queued: queued++; break;
                case DownloadStatus.Completed: completed++; break;
            }
        }

        TotalSpeedText = active > 0 ? $"▼ {Format.Size(totalSpeed)}/s total" : "Idle";
        CountsText = $"{active} downloading · {queued} queued · {completed} completed · {Downloads.Count} total";

        CheckSchedule();

        if (active > 0 && ++_tickCount % 5 == 0)
            SaveNow();
    }

    private void CheckSchedule()
    {
        if (!_settings.SchedulerEnabled) return;
        var now = DateTime.Now;

        if (now.TimeOfDay >= _settings.ScheduleStart && _scheduleStartFired?.Date != now.Date)
        {
            _scheduleStartFired = now;
            StartAll();
        }

        if (_settings.ScheduleStop > _settings.ScheduleStart
            && now.TimeOfDay >= _settings.ScheduleStop
            && _scheduleStopFired?.Date != now.Date)
        {
            _scheduleStopFired = now;
            PauseAll();
        }
    }

    private void SaveNow() => AppStore.SaveDownloads(Downloads);

    private static string GuessFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return "download.bin";
    }

    private static void OpenFolder(DownloadItem item)
    {
        try
        {
            string path = Path.Combine(item.SaveDirectory, item.FileName);
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{item.SaveDirectory}\"");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", File.Exists(path) ? $"\"{path}\"" : $"\"{item.SaveDirectory}\"");
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{item.SaveDirectory}\""));
        }
        catch { }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _clipboard.Stop();
        _engine.CancelAll();
        foreach (var item in Downloads.Where(i => i.Status == DownloadStatus.Downloading))
            item.Status = DownloadStatus.Paused;
        SaveNow();
    }
}