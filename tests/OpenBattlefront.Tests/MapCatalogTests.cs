using System.Diagnostics;
using System.Text;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class MapCatalogTests
{
    internal const string Sample = """
    {"format":1,"language":"english",
     "eras":[{"id":"c","key":"common.era.cw","name":"Clone Wars"},{"id":"g","key":"common.era.gcw","name":"Galactic Civil War"}],
     "modes":[{"id":"con","key":"common.mode.con","flag":"bCon","name":"Conquest"},{"id":"ctf","key":"common.mode.ctf","flag":"bCTF","name":"Capture the Flag"}],
     "maps":[
      {"code":"geo1","file":"geo1%s_%s","name":"Geonosis: Spire","mod":false,
       "eras":[{"id":"c","name":"Clone Wars"}],
       "modes":[{"id":"con","key":"common.mode.con","name":"Conquest","eras":["c"],"missions":{"c":"geo1c_con"}},
                {"id":"ctf","key":"common.mode.ctf","name":"Capture the Flag","eras":["c"],"missions":{"c":"geo1c_ctf"}}]},
      {"code":"kas2","file":"kas2%s_%s","name":"Kashyyyk: Beachhead","mod":false,
       "eras":[{"id":"g","name":"Galactic Civil War"},{"id":"c","name":"Clone Wars"}],
       "modes":[{"id":"con","name":"Conquest","eras":[{"id":"c"},{"id":"g"}],"missions":{"c":"kas2c_con","g":"kas2g_con"}}]},
      {"code":"bes2","file":"bes2%s_%s","name":"Bespin: Cloud City é \"quoted\"","mod":true,
       "eras":["g"],
       "modes":[{"id":"con","name":"Conquest","missions":{"g":"bes2g_con","x":"bad mission;"}},
                {"id":"xl","name":"XL","missions":{"q":"bes2q_xl"}}]},
      {"code":"AB1","file":"AB1%s_%s","name":"Abregado","mod":true,
       "eras":[{"id":"k","name":"KotOR"}],
       "modes":[{"id":"eli","key":"common.mode.eli","name":"Assault","missions":{"k":"AB1k_eli"}},
                {"id":"ass","key":"common.mode.ass","name":"Assault","missions":{"k":"AB1k_ass"}}]},
      {"code":"nomodes","name":"Broken","mod":true,"eras":[],"modes":[]}
     ]}
    """;

    [Fact]
    public void Parses_maps_eras_and_modes()
    {
        var catalog = MapCatalog.Parse(Sample);

        Assert.Equal("english", catalog.Language);
        Assert.Equal(new[] { "geo1", "kas2", "bes2", "AB1" }, catalog.Maps.Select(m => m.Code));

        var geo = catalog.Maps[0];
        Assert.False(geo.IsMod);
        Assert.Equal(new[] { "geo1c_con", "geo1c_ctf" }, geo.Missions.Select(m => m.Mission));
        Assert.Equal("Clone Wars · Capture the Flag", geo.Missions[1].Label);

        // Eras follow the map's order; the dump's mission names are used as they are.
        var kas = catalog.Maps[1];
        Assert.Equal(new[] { "kas2g_con", "kas2c_con" }, kas.Missions.Select(m => m.Mission));
        Assert.Equal("Galactic Civil War", kas.Missions[0].EraName);

        var bes = catalog.Maps[2];
        Assert.True(bes.IsMod);
        Assert.Equal("Bespin: Cloud City é \"quoted\"", bes.Name);
        Assert.Equal(new[] { "bes2g_con", "bes2q_xl" }, bes.Missions.Select(m => m.Mission));
        Assert.Equal("q", bes.Missions[1].EraName);

        Assert.Equal("Kashyyyk: Beachhead · Clone Wars · Conquest", catalog.Find("KAS2C_CON")!.Title);
        Assert.True(catalog.Contains("bes2g_con"));

        // Mod codes keep their case; eli and ass are both "Assault" in the game.
        Assert.Equal("KotOR · Hero Assault", catalog.Find("ab1k_eli")!.Label);
        Assert.Equal("Assault", catalog.Find("AB1k_ass")!.ModeName);
        Assert.False(catalog.Contains("bad mission;"));
    }

    [Theory]
    [InlineData("""{"format":2,"maps":[]}""")]
    [InlineData("""{"maps":[]}""")]
    [InlineData("not json")]
    public void Refuses_other_formats(string json)
    {
        Assert.Throws<InvalidDataException>(() => MapCatalog.Parse(json));
    }

    private static InstallLayout FakeInstall(TempDir temp, bool listing = true)
    {
        var layout = new InstallLayout(temp.Sub("Root"));
        Directory.CreateDirectory(layout.Game);
        File.WriteAllBytes(layout.GameExe, Encoding.ASCII.GetBytes(listing ? "MZ...-listmissions...OPENSWBF2" : "MZ...old build"));
        temp.File("Root\\Game\\AddOn\\BF1\\addme.script", "bf1");
        return layout;
    }

    private static MapCatalogStore.ProcessRunner FakeGame(List<string> calls, string json = Sample, int exitCode = 0) => (info, timeout, ct) =>
    {
        calls.Add(info.Arguments);
        int start = info.Arguments.IndexOf('"') + 1;
        string output = info.Arguments[start..info.Arguments.IndexOf('"', start)];
        if (exitCode == 0) File.WriteAllText(output, json);
        return Task.FromResult(exitCode);
    };

    [Fact]
    public async Task Builds_once_then_uses_the_cache_until_mods_change()
    {
        using var temp = new TempDir();
        var layout = FakeInstall(temp);
        var calls = new List<string>();
        var store = new MapCatalogStore(layout, "0.2.0", FakeGame(calls));

        var first = await store.GetAsync(refresh: false, null, CancellationToken.None);
        Assert.Equal(4, first.Maps.Count);
        Assert.Single(calls);
        Assert.StartsWith("-listmissions \"", calls[0]);
        Assert.Contains("\" -lang 0", calls[0]);
        Assert.True(File.Exists(store.CacheFile));

        await store.GetAsync(refresh: false, null, CancellationToken.None);
        Assert.Single(calls);

        temp.File("Root\\Game\\AddOn\\NEW\\addme.script", "new mod");
        await store.GetAsync(refresh: false, null, CancellationToken.None);
        Assert.Equal(2, calls.Count);

        await store.GetAsync(refresh: true, null, CancellationToken.None);
        Assert.Equal(3, calls.Count);

        // Another build version means another cache key.
        var newer = new MapCatalogStore(layout, "0.2.1", FakeGame(calls));
        Assert.Null(newer.TryLoadCached());
    }

    [Fact]
    public async Task An_older_build_is_never_run()
    {
        using var temp = new TempDir();
        var layout = FakeInstall(temp, listing: false);
        var calls = new List<string>();
        var store = new MapCatalogStore(layout, "0.1.0", FakeGame(calls));

        Assert.False(store.GameSupportsListing);
        var e = await Assert.ThrowsAsync<NotSupportedException>(() => store.GetAsync(false, null, CancellationToken.None));
        Assert.StartsWith("Update OpenSWBF2 to host from here", e.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_failed_dump_is_reported_and_not_cached()
    {
        using var temp = new TempDir();
        var layout = FakeInstall(temp);
        var calls = new List<string>();
        var store = new MapCatalogStore(layout, "0.2.0", FakeGame(calls, exitCode: 2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(false, null, CancellationToken.None));
        Assert.False(File.Exists(store.CacheFile));
    }

    [Fact]
    public async Task Runs_the_game_headless_from_its_folder()
    {
        using var temp = new TempDir();
        var layout = FakeInstall(temp);
        ProcessStartInfo? seen = null;
        var store = new MapCatalogStore(layout, "0.2.0", (info, timeout, ct) =>
        {
            seen = info;
            return Task.FromResult(2);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BuildAsync(null, CancellationToken.None));
        Assert.Equal(layout.GameExe, seen!.FileName);
        Assert.Equal(layout.Game, seen.WorkingDirectory);
        Assert.True(seen.CreateNoWindow);
    }
}
