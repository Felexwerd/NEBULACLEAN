namespace NebulaClean.Models;

public sealed class CleanupCategory
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string LocationLabel { get; init; } = "";
    public string[] Paths { get; init; } = [];
    public string? FilenamePrefix { get; init; }
    public bool IsSelected { get; set; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public string SizeText => FormatBytes(SizeBytes);
    public string CountText => $"{FileCount:N0} файлов";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
