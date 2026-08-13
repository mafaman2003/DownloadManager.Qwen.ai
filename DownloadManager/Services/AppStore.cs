using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DownloadManager.Models;

namespace DownloadManager.Services;

public static class AppStore
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvaloniaDownloadManager");

    private static string DownloadsPath => Path.Combine(DataDirectory, "downloads.json");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static List<DownloadItem> LoadDownloads()
    {
        try
        {
            if (File.Exists(DownloadsPath))
                return JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(DownloadsPath), Options) ?? new();
        }
        catch { }
        return new();
    }

    public static void SaveDownloads(IEnumerable<DownloadItem> items)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(DownloadsPath, JsonSerializer.Serialize(items.ToList(), Options));
        }
        catch { }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch { }
    }
}