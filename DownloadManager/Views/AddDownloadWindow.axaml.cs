using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DownloadManager.Services;

namespace DownloadManager.Views;

public partial class AddDownloadWindow : Window
{
    public AddDownloadWindow(string? presetUrl = null, string? defaultDir = null, int defaultSegments = 8)
    {
        InitializeComponent();
        DirBox.Text = string.IsNullOrWhiteSpace(defaultDir) ? DefaultSaveDir() : defaultDir;
        SegPick.Value = Math.Clamp(defaultSegments, 1, 32);
        AlgoPick.ItemsSource = ChecksumService.Algorithms;
        AlgoPick.SelectedIndex = 0;   // SHA-256
        if (!string.IsNullOrWhiteSpace(presetUrl))
            UrlBox.Text = presetUrl;
    }

    public string Url { get; private set; } = string.Empty;
    public string SaveDirectory { get; private set; } = string.Empty;
    public string? FileName { get; private set; }
    public int Segments { get; private set; } = 8;
    public List<string> Mirrors { get; private set; } = new();
    public string? ChecksumAlgorithm { get; private set; }
    public string? ExpectedChecksum { get; private set; }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose download folder",
            AllowMultiple = false
        });
        if (folders.Count > 0)
            DirBox.Text = folders[0].Path.LocalPath;
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            UrlBox.BorderBrush = Brushes.IndianRed;
            return;
        }

        Url = url;
        SaveDirectory = string.IsNullOrWhiteSpace(DirBox.Text) ? DefaultSaveDir() : DirBox.Text!.Trim();
        FileName = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text!.Trim();
        Segments = (int)(SegPick.Value ?? 8);

        Mirrors = (MirrorsBox.Text ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => Uri.IsWellFormedUriString(l, UriKind.Absolute))
            .Distinct()
            .ToList();

        string hash = HashBox.Text?.Trim() ?? string.Empty;
        if (hash.Length > 0)
        {
            ExpectedChecksum = hash;
            ChecksumAlgorithm = AlgoPick.SelectedItem as string ?? "SHA-256";
        }

        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    internal static string DefaultSaveDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}