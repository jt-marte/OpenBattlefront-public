using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class GameLocatorTests
{
    [Fact]
    public void Finds_GameData_from_whatever_is_dropped()
    {
        using var temp = new TempDir();
        var (gameData, _) = temp.FakeGame();
        string root = temp.Sub("Game Root");

        Assert.Equal(gameData, GameLocator.FindGameData(root));
        Assert.Equal(gameData, GameLocator.FindGameData(gameData));
        Assert.Equal(gameData, GameLocator.FindGameData(gameData + "\\"));
        Assert.Equal(gameData, GameLocator.FindGameData(Path.Combine(gameData, "BattlefrontII.exe")));
        Assert.Equal(gameData, GameLocator.FindGameData(Path.Combine(gameData, "data")));
        Assert.Equal(gameData, GameLocator.FindGameData(Path.Combine(gameData, "data", "_lvl_pc")));
        Assert.Equal(gameData, GameLocator.FindGameData($"\"{root}\""));
    }

    [Fact]
    public void Rejects_folders_that_are_not_the_game()
    {
        using var temp = new TempDir();
        temp.File("Other\\readme.txt");
        temp.File("Empty lvl dir\\data\\_lvl_pc\\core.lvl");

        Assert.Null(GameLocator.FindGameData(temp.Sub("Other")));
        Assert.Null(GameLocator.FindGameData(temp.Sub("Empty lvl dir")));
        Assert.Null(GameLocator.FindGameData(temp.Sub("missing")));
        Assert.Null(GameLocator.FindGameData(""));
        Assert.Null(GameLocator.FindGameData(null));
    }

    [Fact]
    public void Knows_its_own_folder()
    {
        Assert.True(GameLocator.IsInside(@"C:\X\OpenBattlefront\Game", @"C:\X\OpenBattlefront"));
        Assert.True(GameLocator.IsInside(@"C:\X\OpenBattlefront", @"C:\X\OpenBattlefront\"));
        Assert.False(GameLocator.IsInside(@"C:\X\OpenBattlefront2\Game", @"C:\X\OpenBattlefront"));
    }
}

public class GameValidatorTests
{
    [Fact]
    public void Stock_lvls_pass()
    {
        using var temp = new TempDir();
        var (gameData, accepted) = temp.FakeGame();
        var result = GameValidator.Validate(gameData, accepted);
        Assert.True(result.Ok);
        Assert.All(result.Lvls, l => Assert.EndsWith(l.Name + ".lvl", l.Path));
    }

    [Fact]
    public void The_orig_backup_is_read_first()
    {
        using var temp = new TempDir();
        var (gameData, accepted) = temp.FakeGame();
        File.Move(Path.Combine(gameData, "data", "_lvl_pc", "shell.lvl"), Path.Combine(gameData, "data", "_lvl_pc", "shell.lvl.orig"));
        temp.File("Game Root\\GameData\\data\\_lvl_pc\\shell.lvl", "rebuilt by OpenSWBF2");

        var result = GameValidator.Validate(gameData, accepted);
        Assert.True(result.Ok);
        Assert.EndsWith("shell.lvl.orig", result.Lvls.Single(l => l.Name == "shell").Path);
    }

    [Fact]
    public void Modified_and_missing_lvls_are_named()
    {
        using var temp = new TempDir();
        var (gameData, accepted) = temp.FakeGame();
        temp.File("Game Root\\GameData\\data\\_lvl_pc\\common.lvl", "1.3 patch");
        File.Delete(Path.Combine(gameData, "data", "_lvl_pc", "mission.lvl"));

        var result = GameValidator.Validate(gameData, accepted);
        Assert.False(result.Ok);
        Assert.Equal(new[] { "common.lvl" }, result.Modified);
        Assert.Equal(new[] { "mission.lvl" }, result.Missing);
        Assert.Contains("missing", GameSetup.Explain(result));
    }

    [Fact]
    public void Modified_lvls_explain_how_to_restore()
    {
        using var temp = new TempDir();
        var (gameData, accepted) = temp.FakeGame();
        temp.File("Game Root\\GameData\\data\\_lvl_pc\\shell.lvl", "1.3 patch");
        string text = GameSetup.Explain(GameValidator.Validate(gameData, accepted));
        Assert.Contains("shell.lvl", text);
        Assert.Contains("Verify", text);
    }

    [Fact]
    public void A_GOG_copy_is_taken_like_Steam_s()
    {
        // GOG v1.1 (installer build 10935); its mission.lvl is Steam's
        var gog = new Dictionary<string, string>
        {
            ["shell"] = "7cde6a5fcb121653d4f647ce7a2b158305e88bad01bf55d14cfabcd4d0b3d00b",
            ["common"] = "7a6c9a52a76e5ba7e30ac7a9b5d3e2a1fbbd0f4567c4684361b138c0ce68b2f1",
            ["ingame"] = "338b9d1db70383b2eeed26b600b987f8e0bb6d9a4151c79eef6848edabecdbdd",
            ["mission"] = "f1000a76ec8d642b5e531833a6d53b9b5f37dbc0a3bbb4bfe605710fc66da10b",
        };
        foreach (var (name, hash) in gog)
            Assert.Contains(hash, StockLvls.Known[name]);
        Assert.True(StockLvls.IsGogInstaller(@"C:\Desktop\setup_star_wars_-_battlefront_2_1.1_(10935).exe"));
        Assert.False(StockLvls.IsGogInstaller(@"C:\GOG Games\Star Wars - Battlefront 2\GameData\BattlefrontII.exe"));
    }

    [Fact]
    public async Task A_build_that_cannot_take_the_stock_lvls_says_to_update()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        temp.File(@"L\Stock\data\_lvl_pc\shell.lvl", "gog shell");
        temp.File(@"L\Stock\data\_lvl_pc\common.lvl", "common");
        var manifest = new BuildManifest
        {
            Version = "0.2.4",
            StockLvls = new() { ["shell"] = new[] { TempDir.Sha("steam shell") }, ["common"] = new[] { TempDir.Sha("common") } },
        };
        Assert.Equal(new[] { "shell.lvl" }, LvlBuilder.Unsupported(layout, manifest));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LvlBuilder.BuildAsync(layout, temp.Sub("no build"), manifest, CancellationToken.None));
        Assert.Contains("0.2.4", e.Message);
        Assert.Contains("Update OpenSWBF2", e.Message);

        manifest.StockLvls["shell"] = new[] { TempDir.Sha("steam shell"), TempDir.Sha("gog shell").ToUpperInvariant() };
        Assert.Empty(LvlBuilder.Unsupported(layout, manifest));
    }

    [Fact]
    public void Manifest_hashes_add_to_the_known_ones()
    {
        var merged = StockLvls.Merge(new Dictionary<string, string[]> { ["shell"] = new[] { "abc" }, ["bogus"] = new[] { "def" } });
        Assert.Contains("abc", merged["shell"]);
        Assert.Contains(StockLvls.Known["shell"][0], merged["shell"]);
        Assert.False(merged.ContainsKey("bogus"));
    }
}

public class ImporterTests
{
    [Theory]
    [InlineData(@"_lvl_pc\core.lvl", true)]
    [InlineData(@"_lvl_pc\sound\gal.lvl", true)]
    [InlineData(@"_lvl_pc\movies\crawl.mvs", true)]
    [InlineData(@"_lvl_pc\shell.lvl", false)]
    [InlineData(@"_lvl_pc\SHELL.LVL", false)]
    [InlineData(@"_lvl_pc\mission.lvl", false)]
    [InlineData(@"_lvl_pc\shell.lvl.orig", false)]
    [InlineData(@"_lvl_pc\shell.lvl.cache", false)]
    [InlineData(@"_lvl_pc\vidmode.ini", false)]
    [InlineData(@"_lvl_pc\side\shell.lvl", true)]
    [InlineData("_lvl_pc/common.lvl", false)]
    [InlineData(@"_lvl_pc\user_script_9.lvl", true)]
    [InlineData(@"_lvl_pc\custom_gc_1.lvl", true)]
    [InlineData(@"_lvl_pc\user_script_10.lvl", false)]
    [InlineData(@"_lvl_pc\custom_gc_10.lvl", false)]
    [InlineData(@"_lvl_pc\v1.3patch_strings.lvl", false)]
    public void Copy_filter(string relative, bool copy) => Assert.Equal(copy, Importer.ShouldCopyDataFile(relative));

    [Fact]
    public void The_13_patch_is_never_imported()
    {
        using var temp = new TempDir();
        var (gameData, _) = temp.FakeGame();
        temp.File("Game Root\\GameData\\addon\\AAA-v1.3patch\\addme.script");
        temp.File("Game Root\\GameData\\addon\\AAA-v1.3patch\\data\\_lvl_pc\\patch.lvl");
        temp.File("Game Root\\GameData\\addon\\BF1\\addme.script");
        temp.File("Game Root\\GameData\\data\\_lvl_pc\\user_script_10.lvl");
        temp.File("Game Root\\GameData\\data\\_lvl_pc\\user_script_9.lvl");

        var plan = Importer.Plan(gameData, new InstallLayout(temp.Sub("L")), new ImportOptions());
        Assert.DoesNotContain(plan.Items, i => i.Display.Contains("AAA-v1.3patch") || i.Display.EndsWith("user_script_10.lvl"));
        Assert.Contains(plan.Items, i => i.Display == Path.Combine("AddOn", "BF1", "addme.script"));
        Assert.Contains(plan.Items, i => i.Display.EndsWith("user_script_9.lvl"));
        Assert.Equal(1, Importer.Preview(gameData).ModCount);
    }

    [Fact]
    public async Task Copies_data_stock_lvls_and_mods_but_never_profiles_then_resumes()
    {
        using var temp = new TempDir();
        var (gameData, _) = temp.FakeGame();
        temp.File("Game Root\\GameData\\SaveGames\\Jt.profile", "my profile");
        temp.File("Game Root\\GameData\\addon\\ABC\\addme.script", "mod");
        var layout = new InstallLayout(temp.Sub("Launcher"));

        var plan = Importer.Plan(gameData, layout, new ImportOptions());
        Assert.Equal(plan.TotalBytes, plan.PendingBytes);
        await Importer.CopyAsync(plan, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "core.lvl")));
        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "sound", "gal.lvl")));
        Assert.False(File.Exists(Path.Combine(layout.LvlDir, "shell.lvl")));
        Assert.Equal("shell stock", File.ReadAllText(Path.Combine(layout.StockLvlDir, "shell.lvl")));
        Assert.False(Directory.Exists(layout.SaveGames)); // a clean slate: no profiles or saves
        Assert.True(File.Exists(Path.Combine(layout.AddOn, "ABC", "addme.script")));
        Assert.False(File.Exists(Path.Combine(layout.Game, "BattlefrontII.exe")));
        Assert.Empty(Directory.GetFiles(layout.Root, "*.partial", SearchOption.AllDirectories));

        Assert.Equal(0, Importer.Plan(gameData, layout, new ImportOptions()).PendingBytes);

        temp.File("Game Root\\GameData\\data\\_lvl_pc\\core.lvl", "core v2");
        var again = Importer.Plan(gameData, layout, new ImportOptions());
        Assert.Single(again.Pending);
    }

    [Fact]
    public void Options_leave_out_mods()
    {
        using var temp = new TempDir();
        var (gameData, _) = temp.FakeGame();
        temp.File("Game Root\\GameData\\SaveGames\\Jt.profile");
        temp.File("Game Root\\GameData\\addon\\ABC\\addme.script");
        var plan = Importer.Plan(gameData, new InstallLayout(temp.Sub("L")), new ImportOptions(Mods: false));
        Assert.DoesNotContain(plan.Items, i => i.Display.StartsWith("SaveGames") || i.Display.StartsWith("AddOn"));
    }

    [Fact]
    public void Preview_counts_mods()
    {
        using var temp = new TempDir();
        var (gameData, _) = temp.FakeGame();
        temp.File("Game Root\\GameData\\SaveGames\\A.profile");
        temp.File("Game Root\\GameData\\SaveGames\\B.profile");
        temp.File("Game Root\\GameData\\addon\\ABC\\addme.script");
        temp.File("Game Root\\GameData\\addon\\XYZ\\addme.script");
        var preview = Importer.Preview(gameData);
        Assert.Equal(2, preview.ModCount);
        Assert.Equal("coresound".Length, preview.DataBytes);
    }
}
