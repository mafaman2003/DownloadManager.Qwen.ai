namespace DownloadManager.Models;

public sealed class SegmentInfo
{
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; } = -1;   // inclusive; -1 = read until EOF
    public long Downloaded { get; set; }
}