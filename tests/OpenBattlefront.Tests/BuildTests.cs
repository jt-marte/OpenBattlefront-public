using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class BuildManifestTests
{
    [Theory]
    [InlineData("BattlefrontII.exe", true)]
    [InlineData("dxvk/d3d9.dll", true)]
    [InlineData("data/scripts/shell/ifs_start.lua", true)]
    [InlineData("../evil.dll", false)]
    [InlineData("dxvk/../../evil.dll", false)]
    [InlineData("/abs.dll", false)]
    [InlineData("C:/abs.dll", false)]
    [InlineData("dxvk\\d3d9.dll", false)]
    [InlineData("a//b", false)]
    [InlineData("", false)]
    public void Safe_paths(string path, bool safe) => Assert.Equal(safe, BuildManifest.IsSafeRelative(path));

    [Fact]
    public void Runtime_files_are_what_goes_next_to_the_game()
    {
        using var temp = new TempDir();
        string zip = temp.Package("0.1.0", TempDir.BuildFiles());
        var manifest = new BuildStore(new InstallLayout(temp.Sub("L"))).Unpack(zip, null, CancellationToken.None);
        Assert.Equal(new[] { "BattlefrontII.exe", "avcodec-62.dll", "dxvk/d3d9.dll" }, manifest.RuntimeFiles.Select(f => f.Path));
    }

    [Fact]
    public void Rejects_bad_manifests()
    {
        Assert.Throws<InvalidDataException>(() => BuildManifest.Parse("not json"));
        Assert.Throws<InvalidDataException>(() => BuildManifest.Parse("""{"schema":2,"version":"1.0.0","platform":"x64"}"""));
        Assert.Throws<InvalidDataException>(() => BuildManifest.Parse("""{"schema":1,"version":"1.0.0","platform":"x86","files":[]}"""));
        Assert.Throws<InvalidDataException>(() => BuildManifest.Parse(
            """{"schema":1,"version":"1.0.0","platform":"x64","files":[{"path":"../x","size":1,"sha256":"00"}]}"""));
    }

    [Fact]
    public void Reads_the_real_package_format()
    {
        // The shape tools\package-release.ps1 writes with Windows PowerShell's ConvertTo-Json.
        string json = """
        {
            "schema":  1,
            "name":  "OpenSWBF2",
            "version":  "0.1.0",
            "platform":  "x64",
            "commit":  "6cfec4d",
            "dirty":  false,
            "built":  "2026-10-30T21:00:00Z",
            "minLauncherVersion":  "0.1.0",
            "exe":  "BattlefrontII.exe",
            "lvlBuilder":  "LuaCompiler.exe",
            "index":  "data/scripts/index.json",
            "stockLvls":  { "shell":  [ "82335ab55e17689cd620ba174568e6a5481d4523b821549aa153a6b88a831009" ] },
            "files":  [
                { "path":  "BattlefrontII.exe", "size":  6, "sha256":  "0000000000000000000000000000000000000000000000000000000000000000" },
                { "path":  "LuaCompiler.exe", "size":  6, "sha256":  "0000000000000000000000000000000000000000000000000000000000000000" },
                { "path":  "data/scripts/index.json", "size":  2, "sha256":  "0000000000000000000000000000000000000000000000000000000000000000" }
            ]
        }
        """;
        var manifest = BuildManifest.Parse(json);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Single(manifest.StockLvls["shell"]);
        Assert.True(manifest.NeedsNewerLauncher(new Version(0, 0, 9)));
        Assert.False(manifest.NeedsNewerLauncher(new Version(0, 1, 0)));
    }
}

public class BuildStoreTests
{
    [Fact]
    public void Unpacks_verifies_and_deploys()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        var store = new BuildStore(layout);
        var manifest = store.Unpack(temp.Package("0.1.0", TempDir.BuildFiles()), null, CancellationToken.None);

        Assert.Equal("0.1.0", manifest.Version);
        Assert.Empty(manifest.Verify(store.Dir("0.1.0")));
        Assert.False(Directory.Exists(store.Dir("0.1.0") + ".tmp"));

        store.Deploy(manifest);
        Assert.Equal("game 1", File.ReadAllText(Path.Combine(layout.Game, "BattlefrontII.exe")));
        Assert.Equal("dxvk 1", File.ReadAllText(Path.Combine(layout.Game, "dxvk", "d3d9.dll")));
        Assert.False(File.Exists(Path.Combine(layout.Game, "LuaCompiler.exe")));
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "data")));
    }

    [Fact]
    public void Refuses_files_the_manifest_does_not_list()
    {
        using var temp = new TempDir();
        var store = new BuildStore(new InstallLayout(temp.Sub("L")));
        string zip = temp.Package("0.1.0", TempDir.BuildFiles(), extraEntry: "data/_lvl_pc/shell.lvl");
        var e = Assert.Throws<InvalidDataException>(() => store.Unpack(zip, null, CancellationToken.None));
        Assert.Contains("shell.lvl", e.Message);
    }

    [Fact]
    public void Refuses_a_damaged_package()
    {
        using var temp = new TempDir();
        var files = TempDir.BuildFiles();
        string good = temp.Package("0.1.0", files);
        // Same manifest, different exe bytes.
        string manifest;
        using (var zip = System.IO.Compression.ZipFile.OpenRead(good))
        using (var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open()))
            manifest = reader.ReadToEnd();
        File.Delete(good);
        files["BattlefrontII.exe"] = "game X";
        string bad = temp.Package("0.1.0", files, manifestOverride: manifest);

        var store = new BuildStore(new InstallLayout(temp.Sub("L")));
        var e = Assert.Throws<InvalidDataException>(() => store.Unpack(bad, null, CancellationToken.None));
        Assert.Contains("BattlefrontII.exe", e.Message);
        Assert.False(Directory.Exists(store.Dir("0.1.0")));
    }

    [Fact]
    public void Prune_keeps_the_named_builds()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        foreach (string v in new[] { "0.1.0", "0.2.0", "0.3.0" }) Directory.CreateDirectory(Path.Combine(layout.Builds, v));
        new BuildStore(layout).Prune("0.3.0", "0.2.0", null);
        Assert.Equal(new[] { "0.2.0", "0.3.0" }, Directory.GetDirectories(layout.Builds).Select(Path.GetFileName).Order());
    }
}

public class ReleaseClientTests
{
    [Fact]
    public void Finds_the_x64_package_and_its_checksum()
    {
        string json = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/jt-marte/OpenSWBF2/releases/tag/v0.2.0",
          "assets": [
            { "name": "Source.zip", "size": 5, "browser_download_url": "https://x/source.zip" },
            { "name": "OpenSWBF2-0.2.0-x64.zip", "size": 60000000, "browser_download_url": "https://x/pkg.zip" },
            { "name": "OpenSWBF2-0.2.0-x64.zip.sha256", "size": 90, "browser_download_url": "https://x/pkg.zip.sha256" }
          ]
        }
        """;
        var release = ReleaseClient.ParseRelease(json);
        Assert.NotNull(release);
        Assert.Equal("0.2.0", release!.Version);
        Assert.Equal("https://x/pkg.zip", release.DownloadUrl);
        Assert.Equal("https://x/pkg.zip.sha256", release.Sha256Url);
        Assert.Equal(60000000, release.Size);
    }

    [Fact]
    public void A_release_without_a_package_is_ignored() =>
        Assert.Null(ReleaseClient.ParseRelease("""{"tag_name":"v1","assets":[{"name":"notes.txt","size":1,"browser_download_url":"u"}]}"""));

    [Theory]
    [InlineData("0.2.0", null, true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    public void Newer(string candidate, string? installed, bool newer) => Assert.Equal(newer, ReleaseClient.IsNewer(candidate, installed));
}

public class GameLauncherTests
{
    [Fact]
    public void Arguments()
    {
        Assert.Equal("", GameLauncher.BuildArguments(new LauncherState()));
        Assert.Equal("-vulkan -borderless", GameLauncher.BuildArguments(new LauncherState { Renderer = "vulkan", Borderless = true }));
        Assert.Equal("-d3d9 /nointro", GameLauncher.BuildArguments(new LauncherState { Renderer = "d3d9", ExtraArgs = "  /nointro " }));
    }

    [Fact]
    public void State_round_trips_and_survives_a_broken_file()
    {
        using var temp = new TempDir();
        string file = temp.Sub("launcher.json");
        new LauncherState { InstalledVersion = "0.1.0", Borderless = true, ImportComplete = true }.Save(file);
        var loaded = LauncherState.Load(file);
        Assert.Equal("0.1.0", loaded.InstalledVersion);
        Assert.True(loaded.Borderless);
        Assert.True(loaded.ImportComplete);

        File.WriteAllText(file, "{ broken");
        Assert.Null(LauncherState.Load(file).InstalledVersion);
    }
}
