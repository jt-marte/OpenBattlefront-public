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
    public void A_GOG_copy_is_named_as_such()
    {
        var checks = StockLvls.Names.Select(n => new LvlCheck(n, n + ".lvl",
            StockLvls.Gog.TryGetValue(n, out var gog) ? gog : StockLvls.Known[n][0], !StockLvls.Gog.ContainsKey(n))).ToList();
        var result = new ValidationResult("x", checks);
        Assert.True(result.IsGog);
        Assert.Equal(StockLvls.GogNotYet, GameSetup.Explain(result));

        checks[0] = checks[0] with { Sha256 = "1.3 patch" };
        Assert.False(new ValidationResult("x", checks).IsGog);
        Assert.True(StockLvls.IsGogInstaller(@"C:\Desktop\setup_star_wars_-_battlefront_2_1.1_(10935).exe"));
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
