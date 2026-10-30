using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenBattlefront.Core;

/// <summary>Finds an installed Steam copy of Star Wars Battlefront II (Classic, 2005) by reading
/// Steam's own library list. Nothing is changed in Steam.</summary>
public static partial class SteamLocator
{
    public const int AppId = 6060;
    public const string DefaultInstallDir = "Star Wars Battlefront II Classic";

    /// <summary>GameData folders of every Steam copy found, most likely first.</summary>
    public static IReadOnlyList<string> FindGameData()
    {
        var found = new List<string>();
        foreach (string library in Libraries(SteamRoot()))
        {
            string apps = Path.Combine(library, "steamapps");
            string installDir = DefaultInstallDir;
            string manifest = Path.Combine(apps, $"appmanifest_{AppId}.acf");
            if (File.Exists(manifest))
            {
                try { installDir = ReadInstallDir(File.ReadAllText(manifest)) ?? installDir; }
                catch (IOException) { }
            }
            string gameData = Path.Combine(apps, "common", installDir, "GameData");
            if (GameLocator.IsGameData(gameData) && !found.Contains(gameData, StringComparer.OrdinalIgnoreCase))
                found.Add(gameData);
        }
        return found;
    }

    public static string? SteamRoot()
    {
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(value) is string path && Directory.Exists(path)) return Path.GetFullPath(path.Replace('/', '\\'));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    /// <summary>The Steam folder itself plus every library in steamapps\libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> Libraries(string? steamRoot)
    {
        var libraries = new List<string>();
        if (steamRoot == null) return libraries;
        // The registry keeps Steam's path in lower case; the vdf has it as it is on disk, so it goes first.
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                foreach (string path in ReadLibraryPaths(File.ReadAllText(vdf)))
                    if (Directory.Exists(path) && !libraries.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
                        libraries.Add(Path.GetFullPath(path));
        }
        catch (IOException) { }
        if (!libraries.Contains(steamRoot, StringComparer.OrdinalIgnoreCase)) libraries.Insert(0, steamRoot);
        return libraries;
    }

    public static IEnumerable<string> ReadLibraryPaths(string vdf) =>
        LibraryPath().Matches(vdf).Select(m => Unescape(m.Groups[1].Value));

    public static string? ReadInstallDir(string acf)
    {
        var match = InstallDir().Match(acf);
        return match.Success ? Unescape(match.Groups[1].Value) : null;
    }

    private static string Unescape(string value) => value.Replace(@"\\", @"\");

    [GeneratedRegex(@"""path""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    [GeneratedRegex(@"""installdir""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDir();
}
