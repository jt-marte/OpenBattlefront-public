using System.Net;
using System.Text;
using System.Text.Json;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ServerModsTests
{
    private static readonly ServerAddress Local = new("127.0.0.1", 3700);

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5", 3658)]
    [InlineData(" 203.0.113.5:4000 ", "203.0.113.5", 4000)]
    [InlineData("MyServer.Example.com", "myserver.example.com", 3658)]
    [InlineData("box:3658", "box", 3658)]
    public void Parses_server_addresses(string text, string host, int port)
    {
        Assert.True(ServerAddress.TryParse(text, out var address));
        Assert.Equal(new ServerAddress(host, port), address);
        Assert.Equal($"{host}:{port}", address.ConnectArg);
        Assert.Equal(new Uri($"http://{host}:{port}/"), address.ModsBase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:12ab")]
    [InlineData("host:-5")]
    [InlineData("http://host")]
    [InlineData("[::1]:3658")]
    [InlineData("two words")]
    [InlineData("-bad.example.com")]
    [InlineData("a..b")]
    [InlineData("host\";calc")]
    [InlineData("averyveryveryveryveryveryveryveryveryveryverylonghostname.example.com")]
    public void Refuses_what_isnt_an_address(string text) => Assert.False(ServerAddress.TryParse(text, out _));

    [Fact]
    public void The_default_port_is_left_out_when_shown()
    {
        Assert.Equal("host", new ServerAddress("host", 3658).ToString());
        Assert.Equal("host:4000", new ServerAddress("host", 4000).ToString());
    }

    [Theory]
    [InlineData("secret", true)]
    [InlineData("fifteen-chars15", true)]
    [InlineData("sixteen-chars-16", false)]
    [InlineData("two words", false)]
    [InlineData("quo\"te", false)]
    [InlineData("", false)]
    public void Join_passwords_fit_the_game(string password, bool valid) => Assert.Equal(valid, JoinPasswords.IsValid(password));

    [Fact]
    public void Joining_opens_the_game_on_the_server()
    {
        var state = new LauncherState { PlayerName = "Pat", SkipIntro = true, Borderless = false };
        Assert.Equal("-nointro -name \"Pat\"", GameLauncher.BuildArguments(state));
        Assert.Equal("-nointro -name \"Pat\" -connect 203.0.113.5:3658 -password secret",
            GameLauncher.BuildArguments(state, new JoinTarget(new ServerAddress("203.0.113.5", 3658), "secret")));
        Assert.Equal("-nointro -name \"Pat\" -connect box:4000",
            GameLauncher.BuildArguments(state, new JoinTarget(new ServerAddress("box", 4000), "has space")));
    }

    [Theory]
    [InlineData(null, new string[0], false, NameNeed.Ask)]          // first run, no data: ask
    [InlineData(null, new[] { "Pat" }, false, NameNeed.TakeProfile)] // the game has a profile: take it
    [InlineData(null, new string[0], true, NameNeed.Ask)]
    [InlineData(null, new[] { "Pat" }, true, NameNeed.TakeProfile)]
    [InlineData("", new[] { "Pat" }, false, NameNeed.Keep)]         // Play: empty means pick in the game
    [InlineData("", new[] { "Pat" }, true, NameNeed.Ask)]           // Join: a server needs a real name
    [InlineData("[NULL]", new string[0], true, NameNeed.Ask)]
    [InlineData("Jt", new string[0], true, NameNeed.Keep)]          // an older short profile still signs in
    [InlineData("Rex", new string[0], false, NameNeed.Keep)]
    public void Knows_when_to_ask_for_a_player_name(string? saved, string[] profiles, bool forJoin, NameNeed expected) =>
        Assert.Equal(expected, PlayerProfiles.NameNeeded(saved, profiles, forJoin));

    [Fact]
    public void Recent_servers_keep_the_newest_eight()
    {
        var state = new LauncherState();
        for (int i = 0; i < 10; i++) state.RememberServer(new ServerAddress($"10.0.0.{i}", 3658), $"Server {i}", null);
        state.RememberServer(new ServerAddress("10.0.0.5", 3658), "", "pw");
        Assert.Equal(LauncherState.MaxRecentServers, state.RecentServers.Count);
        Assert.Equal("10.0.0.5", state.RecentServers[0].Address);
        Assert.Equal("Server 5", state.RecentServers[0].Name);
        Assert.Equal("pw", state.RecentServers[0].JoinPassword);
        Assert.Single(state.RecentServers, s => s.Address == "10.0.0.5");
        Assert.DoesNotContain(state.RecentServers, s => s.Address == "10.0.0.0");
    }

    // The manifest

    private static string Manifest(string folder, params string[] paths) => JsonSerializer.Serialize(new
    {
        schema = 1,
        server = "S",
        openswbf2 = "0.2.2",
        folders = new[]
        {
            new { name = folder, size = 0, source = "server", files = paths.Select(p => new { path = p, size = 1, sha256 = new string('a', 64) }) },
        },
    });

    [Fact]
    public void Reads_a_good_mod_list()
    {
        var manifest = ServerModsManifest.Parse(Manifest("BF1", "addme.script", "data/_lvl_pc/bf1.lvl"));
        Assert.Equal("S", manifest.Server);
        Assert.Equal(2, manifest.Folders.Single().Size);
        Assert.Equal("data/_lvl_pc/bf1.lvl", manifest.Folders[0].Files[1].Path);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("data/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("data\\bf1.lvl")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("addme.script:evil")]
    [InlineData("data//bf1.lvl")]
    [InlineData("setup.exe")]
    [InlineData("tools/Install.PS1")]
    [InlineData("tools/run.cmd")]
    [InlineData("con")]
    [InlineData("data/NUL.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Refuses_unsafe_files(string path) =>
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(Manifest("BF1", "addme.script", path)));

    [Theory]
    [InlineData("AAA-v1.3patch")]
    [InlineData("aaa-V1.3PATCH")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("My Mod")]
    [InlineData("a/b")]
    [InlineData("CON")]
    public void Refuses_unsafe_folders_and_the_patch(string folder) =>
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(Manifest(folder, "addme.script")));

    [Fact]
    public void Refuses_duplicates_bad_hashes_and_folders_without_addme()
    {
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(Manifest("BF1", "addme.script", "Map.lvl", "map.lvl")));
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(Manifest("BF1", "data/bf1.lvl")));
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(Manifest("BF1", "addme.script").Replace(new string('a', 64), "xyz")));
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(Manifest("BF1", "addme.script").Replace("\"schema\":1", "\"schema\":2")));
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse("not json"));
        string twice = JsonSerializer.Serialize(new
        {
            schema = 1,
            folders = new[] { "BF1", "bf1" }.Select(n => new { name = n, files = new[] { new { path = "addme.script", size = 1, sha256 = new string('a', 64) } } }),
        });
        Assert.Throws<InvalidDataException>(() => ServerModsManifest.Parse(twice));
    }

    // The plan

    private static ServerModsManifest ManifestOf(FakeModServer server) => ServerModsManifest.Parse(server.ManifestJson());

    [Fact]
    public void Plans_new_updated_switched_off_and_same_mods()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("Root"));
        var server = new FakeModServer();
        foreach (string mod in new[] { "Same", "Old", "Off", "OffOld", "New" })
        {
            server.Add(mod, "addme.script", mod + " addme");
            server.Add(mod, "data/map.lvl", mod + " map v2");
        }
        void Local(string dir, string mod, string map)
        {
            temp.File($@"Root\Game\{dir}\{mod}\addme.script", mod + " addme");
            temp.File($@"Root\Game\{dir}\{mod}\data\map.lvl", map);
        }
        Local("AddOn", "Same", "Same map v2");
        Local("AddOn", "Old", "Old map v1");
        temp.File(@"Root\Game\AddOn\Old\my notes.txt", "mine");
        Local("AddOn_disabled", "Off", "Off map v2");
        Local("AddOn_disabled", "OffOld", "OffOld map v1");

        var plan = ModSyncPlan.Create(ManifestOf(server), layout, new FileHashCache());
        var actions = plan.Items.ToDictionary(i => i.Name, i => i.Action);
        Assert.Equal(ModSyncAction.UpToDate, actions["Same"]);
        Assert.Equal(ModSyncAction.Update, actions["Old"]);
        Assert.Equal(ModSyncAction.Enable, actions["Off"]);
        Assert.Equal(ModSyncAction.Update, actions["OffOld"]);
        Assert.Equal(ModSyncAction.New, actions["New"]);
        Assert.Equal(new[] { "data/map.lvl" }, plan.Items.Single(i => i.Name == "Old").Missing.Select(f => f.Path));
        Assert.Equal(2, plan.Items.Single(i => i.Name == "New").Missing.Count);
        Assert.Equal(4, plan.Needed.Count);
        Assert.Equal(plan.Needed.Sum(i => i.Missing.Sum(f => f.Size)), plan.Bytes);
    }

    [Fact]
    public void Hashes_each_local_file_once()
    {
        using var temp = new TempDir();
        string file = temp.File(@"mod\data.lvl", "maps");
        string cacheFile = temp.Sub("hashes.json");
        var cache = new FileHashCache(cacheFile);
        Assert.Equal(TempDir.Sha("maps"), cache.Sha256(file));
        cache.Sha256(file);
        Assert.Equal(1, cache.Hashed);
        cache.Save();

        var again = new FileHashCache(cacheFile);
        Assert.Equal(TempDir.Sha("maps"), again.Sha256(file));
        Assert.Equal(0, again.Hashed);
        File.WriteAllText(file, "other maps");
        Assert.Equal(TempDir.Sha("other maps"), again.Sha256(file));
        Assert.Equal(1, again.Hashed);
    }

    // The mod list over HTTP

    private sealed class Answer : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_answer(request));
    }

    private static HttpClient Http(HttpMessageHandler handler) => ModService.CreateHttp(new Version(0, 3, 0), handler);

    [Fact]
    public async Task A_refused_password_is_tried_once_without_for_a_server_that_dropped_it()
    {
        var open = new FakeModServer();
        open.Add("BF1", "addme.script", "a");
        using (var http = Http(open))
        {
            var (reply, password) = await ModService.GetManifestOrOpenAsync(http, Local, "old", default);
            Assert.Equal(ModServiceStatus.Ok, reply.Status);
            Assert.Null(password);
            Assert.Equal(new[] { "old", null }, open.Requests.Select(r => r.Auth));
        }

        var locked = new FakeModServer { JoinPassword = "secret" };
        locked.Add("BF1", "addme.script", "a");
        using (var http = Http(locked))
        {
            var (wrong, kept) = await ModService.GetManifestOrOpenAsync(http, Local, "old", default);
            Assert.Equal(ModServiceStatus.NeedsPassword, wrong.Status);
            Assert.Equal("old", kept);
            var (right, used) = await ModService.GetManifestOrOpenAsync(http, Local, "secret", default);
            Assert.Equal(ModServiceStatus.Ok, right.Status);
            Assert.Equal("secret", used);
            var (none, noPassword) = await ModService.GetManifestOrOpenAsync(http, Local, null, default);
            Assert.Equal(ModServiceStatus.NeedsPassword, none.Status);
            Assert.Null(noPassword);
        }
    }

    [Fact]
    public async Task Tells_a_password_prompt_from_no_mod_service()
    {
        var server = new FakeModServer { JoinPassword = "secret" };
        server.Add("BF1", "addme.script", "a");
        using var http = Http(server);
        Assert.Equal(ModServiceStatus.NeedsPassword, (await ModService.GetManifestAsync(http, Local, null, default)).Status);
        Assert.Equal(ModServiceStatus.NeedsPassword, (await ModService.GetManifestAsync(http, Local, "wrong", default)).Status);
        var ok = await ModService.GetManifestAsync(http, Local, "secret", default);
        Assert.Equal(ModServiceStatus.Ok, ok.Status);
        Assert.Equal("BF1", ok.Manifest!.Folders.Single().Name);
        Assert.Equal("secret", server.Requests.Last().Auth);

        using var refused = Http(new Answer(_ => throw new HttpRequestException("refused")));
        Assert.Equal(ModServiceStatus.Unavailable, (await ModService.GetManifestAsync(refused, Local, null, default)).Status);
        using var notFound = Http(new Answer(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal(ModServiceStatus.Unavailable, (await ModService.GetManifestAsync(notFound, Local, null, default)).Status);
        using var webPage = Http(new Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") }));
        Assert.Equal(ModServiceStatus.Unavailable, (await ModService.GetManifestAsync(webPage, Local, null, default)).Status);
        using var lockedOut = Http(new Answer(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"too many wrong passwords; wait 30 seconds\"}", Encoding.UTF8, "application/json"),
        }));
        var e = await Assert.ThrowsAsync<ModServiceException>(() => ModService.GetManifestAsync(lockedOut, Local, "x", default));
        Assert.Contains("Wait 30 seconds", e.Message);
    }

    // Downloading and installing

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Temp = new();
        public readonly FakeModServer Server = new();
        public readonly List<string> Removed = new();
        public InstallLayout Layout => new(Temp.Sub("Root"));
        public readonly byte[] Map = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

        public Fixture()
        {
            Server.Add("BF1", "addme.script", "bf1 addme");
            Server.Add("BF1", "data/_lvl_pc/bf1.lvl", Map);
            Directory.CreateDirectory(Layout.Game);
        }

        public async Task SyncAsync(string? password = null)
        {
            using var http = Http(Server);
            var reply = await ModService.GetManifestAsync(http, Local, password, default);
            var plan = ModSyncPlan.Create(reply.Manifest!, Layout, new FileHashCache());
            await new ModSyncClient(http, Layout, Removed.Add).InstallAsync(Local, password, plan, null, default);
        }

        public string Installed(string path) => Path.Combine(Layout.AddOn, path.Replace('/', '\\'));

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task Installs_a_new_mod_and_cleans_up()
    {
        using var f = new Fixture();
        await f.SyncAsync();
        Assert.Equal("bf1 addme", File.ReadAllText(f.Installed("BF1/addme.script")));
        Assert.Equal(f.Map, File.ReadAllBytes(f.Installed("BF1/data/_lvl_pc/bf1.lvl")));
        Assert.False(Directory.Exists(Path.Combine(f.Layout.Downloads, "server-mods")));
        Assert.Contains(f.Server.Requests, r => r.Path == "/mods/BF1/data/_lvl_pc/bf1.lvl");

        // Joining again downloads nothing
        int before = f.Server.Requests.Count;
        await f.SyncAsync();
        Assert.Equal(before + 1, f.Server.Requests.Count);
    }

    [Fact]
    public async Task A_cut_download_resumes_where_it_stopped()
    {
        using var f = new Fixture();
        f.Server.CutOnce = ("data/_lvl_pc/bf1.lvl", 100_000);
        await f.SyncAsync();
        Assert.Equal(f.Map, File.ReadAllBytes(f.Installed("BF1/data/_lvl_pc/bf1.lvl")));
        var tries = f.Server.Requests.Where(r => r.Path.EndsWith("bf1.lvl")).ToList();
        Assert.Equal(2, tries.Count);
        Assert.Null(tries[0].RangeFrom);
        Assert.Equal(100_000, tries[1].RangeFrom);
        Assert.Equal($"\"{FakeModServer.Sha(f.Map)}\"", tries[1].IfRange);
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_starts_the_file_again()
    {
        using var f = new Fixture();
        f.Server.CutOnce = ("data/_lvl_pc/bf1.lvl", 100_000);
        f.Server.IgnoreRanges = true;
        await f.SyncAsync();
        Assert.Equal(f.Map, File.ReadAllBytes(f.Installed("BF1/data/_lvl_pc/bf1.lvl")));
    }

    [Fact]
    public async Task A_damaged_file_is_fetched_again_and_twice_stops()
    {
        using var f = new Fixture();
        f.Server.Damage["data/_lvl_pc/bf1.lvl"] = 1;
        await f.SyncAsync();
        Assert.Equal(f.Map, File.ReadAllBytes(f.Installed("BF1/data/_lvl_pc/bf1.lvl")));

        using var g = new Fixture();
        g.Server.Damage["data/_lvl_pc/bf1.lvl"] = 2;
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => g.SyncAsync());
        Assert.Contains("damaged twice", e.Message);
        Assert.False(Directory.Exists(g.Installed("BF1")));
        Assert.Empty(Directory.EnumerateFiles(g.Layout.Downloads, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_update_swaps_only_the_differing_files()
    {
        using var f = new Fixture();
        f.Temp.File(@"Root\Game\AddOn\BF1\addme.script", "bf1 addme");
        f.Temp.File(@"Root\Game\AddOn\BF1\data\_lvl_pc\bf1.lvl", "old maps");
        f.Temp.File(@"Root\Game\AddOn\BF1\my notes.txt", "mine");
        await f.SyncAsync();
        Assert.Equal(f.Map, File.ReadAllBytes(f.Installed("BF1/data/_lvl_pc/bf1.lvl")));
        Assert.Equal("mine", File.ReadAllText(f.Installed("BF1/my notes.txt")));
        Assert.Equal(new[] { f.Installed("BF1/data/_lvl_pc/bf1.lvl") }, f.Removed);
        Assert.DoesNotContain(f.Server.Requests, r => r.Path == "/mods/BF1/addme.script");
    }

    [Fact]
    public async Task A_switched_off_copy_is_switched_on()
    {
        using var f = new Fixture();
        f.Temp.File(@"Root\Game\AddOn_disabled\BF1\addme.script", "bf1 addme");
        Directory.CreateDirectory(Path.Combine(f.Layout.AddOnDisabled, @"BF1\data\_lvl_pc"));
        File.WriteAllBytes(Path.Combine(f.Layout.AddOnDisabled, @"BF1\data\_lvl_pc\bf1.lvl"), f.Map);
        await f.SyncAsync();
        Assert.True(File.Exists(f.Installed("BF1/data/_lvl_pc/bf1.lvl")));
        Assert.False(Directory.Exists(Path.Combine(f.Layout.AddOnDisabled, "BF1")));
        Assert.DoesNotContain(f.Server.Requests, r => r.Path.StartsWith("/mods/"));
    }

    [Fact]
    public async Task Sends_the_join_password_for_every_file()
    {
        using var f = new Fixture();
        f.Server.JoinPassword = "secret";
        await f.SyncAsync("secret");
        Assert.All(f.Server.Requests, r => Assert.Equal("secret", r.Auth));
    }

    [Fact]
    public void Paths_never_leave_the_mod_folder()
    {
        Assert.EndsWith(@"\root\BF1\data\x.lvl", ModSyncClient.Inside(@"C:\root", "BF1/data/x.lvl"));
        Assert.Throws<InvalidDataException>(() => ModSyncClient.Inside(@"C:\root\BF1", "../BF2/x.lvl"));
        Assert.Throws<InvalidDataException>(() => ModSyncClient.Inside(@"C:\root\BF1", "../BF1x/x.lvl"));
        Assert.Equal("mods/My%20Mod/data/a%23b.lvl", ModService.FilePath("mods", "My Mod", "data/a#b.lvl"));
    }
}
