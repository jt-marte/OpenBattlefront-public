using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

/// <summary>Finds installed Steam copies of Star Wars Battlefront II (Classic, 2005) by reading
/// Steam's own library lists. Nothing is changed in Steam.</summary>
public static partial class SteamLocator
{
    public const int AppId = 6060;
    public const string DefaultInstallDir = "Star Wars Battlefront II Classic";

    /// <summary>The folder names Steam has used for the game, the current one first.</summary>
    private static readonly string[] InstallDirs = { DefaultInstallDir, "Star Wars Battlefront II" };

    /// <summary>GameData folders of every Steam copy on this PC, most likely first.</summary>
    public static IReadOnlyList<string> FindGameData() => FindGameData(SteamRoots(new WindowsRegistry()));

    /// <summary>GameData folders of every Steam copy in the libraries of these Steam folders.</summary>
    public static IReadOnlyList<string> FindGameData(IEnumerable<string> steamRoots)
    {
        var found = new List<string>();
        var libraries = new List<string>();
        foreach (string root in steamRoots)
            foreach (string library in Libraries(root))
                if (!libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                    libraries.Add(library);

        foreach (string library in libraries)
        {
            string apps = Path.Combine(library, "steamapps");
            var dirs = new List<string>();
            string manifest = Path.Combine(apps, $"appmanifest_{AppId}.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    if (ReadInstallDir(File.ReadAllText(manifest)) is string dir) dirs.Add(dir);
                }
                catch (IOException) { }
            }
            dirs.AddRange(InstallDirs);
            foreach (string dir in dirs)
            {
                string gameData = Path.Combine(apps, "common", dir, "GameData");
                if (GameLocator.IsGameData(gameData) && !found.Contains(gameData, StringComparer.OrdinalIgnoreCase))
                    found.Add(gameData);
            }
        }
        return found;
    }

    /// <summary>The Steam folder found through the registry, or null.</summary>
    public static string? SteamRoot() => SteamRoots(new WindowsRegistry()).FirstOrDefault();

    /// <summary>Every Steam folder the registry names that exists: the user's, then the
    /// machine-wide ones (a stale user entry no longer hides a good machine-wide one).</summary>
    public static IReadOnlyList<string> SteamRoots(IRegistryView registry)
    {
        var roots = new List<string>();
        foreach (var (key, value) in new[]
        {
            (@"HKCU\Software\Valve\Steam", "SteamPath"),
            (@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (@"HKLM\SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            string? path = registry.GetString(key, value);
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\')));
                if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return roots;
    }

    /// <summary>The Steam folder itself plus every library listed in steamapps\libraryfolders.vdf
    /// or config\libraryfolders.vdf, in the current or the pre-2021 format.</summary>
    public static IReadOnlyList<string> Libraries(string? steamRoot)
    {
        var libraries = new List<string>();
        if (steamRoot == null) return libraries;
        // The registry keeps Steam's path in lower case; the vdf has it as it is on disk, so it goes first.
        foreach (string vdf in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                 })
        {
            try
            {
                if (File.Exists(vdf))
                    foreach (string path in ReadLibraryPaths(File.ReadAllText(vdf)))
                        if (Directory.Exists(path) && !libraries.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
                            libraries.Add(Path.GetFullPath(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (!libraries.Contains(steamRoot, StringComparer.OrdinalIgnoreCase)) libraries.Insert(0, steamRoot);
        return libraries;
    }

    /// <summary>Library folders in a libraryfolders.vdf: "path" entries (current format) and
    /// numbered entries holding a drive path (the format before 2021).</summary>
    public static IEnumerable<string> ReadLibraryPaths(string vdf) =>
        LibraryPath().Matches(vdf).Concat(OldLibraryPath().Matches(vdf))
            .OrderBy(m => m.Index)
            .Select(m => Unescape(m.Groups[1].Value));

    public static string? ReadInstallDir(string acf)
    {
        var match = InstallDir().Match(acf);
        return match.Success ? Unescape(match.Groups[1].Value) : null;
    }

    private static string Unescape(string value) => value.Replace(@"\\", @"\");

    [GeneratedRegex(@"""path""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    [GeneratedRegex(@"""\d+""\s+""([A-Za-z]:(?:[^""\\]|\\.)*)""")]
    private static partial Regex OldLibraryPath();

    [GeneratedRegex(@"""installdir""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDir();
}
