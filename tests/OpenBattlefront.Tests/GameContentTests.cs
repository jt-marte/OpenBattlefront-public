using System.Text.Json;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class GameContentTests
{
    [Theory]
    [InlineData("BF1", true)]
    [InlineData("DLC_XBOX", true)]
    [InlineData("dlc_xbox", true)]
    [InlineData("UPDATE_XBOX", false)]
    [InlineData("AAA-v1.3patch", false)]
    [InlineData("Remaster", false)]
    public void Only_the_pack_and_the_retired_Xbox_DLC_are_allowed(string folder, bool allowed) =>
        Assert.Equal(allowed, GameContent.IsAllowedFolder(folder));

    [Theory]
    [InlineData(@"C:\Users\Pat\AppData\Local\Temp\Temp1_OpenBattlefront-0.2.0.zip\", true)]
    [InlineData(@"C:\Users\Pat\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\Pat\Downloads\OpenBattlefront-0.2.0\", false)]
    [InlineData(@"C:\Users\Pat\AppData\Local\Temporary\OpenBattlefront\", false)]
    public void Started_straight_from_the_zip_means_a_temp_folder(string exeDir, bool fromTemp) =>
        Assert.Equal(fromTemp, GameContent.StartedFromTemp(exeDir, @"C:\Users\Pat\AppData\Local\Temp\"));

    [Fact]
    public void Packing_leaves_out_the_Xbox_DLC_the_Title_Update_the_patch_and_other_mods()
    {
        using var temp = new TempDir();
        string addOn = FakeAddOn(temp);
        temp.File(@"AddOn\DLC_XBOX\addme.script", "dlc");
        temp.File(@"AddOn\UPDATE_XBOX\addme.script", "update");
        temp.File(@"AddOn\AAA-v1.3patch\addme.script", "patch");
        temp.File(@"AddOn\Remaster\addme.script", "remaster");

        var manifest = ContentManifest.Create(addOn);
        Assert.Equal(new[] { "BF1" }, manifest.Folders);
        Assert.Equal(new[] { "AddOn/BF1/addme.script", "AddOn/BF1/data/_LVL_PC/bf1.lvl" },
            manifest.Files.Select(f => f.Path));
        Assert.Equal(TempDir.Sha("maps"), manifest.Files[1].Sha256);
        Assert.Equal(4, manifest.Files[1].Size);
        Assert.Matches("^[0-9a-f]{12}$", manifest.Version);

        // The version follows the files, not the time of packing.
        Assert.Equal(manifest.Version, ContentManifest.Create(addOn).Version);
        temp.File(@"AddOn\BF1\data\_LVL_PC\bf1.lvl", "new maps");
        Assert.NotEqual(manifest.Version, ContentManifest.Create(addOn).Version);
    }

    [Theory]
    [InlineData(@"BF1\setup.exe")]
    [InlineData(@"BF1\data\patch.dll")]
    [InlineData(@"BF1\tools\install.BAT")]
    public void Packing_refuses_installers(string file)
    {
        using var temp = new TempDir();
        string addOn = FakeAddOn(temp);
        temp.File(Path.Combine("AddOn", file));
        Assert.Throws<InvalidDataException>(() => ContentManifest.Create(addOn));
    }

    [Fact]
    public void Packing_needs_BF1()
    {
        using var temp = new TempDir();
        temp.File(@"AddOn\DLC_XBOX\addme.script");
        Assert.Throws<DirectoryNotFoundException>(() => ContentManifest.Create(temp.Sub("AddOn")));
    }

    [Theory]
    [InlineData("UPDATE_XBOX", "AddOn/UPDATE_XBOX/addme.script")]
    [InlineData("AAA-v1.3patch", "AddOn/AAA-v1.3patch/addme.script")]
    [InlineData("BF1", "AddOn/UPDATE_XBOX/addme.script")]
    [InlineData("BF1", "AddOn/BF1/setup.exe")]
    [InlineData("BF1", "AddOn/BF1/../../evil.txt")]
    [InlineData("BF1", "AddOn/BF1/..")]
    [InlineData("BF1", "C:/Windows/evil.txt")]
    [InlineData("BF1", "/AddOn/BF1/addme.script")]
    [InlineData("BF1", "AddOn\\BF1\\addme.script")]
    [InlineData("BF1", "data/_lvl_pc/shell.lvl")]
    [InlineData("BF1", "AddOn/addme.script")]
    public void A_manifest_outside_the_allowed_folders_is_refused(string folder, string path)
    {
        Assert.Equal("AddOn/BF1/addme.script", ContentManifest.Parse(ManifestJson("BF1", "AddOn/BF1/addme.script")).Files.Single().Path);
        Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(ManifestJson(folder, path)));
    }

    [Fact]
    public async Task Installs_the_included_content_and_saves_the_version()
    {
        using var temp = new TempDir();
        var (setup, _) = Setup(temp, FakeContent(temp));
        Assert.True(setup.NeedsContent);

        Assert.True(await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.Equal("bf1", File.ReadAllText(Path.Combine(setup.Layout.AddOn, "BF1", "addme.script")));
        Assert.Equal("maps", File.ReadAllText(Path.Combine(setup.Layout.AddOn, "BF1", "data", "_LVL_PC", "bf1.lvl")));
        Assert.False(Directory.Exists(Path.Combine(setup.Layout.Temp, "content")));

        string version = setup.Content.Load()!.Version;
        Assert.Equal(version, setup.State.ContentVersion);
        Assert.Equal(version, LauncherState.Load(temp.Sub(@"L\launcher.json")).ContentVersion);

        // Once is enough, until a launcher brings another version.
        Assert.False(setup.NeedsContent);
        Assert.False(await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        temp.File(@"Content\AddOn\BF1\addme.script", "bf1 2");
        ContentManifest.Create(temp.Sub(@"Content\AddOn")).Save(temp.Sub("Content"));
        Assert.True(setup.NeedsContent);
        Assert.True(await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.Equal("bf1 2", File.ReadAllText(Path.Combine(setup.Layout.AddOn, "BF1", "addme.script")));
    }

    [Fact]
    public async Task An_older_package_s_Xbox_DLC_is_skipped()
    {
        using var temp = new TempDir();
        string content = FakeContent(temp);
        temp.File(@"Content\AddOn\DLC_XBOX\addme.script", "dlc");
        var manifest = ContentManifest.Load(content);
        manifest.Folders.Add("DLC_XBOX");
        manifest.Files.Add(new ManifestFile { Path = "AddOn/DLC_XBOX/addme.script", Size = 3, Sha256 = TempDir.Sha("dlc") });
        manifest.Save(content);
        var (setup, _) = Setup(temp, content);

        Assert.True(await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.Equal("bf1", File.ReadAllText(Path.Combine(setup.Layout.AddOn, "BF1", "addme.script")));
        Assert.False(Directory.Exists(Path.Combine(setup.Layout.AddOn, "DLC_XBOX")));
        Assert.Equal(manifest.Version, setup.State.ContentVersion);
    }

    [Fact]
    public void The_launcher_s_own_Xbox_DLC_is_removed_once()
    {
        using var temp = new TempDir();
        var (setup, removed) = Setup(temp, FakeContent(temp));
        temp.File(@"L\Game\AddOn\DLC_XBOX\addme.script", "dlc");
        temp.File(@"L\Game\AddOn_disabled\DLC_XBOX\addme.script", "dlc off");
        setup.State.ContentVersion = "0123456789ab"; // an older launcher put its content in place

        var gone = setup.Content.RemoveRetired(setup.Layout, setup.State);
        Assert.Equal(new[] { Path.Combine(setup.Layout.AddOn, "DLC_XBOX"), Path.Combine(setup.Layout.AddOnDisabled, "DLC_XBOX") }, gone);
        Assert.Equal(gone, removed);
        Assert.Equal(new[] { "DLC_XBOX" }, setup.State.RetiredContentRemoved);

        // One the player adds afterwards stays.
        temp.File(@"L\Game\AddOn\DLC_XBOX\addme.script", "theirs");
        Assert.Empty(setup.Content.RemoveRetired(setup.Layout, setup.State));
        Assert.True(File.Exists(temp.Sub(@"L\Game\AddOn\DLC_XBOX\addme.script")));
    }

    [Fact]
    public void An_Xbox_DLC_the_launcher_never_installed_stays()
    {
        using var temp = new TempDir();
        var (setup, removed) = Setup(temp, FakeContent(temp));
        temp.File(@"L\Game\AddOn\DLC_XBOX\addme.script", "theirs");

        Assert.Empty(setup.Content.RemoveRetired(setup.Layout, setup.State)); // no content version yet
        setup.State.ContentVersion = "0123456789ab";
        Assert.Empty(setup.Content.RemoveRetired(setup.Layout, setup.State));
        Assert.Empty(removed);
        Assert.True(File.Exists(temp.Sub(@"L\Game\AddOn\DLC_XBOX\addme.script")));
    }

    [Fact]
    public async Task Nothing_happens_without_a_Content_folder_or_before_the_import()
    {
        using var temp = new TempDir();
        var (setup, _) = Setup(temp, temp.Sub("NoContent"));
        Assert.False(setup.NeedsContent);
        Assert.False(await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.False(Directory.Exists(setup.Layout.AddOn));
        Assert.Null(setup.State.ContentVersion);

        var (early, _) = Setup(temp, FakeContent(temp), importComplete: false);
        Assert.False(early.NeedsContent);
        Assert.False(await early.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.False(Directory.Exists(early.Layout.AddOn));
    }

    [Fact]
    public async Task A_damaged_file_is_refused_before_anything_is_replaced()
    {
        using var temp = new TempDir();
        string content = FakeContent(temp);
        temp.File(@"Content\AddOn\BF1\data\_LVL_PC\bf1.lvl", "mapz");
        var (setup, removed) = Setup(temp, content);
        temp.File(@"L\Game\AddOn\BF1\addme.script", "old");

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.Contains("AddOn/BF1/data/_LVL_PC/bf1.lvl", e.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(setup.Layout.AddOn, "BF1", "addme.script")));
        Assert.False(Directory.Exists(Path.Combine(setup.Layout.AddOn, "DLC_XBOX")));
        Assert.Empty(removed);
        Assert.Null(setup.State.ContentVersion);
        Assert.False(Directory.Exists(Path.Combine(setup.Layout.Temp, "content")));
    }

    [Fact]
    public async Task A_path_that_climbs_out_is_refused()
    {
        using var temp = new TempDir();
        string content = FakeContent(temp);
        temp.File(@"Content\evil.txt", "evil");
        var manifest = ContentManifest.Load(content);
        File.WriteAllText(Path.Combine(content, ContentManifest.FileName),
            manifest.ToJson().Replace("AddOn/BF1/addme.script", "AddOn/BF1/../../evil.txt"));
        var (setup, _) = Setup(temp, content);

        Assert.True(setup.NeedsContent); // so that the install reports it
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None));
        Assert.False(Directory.Exists(setup.Layout.AddOn));
        Assert.False(File.Exists(Path.Combine(setup.Layout.Game, "evil.txt")));
        Assert.Null(setup.State.ContentVersion);
    }

    [Fact]
    public async Task An_existing_folder_is_replaced()
    {
        using var temp = new TempDir();
        var (setup, removed) = Setup(temp, FakeContent(temp));
        temp.File(@"L\Game\AddOn\BF1\addme.script", "old");
        temp.File(@"L\Game\AddOn\BF1\data\_LVL_PC\old.lvl", "old maps");

        await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None);
        Assert.Equal(new[] { Path.Combine(setup.Layout.AddOn, "BF1") }, removed);
        Assert.Equal("bf1", File.ReadAllText(Path.Combine(setup.Layout.AddOn, "BF1", "addme.script")));
        Assert.False(File.Exists(Path.Combine(setup.Layout.AddOn, "BF1", "data", "_LVL_PC", "old.lvl")));
    }

    [Fact]
    public async Task A_switched_off_folder_is_updated_and_stays_off()
    {
        using var temp = new TempDir();
        var (setup, removed) = Setup(temp, FakeContent(temp));
        temp.File(@"L\Game\AddOn_disabled\BF1\addme.script", "old");

        await setup.InstallContentAsync(new Progress<StepUpdate>(), CancellationToken.None);
        Assert.Equal(new[] { Path.Combine(setup.Layout.AddOnDisabled, "BF1") }, removed);
        Assert.Equal("bf1", File.ReadAllText(Path.Combine(setup.Layout.AddOnDisabled, "BF1", "addme.script")));
        Assert.False(Directory.Exists(Path.Combine(setup.Layout.AddOn, "BF1")));
    }

    [Fact]
    public void The_Mods_page_marks_the_included_folders()
    {
        using var temp = new TempDir();
        var state = new LauncherState { InstallRoot = temp.Sub("L") };
        temp.File(@"L\Game\AddOn_disabled\BF1\addme.script");
        temp.File(@"L\Game\AddOn\DLC_XBOX\addme.script");
        temp.File(@"L\Game\AddOn\UPDATE_XBOX\addme.script");
        temp.File(@"L\Game\AddOn\ABC\addme.script");
        var mods = new ModManager(state.Layout);

        // Not before the launcher has put its content in place: a BF1 the player added stays theirs.
        Assert.All(mods.List(GameContent.Installed(state)), m => Assert.False(m.IsIncluded));

        state.ContentVersion = "0123456789ab";
        var list = mods.List(GameContent.Installed(state));
        Assert.Equal(new[] { "BF1" }, list.Where(m => m.IsIncluded).Select(m => m.Name));
        var bf1 = list.Single(m => m.Name == "BF1");
        Assert.False(bf1.Enabled);
        Assert.True(mods.SetEnabled(bf1, true).IsIncluded); // the switch keeps working
    }

    /// <summary>AddOn\BF1 (two files).</summary>
    private static string FakeAddOn(TempDir temp, string at = "AddOn")
    {
        temp.File($@"{at}\BF1\addme.script", "bf1");
        temp.File($@"{at}\BF1\data\_LVL_PC\bf1.lvl", "maps");
        return temp.Sub(at);
    }

    /// <summary>A Content folder as the launcher package carries it, with its content.json.</summary>
    private static string FakeContent(TempDir temp)
    {
        string addOn = FakeAddOn(temp, @"Content\AddOn");
        ContentManifest.Create(addOn).Save(temp.Sub("Content"));
        return temp.Sub("Content");
    }

    private static (GameSetup Setup, List<string> Removed) Setup(TempDir temp, string content, bool importComplete = true)
    {
        var removed = new List<string>();
        var gameContent = new GameContent(content, path =>
        {
            removed.Add(path);
            Directory.Delete(path, recursive: true);
        });
        var state = new LauncherState { InstallRoot = temp.Sub("L"), ImportComplete = importComplete };
        return (new GameSetup(state, temp.Sub(@"L\launcher.json"), new Version(0, 1, 0), gameContent), removed);
    }

    private static string ManifestJson(string folder, string path) => JsonSerializer.Serialize(new
    {
        schema = 1,
        version = "0123456789ab",
        folders = new[] { folder },
        files = new[] { new { path, size = 1, sha256 = TempDir.Sha("x") } },
    });
}
