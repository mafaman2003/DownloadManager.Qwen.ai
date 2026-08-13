using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DownloadManager.Services;

/// <summary>Polls the clipboard every 1.5 s and raises an event when a new URL appears.</summary>
public sealed class ClipboardMonitor
{
    private readonly DispatcherTimer _timer;
    private string? _lastText;

    public event Action<string>? UrlDetected;
    public Func<string, bool>? Filter { get; set; }

    public ClipboardMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private async Task PollAsync()
    {
        try
        {
            var clipboard = (Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
            if (clipboard == null) return;

            var text = await clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || text == _lastText) return;
            _lastText = text;

            text = text.Trim();
            if (Filter == null || Filter(text))
                UrlDetected?.Invoke(text);
        }
        catch { /* clipboard unavailable on some platforms/states */ }
    }
}