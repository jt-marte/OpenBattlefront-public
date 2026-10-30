namespace OpenBattlefront.Core;

/// <summary>Finds the GameData folder (the one holding data\_lvl_pc) from whatever was dropped:
/// the game's root folder, GameData itself, the exe, or data\ or data\_lvl_pc.</summary>
public static class GameLocator
{
    public static string? FindGameData(string? dropped)
    {
        if (string.IsNullOrWhiteSpace(dropped)) return null;
        string full;
        try { full = Path.GetFullPath(dropped.Trim().Trim('"')); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        string? dir = File.Exists(full) ? Path.GetDirectoryName(full) : full;
        if (dir == null || !Directory.Exists(dir)) return null;

        var candidates = new List<string> { dir, Path.Combine(dir, "GameData") };
        var info = new DirectoryInfo(dir);
        if (info.Name.Equals("_lvl_pc", StringComparison.OrdinalIgnoreCase) && info.Parent?.Parent != null)
            candidates.Add(info.Parent.Parent.FullName);
        if (info.Name.Equals("data", StringComparison.OrdinalIgnoreCase) && info.Parent != null)
            candidates.Add(info.Parent.FullName);

        foreach (string candidate in candidates)
            if (IsGameData(candidate))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return null;
    }

    public static bool IsGameData(string dir) =>
        Directory.Exists(Path.Combine(dir, "data", "_lvl_pc")) &&
        StockLvls.Names.Any(n => StockLvls.FindIn(dir, n) != null);

    /// <summary>True when the path is (or is inside) the launcher's own install folder.</summary>
    public static bool IsInside(string path, string root)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }
}
