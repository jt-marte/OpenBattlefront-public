using System.Diagnostics;

namespace OpenBattlefront.Core;

/// <summary>A copy of the game found on this PC: its GameData folder and where it was found.</summary>
public sealed record FoundCopy(string GameData, string Source);

/// <summary>Looks for the player's copy of Star Wars Battlefront II (2005) wherever it is: every
/// Steam library, the GOG and LucasArts registry keys, Windows' installed-programs list, and the
/// usual folders on every fixed drive (C:, D:, X: …), then a few folders down. Nothing is changed.</summary>
public sealed class GameFinder
{
    /// <summary>Names the game's folder goes by (Steam today and before, GOG, retail).</summary>
    public static readonly string[] FolderNames =
    {
        "Star Wars Battlefront II Classic", "Star Wars Battlefront II", "Star Wars - Battlefront II",
        "Star Wars - Battlefront 2", "STAR WARS Battlefront II", "Battlefront II",
    };

    /// <summary>Folders the drive search never goes into: Windows' own, and ones too big to be
    /// worth it.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery",
        "PerfLogs", "$WinREAgent", "$SysReset", "Config.Msi", "AppData", "node_modules", ".git", "MSOCache",
        "Intel", "AMD", "NVIDIA", "OneDriveTemp",
    };

    private static readonly string[] WellKnown =
    {
        @"SteamLibrary\steamapps\common", @"Steam\steamapps\common", @"Program Files (x86)\Steam\steamapps\common",
        @"Program Files\Steam\steamapps\common", @"Games\Steam\steamapps\common", @"Games\SteamLibrary\steamapps\common",
        @"GOG Games", @"Program Files (x86)\GOG Galaxy\Games", @"Program Files\GOG Galaxy\Games", @"GOG Galaxy\Games",
        @"Program Files (x86)\LucasArts", @"Program Files\LucasArts", @"Games", "",
    };

    private readonly IRegistryView _registry;
    private readonly Func<IEnumerable<string>> _drives;

    public GameFinder(IRegistryView registry, Func<IEnumerable<string>>? drives = null)
    {
        _registry = registry;
        _drives = drives ?? FixedDrives;
    }

    public static GameFinder ForThisPc() => new(new WindowsRegistry());

    /// <summary>How long the search may spend on one drive.</summary>
    public TimeSpan DriveBudget { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How many folders below a drive's root the search goes.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Every ready fixed drive. Removable and network drives are left to Browse: they can
    /// be slow or asleep.</summary>
    public static IEnumerable<string> FixedDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch (IOException) { ready = false; }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>The registry's copies, then the drives', each copy once.</summary>
    public IReadOnlyList<FoundCopy> FindAll(CancellationToken ct = default) =>
        Merge(FindRegistered(ct), ScanDrives(ct));

    /// <summary>Copies the registry knows about: Steam (every Steam folder and library), GOG,
    /// LucasArts retail and Windows' installed-programs list.</summary>
    public IReadOnlyList<FoundCopy> FindRegistered(CancellationToken ct = default)
    {
        var found = new List<FoundCopy>();
        foreach (string gameData in SteamLocator.FindGameData(SteamLocator.SteamRoots(_registry)))
            found.Add(new FoundCopy(gameData, "Steam"));
        ct.ThrowIfCancellationRequested();

        foreach (string root in new[] { @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games", @"HKLM\SOFTWARE\GOG.com\Games" })
            foreach (string id in _registry.SubKeyNames(root))
            {
                string key = root + "\\" + id;
                string name = _registry.GetString(key, "gameName") ?? _registry.GetString(key, "GAMENAME") ?? "";
                string exe = _registry.GetString(key, "exe") ?? _registry.GetString(key, "EXE") ?? "";
                if (!IsTheGame(name) && !exe.EndsWith("BattlefrontII.exe", StringComparison.OrdinalIgnoreCase)) continue;
                AddFolder(found, _registry.GetString(key, "path") ?? _registry.GetString(key, "PATH"), "GOG");
            }
        ct.ThrowIfCancellationRequested();

        foreach (string root in new[] { @"HKLM\SOFTWARE\WOW6432Node\LucasArts", @"HKLM\SOFTWARE\LucasArts" })
            foreach (string game in _registry.SubKeyNames(root).Where(IsTheGame))
            {
                string key = root + "\\" + game;
                AddFolder(found, _registry.GetString(key, "InstallPath") ?? _registry.GetString(key, "Install Dir"), "Retail");
                foreach (string version in _registry.SubKeyNames(key))
                    AddFolder(found, _registry.GetString(key + "\\" + version, "InstallPath") ??
                                     _registry.GetString(key + "\\" + version, "Install Dir"), "Retail");
            }
        ct.ThrowIfCancellationRequested();

        foreach (string root in new[]
                 {
                     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
            foreach (string id in _registry.SubKeyNames(root))
            {
                string key = root + "\\" + id;
                string name = _registry.GetString(key, "DisplayName") ?? "";
                bool steam = id.Equals("Steam App " + SteamLocator.AppId, StringComparison.OrdinalIgnoreCase);
                if (!steam && !IsTheGame(name)) continue;
                AddFolder(found, _registry.GetString(key, "InstallLocation"), steam ? "Steam" : "Installed program");
            }
        return Merge(found);
    }

    /// <summary>Looks on every fixed drive: the usual Steam, GOG and retail folders first, then up
    /// to MaxDepth folders down for a folder named like the game (or a Steam library).</summary>
    public IReadOnlyList<FoundCopy> ScanDrives(CancellationToken ct = default)
    {
        var found = new List<FoundCopy>();
        foreach (string root in _drives())
        {
            ct.ThrowIfCancellationRequested();
            string label = "Found on " + root.TrimEnd('\\');
            foreach (string place in WellKnown)
                foreach (string name in FolderNames)
                    AddFolder(found, Path.Combine(root, place, name), label);

            var clock = Stopwatch.StartNew();
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0 && clock.Elapsed < DriveBudget)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = queue.Dequeue();
                foreach (string child in SubDirectories(dir))
                {
                    string name = Path.GetFileName(child);
                    if (name.Contains("battlefront", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFolder(found, child, label);
                        continue;
                    }
                    if (name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string folder in FolderNames)
                            AddFolder(found, Path.Combine(child, "common", folder), label);
                        continue;
                    }
                    if (depth + 1 < MaxDepth) queue.Enqueue((child, depth + 1));
                }
            }
        }
        return Merge(found);
    }

    /// <summary>The game's GameData anywhere up to <paramref name="depth"/> folders below
    /// <paramref name="dir"/> (a dropped parent folder such as X:\Games or steamapps\common),
    /// folders named like the game first; null when there is none or time runs out.</summary>
    public static string? SearchBelow(string dir, int depth, TimeSpan budget, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var level = new List<string> { dir };
        for (int d = 0; d < depth && level.Count > 0 && clock.Elapsed < budget; d++)
        {
            var next = new List<string>();
            foreach (string parent in level)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget) break;
                next.AddRange(SubDirectories(parent));
            }
            foreach (string child in next.OrderBy(c => Path.GetFileName(c).Contains("battlefront", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                string? gameData = GameLocator.FindGameData(child);
                if (gameData != null) return gameData;
            }
            level = next;
        }
        return null;
    }

    /// <summary>True when the folder holds GOG's offline installer for the game.</summary>
    public static bool HasGogInstaller(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.exe").Any(StockLvls.IsGogInstaller);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsTheGame(string name) =>
        name.Contains("Battlefront II", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Battlefront 2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Each GameData once (case and trailing slashes ignored), in the order found.</summary>
    public static IReadOnlyList<FoundCopy> Merge(params IEnumerable<FoundCopy>[] lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<FoundCopy>();
        foreach (var list in lists)
            foreach (var copy in list)
                if (seen.Add(Path.TrimEndingDirectorySeparator(copy.GameData)))
                    merged.Add(copy);
        return merged;
    }

    private static void AddFolder(List<FoundCopy> found, string? folder, string source)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string? gameData;
        try { gameData = Directory.Exists(folder) ? GameLocator.FindGameData(folder) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { gameData = null; }
        if (gameData != null) found.Add(new FoundCopy(gameData, source));
    }

    private static IEnumerable<string> SubDirectories(string dir)
    {
        DirectoryInfo[] children;
        try
        {
            children = new DirectoryInfo(dir).GetDirectories();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }
        foreach (var child in children)
        {
            FileAttributes attributes;
            try { attributes = child.Attributes; }
            catch (IOException) { continue; }
            if (Skip.Contains(child.Name)) continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) continue;
            yield return child.FullName;
        }
    }
}
