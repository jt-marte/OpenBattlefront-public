using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBattlefront.Core;
using OpenBattlefront.Core.Hosting;

namespace OpenBattlefront.Tests;

/// <summary>The game as rcon reports it; /start and /lobby change its phase as the server does.</summary>
internal sealed class FakeGame : IGameProbe
{
    public GameState State = new("lobby", "BS1g_con", "geo1c_con", 0, 0);
    public List<string> Names = new();
    public List<string> Commands = new();
    public bool Up = true;

    public Task<GameState> StateAsync(CancellationToken ct) => Up ? Task.FromResult(State) : throw new TimeoutException("no answer");
    public Task<IReadOnlyList<string>> NamesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Names.ToList());

    public Task StartAsync(int countdown, CancellationToken ct)
    {
        if (!Up) throw new TimeoutException("no answer");
        Commands.Add($"/start {countdown}");
        State = State with { Phase = "warmup" };
        return Task.CompletedTask;
    }

    public Task LobbyNowAsync(CancellationToken ct)
    {
        if (!Up) throw new TimeoutException("no answer");
        Commands.Add("/lobby now");
        State = State with { Phase = "lobby" };
        return Task.CompletedTask;
    }
}

/// <summary>Made-up mods in a temp folder, the same ones test_modserver.py makes: BF1 with a program
/// in it, the 1.3 patch, a folder without addme.script, and a secret next to them.</summary>
internal sealed class HostFixture : IAsyncDisposable
{
    public const string Join = "joinpw";
    public const string Admin = "Admin12345";
    public readonly TempDir Temp = new();
    public readonly byte[] Lvl = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 + 7)).ToArray();
    public readonly FakeGame Game = new();
    public DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    public ModHostService Service = null!;

    public string AddOn => Temp.Sub("addons");

    public HostFixture()
    {
        Temp.File("secret.txt", "top secret");
        Temp.File(@"addons\BF1\addme.script", "addme");
        File.WriteAllBytes(Temp.File(@"addons\BF1\data\_lvl_pc\bf1.lvl"), Lvl);
        Temp.File(@"addons\BF1\Readme.txt", "hello");
        Temp.File(@"addons\BF1\setup.exe", "MZ program");
        Temp.File(@"addons\BF1\tools\run.BAT", "@echo off");
        Temp.File(@"addons\AAA-v1.3patch\addme.script", "the patch");
        Temp.File(@"addons\NoAddme\x.lvl", "x");
    }

    public ModHostService Start(string join = Join, bool game = true, Func<string?>? missions = null)
    {
        Service = new ModHostService(new ModHostOptions
        {
            AddOn = AddOn,
            Hashes = new FileHashCache(Temp.Sub("hashes.json")),
            ServerName = "Test server",
            Version = "0.2.2",
            JoinPassword = join,
            AdminPassword = Admin,
            Bind = IPAddress.Loopback,
            Port = 0,
            Missions = missions,
        }, game ? Game : null, () => Now);
        Service.Start(watch: false);
        return Service;
    }

    public int Port => Service.Port;

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Temp.Dispose();
    }
}

public class ModHostServiceTests
{
    private const string Join = HostFixture.Join, Admin = HostFixture.Admin;

    private static RawHttp.Answer Get(HostFixture f, string path, string? password = Join, IDictionary<string, string>? headers = null) =>
        RawHttp.Send(f.Port, "GET", path, password, headers: headers);

    [Fact]
    public async Task Lists_mod_folders_with_hashes_and_no_programs()
    {
        await using var f = new HostFixture();
        f.Start();
        var reply = Get(f, "/mods.json");
        Assert.Equal(200, reply.Status);
        var json = reply.Json;
        Assert.Equal(1, json.GetProperty("schema").GetInt32());
        Assert.Equal("Test server", json.GetProperty("server").GetString());
        Assert.False(json.GetProperty("uploads").GetBoolean());
        Assert.False(json.TryGetProperty("pending", out _));
        var folder = json.GetProperty("folders").EnumerateArray().Single();
        Assert.Equal("BF1", folder.GetProperty("name").GetString());
        var files = folder.GetProperty("files").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!, e => e);
        Assert.Equal(new[] { "addme.script", "data/_lvl_pc/bf1.lvl", "Readme.txt" }.Order(), files.Keys.Order());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(f.Lvl)).ToLowerInvariant(), files["data/_lvl_pc/bf1.lvl"].GetProperty("sha256").GetString());
        Assert.Equal(0, Get(f, "/mods.json", Admin).Json.GetProperty("pending").GetArrayLength());

        // What launchers read goes through the launcher's own checks
        Assert.Equal("BF1", ServerModsManifest.Parse(reply.Text).Folders.Single().Name);
    }

    [Fact]
    public async Task Needs_the_join_or_admin_password_and_locks_out_guessers()
    {
        await using var f = new HostFixture();
        f.Start();
        var none = Get(f, "/mods.json", null);
        Assert.Equal((401, "password"), (none.Status, none.Error));
        Assert.Contains("Bearer", none.Headers["WWW-Authenticate"]);
        Assert.Equal(401, Get(f, "/mods/BF1/addme.script", null).Status);
        Assert.Equal(200, Get(f, "/mods.json", Admin).Status);
        for (int i = 0; i < 5; i++) Assert.Equal(401, Get(f, "/mods.json", "guess").Status);
        Assert.Equal(429, Get(f, "/mods.json", Admin).Status);
    }

    [Fact]
    public async Task Open_without_a_join_password()
    {
        await using var f = new HostFixture();
        f.Start(join: "");
        Assert.Equal(200, Get(f, "/mods.json", null).Status);
        Assert.Equal(200, Get(f, "/mods/BF1/addme.script", null).Status);
        Assert.Equal(401, Get(f, "/mods.json", "wrong").Status);
    }

    [Fact]
    public async Task Serves_the_hosted_games_map_list()
    {
        await using var f = new HostFixture();
        string? listing = null;
        f.Start(missions: () => listing);
        var none = Get(f, "/missions.json");
        Assert.Equal((404, "the server has no map list yet"), (none.Status, none.Error));
        listing = """{"format": 1, "maps": []}""";
        var reply = Get(f, "/missions.json");
        Assert.Equal((200, listing), (reply.Status, reply.Text));
        Assert.Equal(401, Get(f, "/missions.json", null).Status);
        // Nothing to keep on a hosted server: its settings are this PC's
        Assert.Equal(404, Get(f, "/admin/settings", Admin).Status);
    }

    [Fact]
    public async Task No_uploads_or_restarts_on_a_hosted_server()
    {
        await using var f = new HostFixture();
        f.Start();
        Assert.Equal(403, RawHttp.Send(f.Port, "POST", "/upload/NEW/begin", Join, new { files = Array.Empty<object>() }).Status);
        var upload = RawHttp.Send(f.Port, "POST", "/upload/NEW/begin", Admin, new { files = Array.Empty<object>() });
        Assert.Equal(403, upload.Status);
        Assert.Contains("uploads are switched off", upload.Error);
        Assert.Equal(403, RawHttp.Send(f.Port, "PUT", "/upload/NEW/addme.script", Admin, new { x = 1 }).Status);
        Assert.Equal(403, RawHttp.Send(f.Port, "POST", "/admin/restart?when=now", Admin).Status);
    }

    public static readonly string[] Traversal =
    {
        "/mods/BF1/../BF1/addme.script",
        "/mods/BF1/%2e%2e/%2e%2e/secret.txt",
        "/mods/BF1/..%2f..%2fsecret.txt",
        "/mods/BF1/%2E%2E/NoAddme/x.lvl",
        "/mods/BF1/data%5c_lvl_pc%5cbf1.lvl",
        "/mods/BF1/data\\_lvl_pc\\bf1.lvl",
        "//secret.txt",
        "/mods//BF1/addme.script",
        "/mods/BF1/C:/secret.txt",
        "/mods/BF1/addme.script%00.txt",
        "/mods/BF1/addme.script::$DATA",
        "/mods/%2e%2e/secret.txt",
        "/mods/BF1/",
        "/mods/BF1/%ff",
        "/mods/BF1/con",
    };

    [Fact]
    public async Task Traversal_never_leaves_the_folder()
    {
        await using var f = new HostFixture();
        f.Start();
        foreach (string path in Traversal.Append("/mods/" + string.Concat(Enumerable.Repeat("a/", 600)) + "x"))
        {
            var reply = Get(f, path, Admin);
            Assert.True(reply.Status is 400 or 404, $"{path}: {reply.Status}");
            Assert.DoesNotContain("top secret", reply.Text);
        }
        foreach (string path in new[] { "/mods/BF1/setup.exe", "/mods/BF1/tools/run.BAT", "/mods/AAA-v1.3patch/addme.script", "/mods/NoAddme/x.lvl", "/mods/bf1/addme.script", "/mods.JSON" })
            Assert.Equal(404, Get(f, path).Status);
    }

    [Theory]
    [InlineData("bytes=100-199", 206, "bytes 100-199/300000", 100, 100)]
    [InlineData("bytes=299000-", 206, "bytes 299000-299999/300000", 299000, 1000)]
    [InlineData("bytes=-500", 206, "bytes 299500-299999/300000", 299500, 500)]
    [InlineData("bytes=0-999999", 206, "bytes 0-299999/300000", 0, 300000)]
    [InlineData("bytes=0-1,5-6", 200, null, 0, 300000)]
    [InlineData("items=0-1", 200, null, 0, 300000)]
    public async Task Ranges(string range, int status, string? contentRange, int start, int length)
    {
        await using var f = new HostFixture();
        f.Start();
        var reply = Get(f, "/mods/BF1/data/_lvl_pc/bf1.lvl", headers: new Dictionary<string, string> { ["Range"] = range });
        Assert.Equal(status, reply.Status);
        Assert.Equal(contentRange, reply.Headers.GetValueOrDefault("Content-Range"));
        Assert.Equal(f.Lvl.AsSpan(start, length).ToArray(), reply.Body);
        Assert.Equal("bytes", reply.Headers["Accept-Ranges"]);
    }

    [Fact]
    public async Task Unsatisfiable_ranges_if_range_head_and_keep_alive()
    {
        await using var f = new HostFixture();
        f.Start();
        string url = "/mods/BF1/data/_lvl_pc/bf1.lvl";
        var none = Get(f, url, headers: new Dictionary<string, string> { ["Range"] = "bytes=300000-" });
        Assert.Equal((416, "bytes */300000"), (none.Status, none.Headers["Content-Range"]));
        string sha = Convert.ToHexString(SHA256.HashData(f.Lvl)).ToLowerInvariant();
        Assert.Equal(200, Get(f, url, headers: new Dictionary<string, string> { ["Range"] = "bytes=10-", ["If-Range"] = $"\"{new string('0', 64)}\"" }).Status);
        Assert.Equal(206, Get(f, url, headers: new Dictionary<string, string> { ["Range"] = "bytes=10-", ["If-Range"] = $"\"{sha}\"" }).Status);
        var head = RawHttp.Send(f.Port, "HEAD", url, Join);
        Assert.Equal(("300000", 0), (head.Headers["Content-Length"], head.Body.Length));
        Assert.Equal($"\"{sha}\"", head.Headers["ETag"]);

        using var one = new RawHttp(f.Port);
        foreach (string path in new[] { "/mods/BF1/addme.script", "/mods/BF1/Readme.txt", url, "/mods.json" })
            Assert.Equal(200, one.Request("GET", path, Join).Status);
    }

    [Fact]
    public async Task Caps_connections_from_one_address()
    {
        await using var f = new HostFixture();
        f.Start();
        var idle = Enumerable.Range(0, ModHostService.MaxPerAddress).Select(_ => new TcpClient("127.0.0.1", f.Port)).ToList();
        try
        {
            await Task.Delay(300);
            using var extra = new TcpClient("127.0.0.1", f.Port);
            var stream = extra.GetStream();
            stream.ReadTimeout = 5000;
            stream.Write("GET /mods.json HTTP/1.1\r\nHost: x\r\n\r\n"u8);
            int read;
            try { read = stream.Read(new byte[100]); }
            catch (IOException) { read = 0; }
            Assert.Equal(0, read);
        }
        finally
        {
            idle.ForEach(c => c.Dispose());
        }
        await Task.Delay(500);
        Assert.Equal(200, Get(f, "/mods.json").Status);
    }

    // The lobby

    private static string JoinLobby(HostFixture f, string name)
    {
        var reply = RawHttp.Send(f.Port, "POST", "/lobby/join", Join, new { name });
        Assert.Equal(200, reply.Status);
        return reply.Json.GetProperty("token").GetString()!;
    }

    private static void Update(HostFixture f, string token, string state, int progress = 0) =>
        Assert.Equal(200, RawHttp.Send(f.Port, "POST", "/lobby/update", Join, new { token, state, progress }).Status);

    private static JsonElement Status(HostFixture f, string? token = null) =>
        Get(f, "/status" + (token == null ? "" : $"?token={token}")).Json;

    private static RawHttp.Answer Match(HostFixture f, string? what, string method = "POST", string? password = Admin) =>
        RawHttp.Send(f.Port, method, "/admin/match" + (what == null ? "" : $"?do={what}"), password);

    [Fact]
    public async Task Join_update_leave_and_expiry()
    {
        await using var f = new HostFixture();
        f.Start();
        string token = JoinLobby(f, "Pat");
        var status = Status(f, token);
        Assert.True(status.GetProperty("inLobby").GetBoolean());
        Assert.Equal("Test server", status.GetProperty("server").GetString());
        var entry = status.GetProperty("lobby").EnumerateArray().Single();
        Assert.Equal(("Pat", "downloading", 0), (entry.GetProperty("name").GetString(), entry.GetProperty("state").GetString(), entry.GetProperty("progress").GetInt32()));
        Update(f, token, "downloading", 45);
        Assert.Equal(45, Status(f, token).GetProperty("lobby")[0].GetProperty("progress").GetInt32());
        Update(f, token, "waiting", 100);
        Assert.Equal("waiting", Status(f, token).GetProperty("lobby")[0].GetProperty("state").GetString());
        RawHttp.Send(f.Port, "POST", "/lobby/leave", Join, new { token });
        Assert.Equal(0, Status(f).GetProperty("lobby").GetArrayLength());
        Assert.False(Status(f, token).GetProperty("inLobby").GetBoolean());

        JoinLobby(f, "Rex");
        f.Now += LauncherLobby.Expire + TimeSpan.FromSeconds(1);
        Assert.Equal(0, Status(f).GetProperty("lobby").GetArrayLength());
        Assert.Equal(404, RawHttp.Send(f.Port, "POST", "/lobby/update", Join, new { token = "nope", state = "waiting" }).Status);
    }

    [Fact]
    public async Task Names_and_passwords_are_checked()
    {
        await using var f = new HostFixture();
        f.Start();
        foreach (object? name in new object?[] { "", new string('x', 16), "a\"b", "a\\b", " pad", "pad ", "[NULL]", 7, null })
            Assert.Equal(400, RawHttp.Send(f.Port, "POST", "/lobby/join", Join, new { name }).Status);
        Assert.Equal(401, RawHttp.Send(f.Port, "POST", "/lobby/join", null, new { name = "Pat" }).Status);
        Assert.Equal(401, Get(f, "/status", null).Status);
        Assert.Equal(400, RawHttp.Send(f.Port, "POST", "/lobby/update", Join, new { token = "x", state = "playing" }).Status);
    }

    [Fact]
    public async Task Start_loads_everyones_game_then_starts()
    {
        await using var f = new HostFixture();
        f.Start();
        Update(f, JoinLobby(f, "Pat"), "waiting", 100);
        Update(f, JoinLobby(f, "Rex"), "waiting", 100);
        JoinLobby(f, "Slow"); // still downloading: doesn't hold the start
        var reply = Match(f, "start");
        Assert.Equal((200, "loading"), (reply.Status, reply.Json.GetProperty("start").GetString()));
        Assert.Equal(60, reply.Json.GetProperty("startIn").GetInt32());

        f.Game.State = f.Game.State with { Players = 1, Teams = 1 };
        await f.Service.Watch.StepAsync(default);
        f.Game.State = f.Game.State with { Players = 2, Teams = 1 }; // the second is still loading
        await f.Service.Watch.StepAsync(default);
        Assert.Empty(f.Game.Commands);
        f.Game.State = f.Game.State with { Teams = 2 };
        await f.Service.Watch.StepAsync(default);
        Assert.Equal(new[] { "/start 5" }, f.Game.Commands);
        var status = Status(f);
        Assert.Equal(("none", "warmup"), (status.GetProperty("start").GetString(), status.GetProperty("phase").GetString()));
    }

    [Fact]
    public async Task Start_settles_and_has_a_deadline()
    {
        await using var f = new HostFixture();
        f.Start();
        Update(f, JoinLobby(f, "Pat"), "waiting");
        Match(f, "start");
        f.Game.State = f.Game.State with { Players = 1, Teams = 0 }; // on the team select screen
        await f.Service.Watch.StepAsync(default);
        f.Now += LobbyWatch.Settle - TimeSpan.FromSeconds(1);
        await f.Service.Watch.StepAsync(default);
        Assert.Empty(f.Game.Commands);
        f.Now += TimeSpan.FromSeconds(2);
        await f.Service.Watch.StepAsync(default);
        Assert.Equal(new[] { "/start 5" }, f.Game.Commands);

        await using var g = new HostFixture();
        g.Start();
        Update(g, JoinLobby(g, "Pat"), "waiting");
        Update(g, JoinLobby(g, "Rex"), "waiting");
        Match(g, "start");
        g.Game.State = g.Game.State with { Players = 1, Teams = 1 };
        await g.Service.Watch.StepAsync(default);
        Assert.Empty(g.Game.Commands);
        g.Now += LobbyWatch.Deadline + TimeSpan.FromSeconds(1);
        await g.Service.Watch.StepAsync(default);
        Assert.Equal(new[] { "/start 5" }, g.Game.Commands);
    }

    [Fact]
    public async Task Start_only_from_the_lobby_and_at_once_with_nobody_waiting()
    {
        await using var f = new HostFixture();
        f.Start();
        JoinLobby(f, "Slow");
        var reply = Match(f, "start");
        Assert.Equal((200, "none"), (reply.Status, reply.Json.GetProperty("start").GetString()));
        Assert.Equal(new[] { "/start 5" }, f.Game.Commands);
        Assert.Equal(409, Match(f, "start").Status); // now warming up
        f.Game.Up = false;
        var offline = Match(f, "start");
        Assert.Equal((409, "the game doesn't answer"), (offline.Status, offline.Error));
        Assert.Equal("offline", Status(f).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_start_from_elsewhere_cancel_and_back_to_lobby()
    {
        await using var f = new HostFixture();
        f.Start();
        Update(f, JoinLobby(f, "Pat"), "waiting");
        Match(f, "start");
        f.Game.State = f.Game.State with { Phase = "warmup" }; // the host's own Start, or in-game /admin /start
        await f.Service.Watch.StepAsync(default);
        Assert.Equal("none", Status(f).GetProperty("start").GetString());
        Assert.Empty(f.Game.Commands);

        f.Game.State = f.Game.State with { Phase = "lobby" };
        Match(f, "start");
        Assert.Equal("none", Match(f, null, "DELETE").Json.GetProperty("start").GetString());
        f.Game.State = f.Game.State with { Phase = "playing" };
        Assert.Equal(200, Match(f, "lobby").Status);
        Assert.Equal(new[] { "/lobby now" }, f.Game.Commands);
        Assert.Equal(400, Match(f, "later").Status);
        Assert.Equal(403, Match(f, "start", password: Join).Status);
    }

    [Fact]
    public async Task Status_names_whos_in_the_game_while_anyone_waits()
    {
        await using var f = new HostFixture();
        f.Start();
        f.Game.Names = new() { "Rex", "Player 2" };
        await f.Service.Watch.StepAsync(default);
        Assert.Equal(0, Status(f).GetProperty("inGame").GetArrayLength());
        JoinLobby(f, "Pat");
        await f.Service.Watch.StepAsync(default);
        Assert.Equal(new[] { "Rex", "Player 2" }, Status(f).GetProperty("inGame").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Launchers_sync_and_wait_in_its_lobby()
    {
        await using var f = new HostFixture();
        f.Start();
        var server = new ServerAddress("127.0.0.1", f.Port);
        using var http = ModService.CreateHttp(new Version(0, 3, 0));
        var reply = await ModService.GetManifestAsync(http, server, Join, default);
        Assert.Equal(ModServiceStatus.Ok, reply.Status);
        var layout = new InstallLayout(f.Temp.Sub("player"));
        var plan = ModSyncPlan.Create(reply.Manifest!, layout, new FileHashCache());
        await new ModSyncClient(http, layout, _ => { }).InstallAsync(server, Join, plan, null, default);
        Assert.Equal(f.Lvl, File.ReadAllBytes(Path.Combine(layout.AddOn, @"BF1\data\_lvl_pc\bf1.lvl")));
        Assert.False(File.Exists(Path.Combine(layout.AddOn, @"BF1\setup.exe")));

        var lobby = new LobbyClient(http, server, Join);
        Assert.True(await lobby.JoinAsync("Pat", default));
        await lobby.UpdateAsync(downloading: false, 100, default);
        await f.Service.Watch.StepAsync(default); // the watch loop's first look
        var status = await lobby.StatusAsync(default);
        Assert.True(status.InLobby);
        Assert.Equal("lobby", status.Phase);
        Assert.False(LobbyRules.ShouldLaunch(status, downloading: false));
        await new ModUploadClient(http, server, Admin).StartMatchAsync(default);
        Assert.True(LobbyRules.ShouldLaunch(await lobby.StatusAsync(default), downloading: false));
    }

    private sealed class StuckGame : IGameProbe
    {
        private readonly TaskCompletionSource<GameState> _never = new();
        public Task<GameState> StateAsync(CancellationToken ct) => _never.Task; // ignores ct, as a game that froze
        public Task<IReadOnlyList<string>> NamesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task StartAsync(int countdown, CancellationToken ct) => Task.CompletedTask;
        public Task LobbyNowAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Stopping_closes_the_port_even_when_the_game_stopped_answering()
    {
        using var temp = new TempDir();
        temp.File(@"addons\BF1\addme.script", "addme");
        var service = new ModHostService(new ModHostOptions
        {
            AddOn = temp.Sub("addons"), Hashes = new FileHashCache(), Bind = IPAddress.Loopback, Port = 0,
        }, new StuckGame());
        service.Start();
        int port = service.Port;
        Assert.Equal(200, RawHttp.Send(port, "GET", "/mods.json").Status);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await service.DisposeAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
        Assert.Throws<SocketException>(() => new TcpClient("127.0.0.1", port).Dispose());
    }

    [Theory]
    [InlineData("bytes=5-", 10, 5L, 9L)]
    [InlineData("bytes=-3", 10, 7L, 9L)]
    [InlineData("bytes=9-2", 10, null, null)]
    public void Parses_ranges_as_the_python_service(string header, long size, long? start, long? end)
    {
        var range = ModHostService.ParseRange(header, size);
        Assert.Equal(start, range?.Start);
        Assert.Equal(end, range?.End);
    }
}
