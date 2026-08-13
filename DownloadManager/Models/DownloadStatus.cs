namespace DownloadManager.Models;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Canceled,
    Error
}