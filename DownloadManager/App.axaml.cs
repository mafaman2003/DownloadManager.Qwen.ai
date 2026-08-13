using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DownloadManager.Models;
using DownloadManager.Services;
using DownloadManager.ViewModels;

namespace DownloadManager;

public partial class App : Application
{
    private AppSettings? _settings;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _settings = AppStore.LoadSettings();
        ApplyTheme(_settings.SelectedTheme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(_settings!);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            
            // Apply compact mode if enabled
            if (_settings.UseCompactMode)
            {
                desktop.MainWindow.Classes.Add("compact");
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void UpdateTheme(AppTheme theme)
    {
        ApplyTheme(theme);
    }

    private void ApplyTheme(AppTheme theme)
    {
        switch (theme)
        {
            case AppTheme.Light:
                RequestedThemeVariant = ThemeVariant.Light;
                break;
            case AppTheme.Dark:
                RequestedThemeVariant = ThemeVariant.Dark;
                break;
            case AppTheme.FollowSystem:
                RequestedThemeVariant = ThemeVariant.Default;
                break;
        }
    }
}