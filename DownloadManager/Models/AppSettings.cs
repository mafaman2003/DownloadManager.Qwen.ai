using System;
using System.Collections.Generic;

namespace DownloadManager.Models;

public enum ProxyMode { System, None, Custom }

public sealed class AppSettings
{
    // General
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DefaultSegments { get; set; } = 8;
    public string DefaultSaveDirectory { get; set; } = string.Empty;

    // Clipboard
    public bool MonitorClipboard { get; set; } = false;
    public bool CaptureAllUrls { get; set; } = false;

    // Speed limits (bytes/sec; 0 = unlimited)
    public long GlobalSpeedLimit { get; set; } = 0;
    public Dictionary<string, long> HostSpeedLimits { get; set; } = new();

    // Network
    public int MaxRetries { get; set; } = 8;
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    public string ProxyUrl { get; set; } = string.Empty;
    public string ProxyUser { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;

    // Scheduler (daily window; stop must be after start)
    public bool SchedulerEnabled { get; set; } = false;
    public TimeSpan ScheduleStart { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan ScheduleStop { get; set; } = TimeSpan.FromHours(8);
}