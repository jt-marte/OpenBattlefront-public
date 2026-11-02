using System.Net;
using System.Net.Sockets;

namespace OpenBattlefront.Core.Rcon;

public sealed class RconException : Exception
{
    public RconException(string message) : base(message) { }
}

/// <summary>
/// Remote admin over the game port (UDP), in the retail packet format (<see cref="RconPacket"/>).
/// Hello gets the admin key, a heartbeat takes one of the server's three admin slots and keeps it
/// (every 15 s; a slot idle for 60 s can be taken), then /login. The login lasts across map
/// changes, so it is sent again only when the server says "not logged in". Commands go one at a
/// time; a reply group that isn't the answer to the one in flight is a pushed message (chat, game
/// started, players joining) and goes to <see cref="Pushed"/>.
/// </summary>
public sealed class RconClient : IServerAdmin
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>The server takes 5 logins from one address in this long and refuses the rest.</summary>
    public static readonly TimeSpan LoginRateWindow = TimeSpan.FromSeconds(10);

    private readonly IPEndPoint _server;
    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly RconAssembler _assembler = new();
    private Task? _receiving;
    private Task? _beating;
    private Pending? _pending;
    private int _group = Random.Shared.Next(1, 60000);
    private byte _key;
    private string? _password;
    private int _logins;
    private DateTime _loginAllowedAt;

    public RconClient(IPEndPoint server)
    {
        _server = server;
        // A loopback socket for a server on this PC, so the launcher never asks the firewall.
        var local = IPAddress.IsLoopback(server.Address) ? new IPEndPoint(IPAddress.Loopback, 0) : new IPEndPoint(IPAddress.Any, 0);
        _udp = new UdpClient(local);
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException) { }
        _udp.Connect(server);
    }

    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A line the server sent on its own: chat, game started/over, players joining.</summary>
    public event Action<string>? Pushed;

    /// <summary>Heartbeats stopped getting answers (the server stopped or restarted).</summary>
    public event Action<Exception>? Lost;

    public bool IsLoggedIn { get; private set; }
    public IPEndPoint Server => _server;

    /// <summary>Hello, heartbeat, login. Throws <see cref="RconException"/> on a wrong password.</summary>
    public async Task ConnectAsync(string password, CancellationToken ct)
    {
        _receiving ??= Task.Run(ReceiveLoopAsync);
        await TakeSlotAsync(ct);
        await LoginAsync(password, ct);
        _beating ??= Task.Run(HeartbeatLoopAsync);
    }

    public async Task LoginAsync(string password, CancellationToken ct)
    {
        // After "login rate limited" the server refuses every login until its window is over, so a
        // poll asking again before then gets the same answer here
        if (DateTime.UtcNow < _loginAllowedAt) throw RateLimited();
        // One datagram, given the time of three tries: a busy server answers every copy it was sent
        // (late), and each copy counts as a login
        string line = RconPacket.AdminPrefix + "/login " + password;
        var reply = await RequestAsync((group, key) => RconPacket.EncodeLine(group, key, line), r => r.Lines.Any(IsLoginLine),
            sends: 1, ReplyTimeout * 3, ct);
        if (reply.Lines.Contains("logged in"))
        {
            _password = password;
            IsLoggedIn = true;
            Interlocked.Increment(ref _logins);
            return;
        }
        IsLoggedIn = false;
        if (!reply.Lines.Contains("login rate limited")) throw new RconException("The server didn't accept the admin password.");
        _loginAllowedAt = DateTime.UtcNow + LoginRateWindow;
        throw RateLimited();
    }

    private static RconException RateLimited() => new("Too many login attempts. Wait ten seconds and try again.");

    private static bool IsLoginLine(string line) => line is "logged in" or "login failed" or "login rate limited";

    /// <summary>A late answer to a hello, a login or a line sent without one: the server never
    /// pushes these by itself.</summary>
    private static bool IsSessionAnswer(RconReply reply) =>
        reply.Lines.Count > 0 && reply.Lines.All(l => l is "hello" or "not logged in" || IsLoginLine(l));

    // Typed commands. The new ones reply with a "#command" section; the retail ones with free text.

    public async Task<ServerState> StateAsync(CancellationToken ct = default) =>
        RconReplies.ParseState(await TaggedAsync("state", "/state", retry: true, ct));

    public async Task<IReadOnlyList<PlayerInfo>> PlayersAsync(CancellationToken ct = default)
    {
        var players = new List<PlayerInfo>();
        int? first = null;
        for (int page = 0; page < 8; page++)
        {
            var reply = RconReplies.ParsePlayers(await TaggedAsync("playerinfo", first == null ? "/playerinfo" : $"/playerinfo {first}", retry: true, ct));
            players.AddRange(reply.Rows);
            if (reply.More is not int more || more <= (first ?? 0)) break;
            first = more;
        }
        return players;
    }

    public async Task<IReadOnlyList<RotationEntry>> RotationAsync(CancellationToken ct = default)
    {
        var entries = new List<RotationEntry>();
        int? first = null;
        for (int page = 0; page < 16; page++)
        {
            var reply = RconReplies.ParseRotation(await TaggedAsync("maplist", first == null ? "/maplist" : $"/maplist {first}", retry: true, ct));
            entries.AddRange(reply.Rows);
            if (reply.More is not int more || more <= (first ?? 0)) break;
            first = more;
        }
        return entries;
    }

    /// <summary>/clearmaps, then one /appendmap per entry. The map being played stays until it ends.</summary>
    public async Task SetRotationAsync(IEnumerable<PlaylistEntry> playlist, CancellationToken ct = default)
    {
        foreach (string command in PlaylistCommands.ForRotation(playlist))
        {
            var lines = await TaggedAsync(command.Split(' ')[0].TrimStart('/'), command, retry: false, ct);
            string? problem = lines.FirstOrDefault(l => l.Contains("not added") || l == "rotation full" || l == "no map");
            if (problem != null) throw new RconException($"The server refused the playlist: {problem}.");
        }
    }

    public async Task<string> StartAsync(int warmupSeconds = 0, CancellationToken ct = default) =>
        Result(await TaggedAsync("start", warmupSeconds > 0 ? $"/start {warmupSeconds}" : "/start", retry: false, ct), "start");

    public async Task<string> LobbyAsync(bool now, CancellationToken ct = default) =>
        Result(await TaggedAsync("lobby", now ? "/lobby now" : "/lobby", retry: false, ct), "lobby");

    public Task EndMatchAsync(CancellationToken ct = default) => CommandAsync("/endgame", ct);

    /// <summary>/changemap needs the mission to go to: the rotation's next entry.</summary>
    public Task NextMapAsync(string mission, CancellationToken ct = default) =>
        PlaylistEntry.IsValidMission(mission) ? CommandAsync("/changemap " + mission, ct) : EndMatchAsync(ct);

    public Task RestartAsync(CancellationToken ct = default) => CommandAsync("/restart", ct);
    public Task KickAsync(int slot, CancellationToken ct = default) => CommandAsync($"/kick {slot}", ct);
    public Task SwapAsync(int slot, CancellationToken ct = default) => CommandAsync($"/swap {slot}", ct);

    public Task SayAsync(string text, CancellationToken ct = default)
    {
        string clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        int room = RconPacket.LineMax - RconPacket.AdminPrefix.Length - "/say ".Length;
        return CommandAsync("/say " + (clean.Length > room ? clean[..room] : clean), ct);
    }

    public Task SetOptionAsync(string name, int value, CancellationToken ct = default) => CommandAsync($"/{name} {value}", ct);

    public async Task<int?> ReadOptionAsync(string name, CancellationToken ct = default) =>
        RconReplies.ParseNumber(await ReadAsync(name, ct));

    public async Task<string?> ReadTextAsync(string name, CancellationToken ct = default) =>
        (await ReadAsync(name, ct)).FirstOrDefault()?.Trim();

    /// <summary>A setting with no value given: the game answers with what it is. Reading changes
    /// nothing, so it can be asked again.</summary>
    private Task<IReadOnlyList<string>> ReadAsync(string name, CancellationToken ct) =>
        SendRawAsync("/" + name, r => !IsLateAnswer(r) && !RconReplies.LooksLikePush(r.Lines), retry: true, ct);

    public Task ShutdownAsync(CancellationToken ct = default) => CommandAsync("/shutdown", ct);

    /// <summary>A retail command. Its reply is free text, so take the first group that isn't a
    /// pushed message; the commands that change the game are never retried.</summary>
    public Task<IReadOnlyList<string>> CommandAsync(string command, CancellationToken ct = default) =>
        SendRawAsync(command, r => !IsLateAnswer(r) && !RconReplies.LooksLikePush(r.Lines), retry: false, ct);

    private static string Result(IReadOnlyList<string> lines, string command) =>
        RconReplies.Section(lines, command)?.FirstOrDefault() ?? "";

    /// <summary>The reply with this command's "#tag" section, or (from a build without the
    /// command) a plain refusal such as "command not found".</summary>
    private Task<IReadOnlyList<string>> TaggedAsync(string tag, string command, bool retry, CancellationToken ct) =>
        SendRawAsync(command, r => RconReplies.HasSection(r.Lines, tag) || r.Lines.Count > 0 && !IsLateAnswer(r) && !RconReplies.LooksLikePush(r.Lines), retry, ct);

    /// <summary>A "#command" reply nobody is waiting for: the answer to a request that timed out
    /// (the server stalls while it loads a map, then answers every copy it was sent).</summary>
    private static bool IsLateAnswer(RconReply reply) => reply.Lines.Count > 0 && reply.Lines[0].StartsWith('#');

    private async Task<IReadOnlyList<string>> SendRawAsync(string command, Func<RconReply, bool> isReply, bool retry, CancellationToken ct)
    {
        string line = RconPacket.AdminPrefix + command;
        bool canRelogin = _password != null && !command.StartsWith("/login ", StringComparison.Ordinal);
        int logins = _logins;
        RconReply reply;
        try
        {
            reply = await RequestAsync((group, key) => RconPacket.EncodeLine(group, key, line), isReply, retry, ct);
        }
        catch (TimeoutException) when (canRelogin)
        {
            // No answer: the server is busy (it stalls while it loads a map, then answers every copy
            // it was sent), gave this admin slot to another admin, or restarted with a new key. Take
            // the key and a slot again, but don't log in: the login outlasts a map change, and a
            // server that lost it says so below. A command that changes the game is not sent twice.
            await TakeSlotAsync(ct);
            if (!retry) throw;
            reply = await RequestAsync((group, key) => RconPacket.EncodeLine(group, key, line), isReply, retry, ct);
        }
        if (reply.Lines.Contains("not logged in") && canRelogin)
        {
            await ReloginAsync(logins, ct);
            reply = await RequestAsync((group, key) => RconPacket.EncodeLine(group, key, line), isReply, retry, ct);
        }
        return reply.Lines;
    }

    /// <summary>Hello answers with the current admin key whatever key it carries; a heartbeat takes
    /// one of the server's admin slots, or keeps this one's. Neither logs in or out.</summary>
    private async Task TakeSlotAsync(CancellationToken ct)
    {
        await RequestAsync((group, _) => RconPacket.EncodeHello(group), r => r.Lines.Contains("hello"), retry: true, ct);
        await RequestAsync(RconPacket.EncodeHeartbeat, r => r.Lines.Count == 0, retry: true, ct);
    }

    /// <summary>Logs in once for every request that was told "not logged in": one that logged in
    /// after this request went out did it for both (the polls and a command run side by side).</summary>
    private async Task ReloginAsync(int logins, CancellationToken ct)
    {
        await _loginGate.WaitAsync(ct);
        try
        {
            if (_logins != logins) return;
            IsLoggedIn = false;
            await LoginAsync(_password!, ct);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private Task<RconReply> RequestAsync(Func<int, byte, byte[]> packet, Func<RconReply, bool> isReply, bool retry, CancellationToken ct) =>
        RequestAsync(packet, isReply, retry ? 3 : 1, ReplyTimeout, ct);

    /// <param name="packet">Builds the datagram from a new group number and the current key.</param>
    /// <param name="sends">How many times it goes out, <paramref name="wait"/> apart, until answered.</param>
    private async Task<RconReply> RequestAsync(Func<int, byte, byte[]> packet, Func<RconReply, bool> isReply, int sends, TimeSpan wait, CancellationToken ct)
    {
        _receiving ??= Task.Run(ReceiveLoopAsync);
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; attempt < sends; attempt++)
            {
                var pending = new Pending(isReply);
                _pending = pending;
                _group = (_group + 1) & 0xFFFF;
                await _udp.SendAsync(packet(_group, _key), ct);
                var done = await Task.WhenAny(pending.Done.Task, Task.Delay(wait, ct));
                ct.ThrowIfCancellationRequested();
                if (done == pending.Done.Task) return await pending.Done.Task;
            }
            throw new TimeoutException($"The server at {_server} didn't answer.");
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (!RconPacket.TryReadFragment(result.Buffer, out var fragment) || fragment.Type != RconPacket.ReplyType) continue;
            if (_assembler.Add(fragment) is not { } payload || RconPacket.DecodeReply(payload) is not { } reply) continue;
            _key = reply.Key;
            var pending = _pending;
            if (pending != null && pending.IsReply(reply) && pending.Done.TrySetResult(reply)) continue;
            if (IsLateAnswer(reply) || IsSessionAnswer(reply)) continue;
            foreach (string line in reply.Lines) Pushed?.Invoke(line);
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        int misses = 0;
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, _stop.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                await RequestAsync(RconPacket.EncodeHeartbeat, r => r.Lines.Count == 0, retry: true, _stop.Token);
                misses = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is TimeoutException or SocketException)
            {
                if (++misses == 2) Lost?.Invoke(e);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        foreach (var task in new[] { _receiving, _beating })
        {
            if (task == null) continue;
            try { await task; }
            catch (Exception) { }
        }
        _stop.Dispose();
    }

    private sealed class Pending
    {
        public Pending(Func<RconReply, bool> isReply) => IsReply = isReply;
        public Func<RconReply, bool> IsReply { get; }
        public TaskCompletionSource<RconReply> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
