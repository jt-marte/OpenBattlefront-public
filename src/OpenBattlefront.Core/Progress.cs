namespace OpenBattlefront.Core;

public sealed record TransferProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current, double BytesPerSecond)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;
}

/// <summary>One line of status for a long job: what it is doing, and how far along when known.</summary>
public sealed record StepUpdate(string Title, string? Detail = null, double? Fraction = null);

public static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
