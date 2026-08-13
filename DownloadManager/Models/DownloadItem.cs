using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Input;
using DownloadManager.ViewModels;

namespace DownloadManager.Models;

public sealed class DownloadItem : ObservableObject
{
    // ---- persisted state ----
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public List<string> Mirrors { get; set; } = new();
    public string FileName { get; set; } = "download";
    public string SaveDirectory { get; set; } = string.Empty;
    public int SegmentCount { get; set; } = 8;
    public long TotalSize { get; set; }
    public bool SupportsResume { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<SegmentInfo> Segments { get; set; } = new();

    // Checksum
    public string? ChecksumAlgorithm { get; set; }   // "MD5" | "SHA-1" | "SHA-256" | "SHA-512"
    public string? ExpectedChecksum { get; set; }
    public bool ChecksumVerified { get; set; }

    private long _downloaded;
    public long Downloaded
    {
        get => _downloaded;
        set
        {
            if (Set(ref _downloaded, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressLine));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(EtaText));
            }
        }
    }

    private long _speed;
    public long Speed
    {
        get => _speed;
        set
        {
            if (Set(ref _speed, value))
            {
                OnPropertyChanged(nameof(SpeedText));
                OnPropertyChanged(nameof(EtaText));
            }
        }
    }

    private DownloadStatus _status = DownloadStatus.Queued;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(CanVerify));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(SpeedText));
            }
        }
    }

    private string? _error;
    public string? Error { get => _error; set => Set(ref _error, value); }

    private string? _statusNote;
    [JsonIgnore]
    public string? StatusNote
    {
        get => _statusNote;
        set { if (Set(ref _statusNote, value)) OnPropertyChanged(nameof(ProgressLine)); }
    }

    // ---- commands (wired by the ViewModel) ----
    [JsonIgnore] public ICommand StartCommand { get; set; } = null!;
    [JsonIgnore] public ICommand PauseCommand { get; set; } = null!;
    [JsonIgnore] public ICommand DeleteCommand { get; set; } = null!;
    [JsonIgnore] public ICommand OpenFolderCommand { get; set; } = null!;
    [JsonIgnore] public ICommand VerifyCommand { get; set; } = null!;

    // ---- computed ----
    [JsonIgnore] public double Progress => TotalSize > 0 ? Math.Min(100.0, Downloaded * 100.0 / TotalSize) : 0;
    [JsonIgnore] public bool IsIndeterminate => TotalSize <= 0 && Status == DownloadStatus.Downloading;
    [JsonIgnore] public bool IsDownloading => Status == DownloadStatus.Downloading;
    [JsonIgnore] public bool IsCompleted => Status == DownloadStatus.Completed;
    [JsonIgnore] public bool HasError => Status == DownloadStatus.Error;
    [JsonIgnore] public bool HasChecksum => !string.IsNullOrWhiteSpace(ExpectedChecksum);
    [JsonIgnore] public bool CanVerify => HasChecksum && Status is DownloadStatus.Completed or DownloadStatus.Error;
    [JsonIgnore] public bool CanStart => Status is DownloadStatus.Queued or DownloadStatus.Paused
        or DownloadStatus.Error or DownloadStatus.Canceled;

    [JsonIgnore]
    public string StatusIcon => Status switch
    {
        DownloadStatus.Downloading => "↓",
        DownloadStatus.Queued => "⋯",
        DownloadStatus.Paused => "‖",
        DownloadStatus.Completed => ChecksumVerified ? "✓✓" : "✓",
        DownloadStatus.Error => "!",
        DownloadStatus.Canceled => "⊘",
        _ => "…"
    };

    [JsonIgnore]
    public string ProgressText => TotalSize > 0
        ? $"{Progress:0.0}% of {Format.Size(TotalSize)}"
        : $"{Format.Size(Downloaded)} received";

    [JsonIgnore]
    public string ProgressLine => string.IsNullOrWhiteSpace(StatusNote) ? ProgressText : StatusNote!;

    [JsonIgnore]
    public string SizeText => TotalSize > 0 ? Format.Size(TotalSize) : Format.Size(Downloaded);

    [JsonIgnore]
    public string SpeedText => IsDownloading ? $"{Format.Size(Speed)}/s" : "—";

    [JsonIgnore]
    public string EtaText => IsDownloading && Speed > 1 && TotalSize > Downloaded
        ? Format.Time((TotalSize - Downloaded) / (double)Speed)
        : string.Empty;

    private string? _host;
    [JsonIgnore]
    public string Host => _host ??= Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : string.Empty;

    [JsonIgnore]
    public IReadOnlyList<string> AllUrls
    {
        get
        {
            var list = new List<string> { Url };
            foreach (var m in Mirrors)
                if (Uri.IsWellFormedUriString(m, UriKind.Absolute)) list.Add(m);
            return list.Distinct().ToList();
        }
    }
}