using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DownloadManager.Models;

namespace DownloadManager.Views;

public sealed class HostLimitRow
{
    public string Host { get; }
    public long BytesPerSec { get; }
    public string Display => $"{Host} — {Format.Size(BytesPerSec)}/s";

    public HostLimitRow(string host, long bytesPerSec)
    {
        Host = host;
        BytesPerSec = bytesPerSec;
    }
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;   // edited copy; applied only on Save

    public ObservableCollection<HostLimitRow> HostLimits { get; } = new();

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        // Deep-copy so Cancel never mutates live settings.
        _working = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current))!;

        MaxConcPick.Value = _working.MaxConcurrentDownloads;
        SegPick.Value = _working.DefaultSegments;
        DirBox.Text = _working.DefaultSaveDirectory;
        RetryPick.Value = _working.MaxRetries;

        // Appearance
        ThemePick.ItemsSource = Enum.GetValues<AppTheme>();
        ThemePick.SelectedItem = _working.SelectedTheme;
        CompactCheck.IsChecked = _working.UseCompactMode;
        NotifyCheck.IsChecked = _working.ShowNotifications;
        TrayCheck.IsChecked = _working.MinimizeToTray;

        ClipCheck.IsChecked = _working.MonitorClipboard;
        AllUrlsCheck.IsChecked = _working.CaptureAllUrls;

        GlobalLimitPick.Value = _working.GlobalSpeedLimit / 1024;
        foreach (var kv in _working.HostSpeedLimits)
            HostLimits.Add(new HostLimitRow(kv.Key, kv.Value));
        HostList.ItemsSource = HostLimits;

        ProxyModePick.ItemsSource = Enum.GetValues<ProxyMode>();
        ProxyModePick.SelectedItem = _working.ProxyMode;
        ProxyUrlBox.Text = _working.ProxyUrl;
        ProxyUserBox.Text = _working.ProxyUser;
        ProxyPassBox.Text = _working.ProxyPassword;

        SchedCheck.IsChecked = _working.SchedulerEnabled;
        StartPick.SelectedTime = _working.ScheduleStart;
        StopPick.SelectedTime = _working.ScheduleStop;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose default download folder",
            AllowMultiple = false
        });
        if (folders.Count > 0)
            DirBox.Text = folders[0].Path.LocalPath;
    }

    private void OnAddHost(object? sender, RoutedEventArgs e)
    {
        string host = HostBox.Text?.Trim() ?? string.Empty;
        if (host.Length == 0) return;

        long bytesPerSec = (long)(HostLimitPick.Value ?? 0) * 1024;
        if (bytesPerSec <= 0) return;

        var existing = HostLimits.FirstOrDefault(r => r.Host == host);
        if (existing != null) HostLimits.Remove(existing);
        HostLimits.Add(new HostLimitRow(host, bytesPerSec));
        HostBox.Text = string.Empty;
    }

    private void OnRemoveHost(object? sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is HostLimitRow row)
            HostLimits.Remove(row);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _working.MaxConcurrentDownloads = (int)(MaxConcPick.Value ?? 3);
        _working.DefaultSegments = (int)(SegPick.Value ?? 8);
        _working.DefaultSaveDirectory = DirBox.Text?.Trim() ?? string.Empty;
        _working.MaxRetries = (int)(RetryPick.Value ?? 8);

        // Appearance
        _working.SelectedTheme = ThemePick.SelectedItem is AppTheme t ? t : AppTheme.Dark;
        _working.UseCompactMode = CompactCheck.IsChecked == true;
        _working.ShowNotifications = NotifyCheck.IsChecked == true;
        _working.MinimizeToTray = TrayCheck.IsChecked == true;

        _working.MonitorClipboard = ClipCheck.IsChecked == true;
        _working.CaptureAllUrls = AllUrlsCheck.IsChecked == true;

        _working.GlobalSpeedLimit = (long)(GlobalLimitPick.Value ?? 0) * 1024;
        _working.HostSpeedLimits = HostLimits.ToDictionary(r => r.Host, r => r.BytesPerSec);

        _working.ProxyMode = ProxyModePick.SelectedItem is ProxyMode m ? m : ProxyMode.System;
        _working.ProxyUrl = ProxyUrlBox.Text?.Trim() ?? string.Empty;
        _working.ProxyUser = ProxyUserBox.Text ?? string.Empty;
        _working.ProxyPassword = ProxyPassBox.Text ?? string.Empty;

        _working.SchedulerEnabled = SchedCheck.IsChecked == true;
        _working.ScheduleStart = StartPick.SelectedTime ?? TimeSpan.FromHours(2);
        _working.ScheduleStop = StopPick.SelectedTime ?? TimeSpan.FromHours(8);

        Close(_working);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}