using Avalonia.Controls;
using DownloadManager.ViewModels;

namespace DownloadManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MainViewModel)?.Shutdown();
    }
}