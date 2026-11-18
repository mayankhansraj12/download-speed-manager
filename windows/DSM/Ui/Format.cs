using Dsm.Engine;

namespace Dsm.Ui;

internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long? bytes)
    {
        if (bytes is not long value) return "Unknown";
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} B" : $"{size:0.##} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Eta(double? seconds)
    {
        if (seconds is not double s || double.IsInfinity(s)) return "";
        var t = TimeSpan.FromSeconds(Math.Ceiling(s));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds:00} s";
        return $"{t.Seconds} s";
    }

    public static string Percent(DownloadItem d) => d.Fraction is double f ? $"{f * 100:0.0}%" : "";

    public static string Status(DownloadItem d) => d.Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Connecting => "Connecting…",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Error => "Error",
        DownloadStatus.Cancelled => "Cancelled",
        _ => d.Status.ToString(),
    };

    public static string Date(DateTime? date) => date?.ToString("g") ?? "";
}
