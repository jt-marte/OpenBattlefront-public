using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class GameFinderTests
{
    private sealed class FakeRegistry : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry Set(string key, string value, string data)
        {
            if (!_keys.TryGetValue(key, out var values)) _keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            values[value] = data;
            return this;
        }

        public string? GetString(string key, string value) =>
            _keys.TryGetValue(key, out var values) && values.TryGetValue(value, out var data) ? data : null;

        public IReadOnlyList<string> SubKeyNames(string key) =>
            _keys.Keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(k => k[(key.Length + 1)..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Escape(string path) => path.Replace(@"\", @"\\");

    [Fact]
    public void Every_steam_folder_counts_not_just_the_first()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("StaleSteam"));
        temp.FakeGame(@"MachineSteam\steamapps\common\Star Wars Battlefront II Classic\GameData");
        var registry = new FakeRegistry()
            .Set(@"HKCU\Software\Valve\Steam", "SteamPath", temp.Sub("StaleSteam").Replace('\\', '/'))
            .Set(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", temp.Sub("MachineSteam"));

        Assert.Equal(new[] { temp.Sub("StaleSteam"), temp.Sub("MachineSteam") }, SteamLocator.SteamRoots(registry));
        var found = new GameFinder(registry, () => Array.Empty<string>()).FindRegistered();
        Assert.Equal(temp.Sub(@"MachineSteam\steamapps\common\Star Wars Battlefront II Classic\GameData"), Assert.Single(found).GameData);
        Assert.Equal("Steam", found[0].Source);
    }

    [Fact]
    public void Libraries_come_from_both_vdf_files_in_either_format()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("NewLib"));
        Directory.CreateDirectory(temp.Sub("OldLib"));
        temp.File(@"Steam\steamapps\libraryfolders.vdf",
            "\"libraryfolders\" { \"0\" { \"path\" \"" + Escape(temp.Sub("NewLib")) + "\" \"apps\" { \"6060\" \"9530000000\" } } }");
        // Steam before 2021: numbered entries holding the path, in config\
        temp.File(@"Steam\config\libraryfolders.vdf",
            "\"LibraryFolders\" { \"TimeNextStatsReport\" \"1609459200\" \"1\" \"" + Escape(temp.Sub("OldLib")) + "\" }");

        Assert.Equal(new[] { temp.Sub("Steam"), temp.Sub("NewLib"), temp.Sub("OldLib") }, SteamLocator.Libraries(temp.Sub("Steam")));
    }

    [Fact]
    public void Old_vdf_numbers_are_libraries_but_app_sizes_are_not()
    {
        const string vdf = "\"LibraryFolders\" { \"1\" \"D:\\\\SteamLibrary\" \"2\" \"X:\\\\Games\\\\Steam\" \"6060\" \"9530000000\" }";
        Assert.Equal(new[] { @"D:\SteamLibrary", @"X:\Games\Steam" }, SteamLocator.ReadLibraryPaths(vdf));
    }

    [Fact]
    public void A_library_without_a_manifest_uses_the_usual_folder_names()
    {
        using var temp = new TempDir();
        temp.FakeGame(@"Steam\steamapps\common\Star Wars Battlefront II\GameData");
        Assert.Equal(new[] { temp.Sub(@"Steam\steamapps\common\Star Wars Battlefront II\GameData") },
            SteamLocator.FindGameData(new[] { temp.Sub("Steam") }));
    }

    [Fact]
    public void GOG_retail_and_installed_program_entries_are_found()
    {
        using var temp = new TempDir();
        temp.FakeGame(@"GOG Games\Star Wars Battlefront II\GameData");
        temp.FakeGame(@"LucasArts\SWBF2\GameData");
        temp.FakeGame(@"Elsewhere\BF2\GameData");
        var registry = new FakeRegistry()
            .Set(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1207659232", "gameName", "Star Wars: Battlefront II")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1207659232", "path", temp.Sub(@"GOG Games\Star Wars Battlefront II"))
            .Set(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1", "gameName", "Some other game")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1", "path", temp.Path)
            .Set(@"HKLM\SOFTWARE\WOW6432Node\LucasArts\Star Wars Battlefront II\1.0", "InstallPath", temp.Sub(@"LucasArts\SWBF2"))
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ABC}", "DisplayName", "Star Wars Battlefront 2")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ABC}", "InstallLocation", temp.Sub(@"Elsewhere\BF2"));

        var found = new GameFinder(registry, () => Array.Empty<string>()).FindRegistered();

        Assert.Equal(new[] { "GOG", "Retail", "Installed program" }, found.Select(f => f.Source));
        Assert.Equal(temp.Sub(@"GOG Games\Star Wars Battlefront II\GameData"), found[0].GameData);
    }

    [Fact]
    public void Drives_are_searched_in_the_usual_places_and_a_few_folders_down()
    {
        using var drive = new TempDir();
        drive.FakeGame(@"SteamLibrary\steamapps\common\Star Wars Battlefront II Classic\GameData");
        drive.FakeGame(@"Stuff\Old games\SW Battlefront 2 backup\GameData");
        // A Steam library a few folders down
        drive.FakeGame(@"Media\Steam\steamapps\common\Star Wars Battlefront II Classic\GameData");
        // Too deep, and in folders the search skips
        drive.FakeGame(@"A\B\C\D\Battlefront II\GameData");
        drive.FakeGame(@"$Recycle.Bin\S-1\Battlefront II\GameData");

        var found = new GameFinder(new FakeRegistry(), () => new[] { drive.Path }).ScanDrives();

        Assert.Equal(new[]
        {
            drive.Sub(@"SteamLibrary\steamapps\common\Star Wars Battlefront II Classic\GameData"),
            drive.Sub(@"Stuff\Old games\SW Battlefront 2 backup\GameData"),
            drive.Sub(@"Media\Steam\steamapps\common\Star Wars Battlefront II Classic\GameData"),
        }.OrderBy(p => p), found.Select(f => f.GameData).OrderBy(p => p));
        Assert.All(found, f => Assert.StartsWith("Found on ", f.Source));
    }

    [Fact]
    public void A_copy_found_twice_is_listed_once()
    {
        using var temp = new TempDir();
        temp.FakeGame(@"Steam\steamapps\common\Star Wars Battlefront II Classic\GameData");
        var registry = new FakeRegistry()
            .Set(@"HKCU\Software\Valve\Steam", "SteamPath", temp.Sub("Steam"))
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 6060", "InstallLocation",
                temp.Sub(@"Steam\steamapps\common\Star Wars Battlefront II Classic\"));

        var found = new GameFinder(registry, () => new[] { temp.Path }).FindAll();

        Assert.Equal("Steam", Assert.Single(found).Source);
    }

    [Fact]
    public void A_dropped_parent_folder_is_searched_below()
    {
        using var temp = new TempDir();
        temp.FakeGame(@"Games\Misc\Star Wars Battlefront II Classic\GameData");
        Directory.CreateDirectory(temp.Sub(@"Games\Other"));

        Assert.Null(GameLocator.FindGameData(temp.Sub("Games")));
        Assert.Equal(temp.Sub(@"Games\Misc\Star Wars Battlefront II Classic\GameData"),
            GameFinder.SearchBelow(temp.Sub("Games"), 3, TimeSpan.FromSeconds(5)));
        Assert.Null(GameFinder.SearchBelow(temp.Sub("Games"), 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_folder_with_the_GOG_installer_is_recognised()
    {
        using var temp = new TempDir();
        temp.File(@"Downloads\setup_star_wars_-_battlefront_2_1.1_(10935).exe");
        temp.File(@"Downloads\setup_star_wars_-_battlefront_2_1.1_(10935)-1.bin");
        Assert.True(GameFinder.HasGogInstaller(temp.Sub("Downloads")));
        Assert.False(GameFinder.HasGogInstaller(temp.Path));
    }
}
