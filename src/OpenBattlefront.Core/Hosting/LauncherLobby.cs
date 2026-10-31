using System.Text.Json;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.Core.Hosting;

/// <summary>A request the hosted mod service turns down: the status code and the {"error": …} text,
/// the same as the Docker server's modserver.py says them.</summary>
public sealed class ModHostRefused : Exception
{
    public ModHostRefused(int status, string error) : base(error) => Status = status;

    public int Status { get; }
}

/// <summary>What the game is doing. Teams counts players on a team: one still loading the map has none.</summary>
public sealed record GameState(string Phase, string Map, string Next, int Players, int Teams)
{
    public static readonly GameState Offline = new("offline", "", "", 0, 0);
}

/// <summary>The hosted game, as the lobby needs it.</summary>
public interface IGameProbe
{
    Task<GameState> StateAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> NamesAsync(CancellationToken ct);
    Task StartAsync(int countdown, CancellationToken ct);
    Task LobbyNowAsync(CancellationToken ct);
}

/// <summary>The host screen's own admin connection to its server (RconClient sends one command at a
/// time, so the lobby can share it).</summary>
public sealed class AdminGameProbe : IGameProbe
{
    private readonly IServerAdmin _admin;

    public AdminGameProbe(IServerAdmin admin) => _admin = admin;

    public async Task<GameState> StateAsync(CancellationToken ct)
    {
        var state = await _admin.StateAsync(ct).ConfigureAwait(false);
        string phase = state.Phase switch
        {
            ServerPhase.Lobby => "lobby",
            ServerPhase.Warmup => "warmup",
            ServerPhase.Playing => "playing",
            ServerPhase.Ended => "ended",
            _ => "shell",
        };
        return new GameState(phase, state.Map, state.Next, state.Players, state.Team1 + state.Team2);
    }

    public async Task<IReadOnlyList<string>> NamesAsync(CancellationToken ct) =>
        (await _admin.PlayersAsync(ct).ConfigureAwait(false)).Select(p => p.DisplayName).ToList();

    public Task StartAsync(int countdown, CancellationToken ct) => _admin.StartAsync(countdown, ct);

    public Task LobbyNowAsync(CancellationToken ct) => _admin.LobbyAsync(now: true, ct);
}

/// <summary>Players waiting in their launchers (their game isn't running yet) for the match to
/// start. An entry goes 20 s after its launcher last asked for /status. As modserver.py's Lobby.</summary>
public sealed class LauncherLobby
{
    public const int Max = 64;
    public const int MaxPerAddress = 16;
    public static readonly TimeSpan Expire = TimeSpan.FromSeconds(20);

    private sealed class Entry
    {
        public required string Name { get; init; }
        public required string Address { get; init; }
        public string State { get; set; } = "downloading";
        public int Progress { get; set; }
        public DateTime Seen { get; set; }
    }

    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _lock = new();

    public LauncherLobby(Func<DateTime> clock) => _clock = clock;

    private void Prune()
    {
        var now = _clock();
        foreach (var token in _entries.Where(e => now - e.Value.Seen > Expire).Select(e => e.Key).ToList())
            _entries.Remove(token);
    }

    public string Join(JsonElement name, string address)
    {
        string? text = name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        if (!PlayerProfiles.IsValid(text)) throw new ModHostRefused(400, "a player name is 1 to 15 plain characters");
        lock (_lock)
        {
            Prune();
            if (_entries.Count >= Max) throw new ModHostRefused(503, "the lobby is full");
            if (_entries.Values.Count(e => e.Address == address) >= MaxPerAddress) throw new ModHostRefused(429, "too many players from one address");
            string token = Guid.NewGuid().ToString("N");
            _entries[token] = new Entry { Name = text!, Address = address, Seen = _clock() };
            return token;
        }
    }

    public void Update(JsonElement token, JsonElement state, JsonElement progress)
    {
        string? value = state.ValueKind == JsonValueKind.String ? state.GetString() : null;
        if (value is not ("downloading" or "waiting")) throw new ModHostRefused(400, "state is downloading or waiting");
        lock (_lock)
        {
            var entry = Find(token);
            entry.State = value;
            entry.Progress = progress.ValueKind == JsonValueKind.Number && progress.TryGetDouble(out double p) ? (int)Math.Clamp(p, 0, 100) : 0;
            entry.Seen = _clock();
        }
    }

    private Entry Find(JsonElement token)
    {
        string? key = token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        if (key == null || !_entries.TryGetValue(key, out var entry)) throw new ModHostRefused(404, "not in the lobby; join again");
        return entry;
    }

    public bool Touch(string? token)
    {
        lock (_lock)
        {
            Prune();
            if (token == null || !_entries.TryGetValue(token, out var entry)) return false;
            entry.Seen = _clock();
            return true;
        }
    }

    public void Leave(JsonElement token)
    {
        lock (_lock)
        {
            if (token.ValueKind == JsonValueKind.String) _entries.Remove(token.GetString()!);
        }
    }

    public IReadOnlyList<LobbyPlayer> List()
    {
        lock (_lock)
        {
            Prune();
            return _entries.Values.Select(e => new LobbyPlayer(e.Name, e.State, e.Progress)).ToList();
        }
    }

    public int Count(string? state = null)
    {
        lock (_lock)
        {
            Prune();
            return _entries.Values.Count(e => state == null || e.State == state);
        }
    }
}

/// <summary>What the game is doing, read every 2 s while a launcher waits (else every 15 s), and the
/// lobby start: Start opens every waiting player's game into the server's held lobby, and the match
/// begins once that many players have loaded (they are on a team), 15 s after that many are
/// connected, or after 60 s. Players still downloading don't hold it up. As modserver.py's GameWatch.</summary>
public sealed class LobbyWatch
{
    public static readonly TimeSpan Busy = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan NamesEvery = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    public const int Countdown = 5;

    private readonly IGameProbe? _probe;
    private readonly LauncherLobby _lobby;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private GameState _state = GameState.Offline;
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private DateTime? _polled, _named, _connected;
    private bool _loading;
    private DateTime _deadline;
    private int _expected;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LobbyWatch(IGameProbe? probe, LauncherLobby lobby, Func<DateTime> clock)
    {
        _probe = probe;
        _lobby = lobby;
        _clock = clock;
    }

    public bool IsBusy
    {
        get
        {
            lock (_lock) if (_loading) return true;
            return _lobby.Count() > 0;
        }
    }

    public GameState State
    {
        get { lock (_lock) return _state; }
    }

    public void Wake() => _wake.TrySetResult();

    public async Task RefreshAsync(CancellationToken ct)
    {
        GameState? state = null;
        if (_probe != null)
        {
            try { state = await _probe.StateAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        var now = _clock();
        lock (_lock)
        {
            _state = state ?? GameState.Offline;
            _polled = now;
        }
        bool namesDue;
        lock (_lock) namesDue = _named == null || now - _named >= NamesEvery;
        if (state == null || !IsBusy || !namesDue) return;
        IReadOnlyList<string>? names = null;
        try { names = await _probe!.NamesAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { }
        lock (_lock)
        {
            if (names != null) _names = names;
            _named = now;
        }
    }

    /// <summary>One look at the game, then the start once everyone is in (the loop calls it; tests too).</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_loading) return;
            if (_state.Phase != "lobby")
            {
                _loading = false; // started some other way, or the server changed map
                return;
            }
            var now = _clock();
            if (_state.Players >= _expected) _connected ??= now;
            else _connected = null;
            bool loaded = _state.Teams >= _expected;
            bool settled = _connected is { } since && now - since >= Settle;
            if (!(loaded || settled || now >= _deadline)) return;
            _loading = false;
        }
        await SendStartAsync(ct).ConfigureAwait(false);
    }

    private async Task SendStartAsync(CancellationToken ct)
    {
        try
        {
            if (_probe != null) await _probe.StartAsync(Countdown, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The game stopped answering; the next look says so.
        }
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await StepAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            var wake = _wake;
            try { await Task.WhenAny(wake.Task, Task.Delay(IsBusy ? Busy : Idle, ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (wake.Task.IsCompleted) _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Everyone connected, or null while the game doesn't answer.</summary>
    public int? Players
    {
        get { lock (_lock) return _state.Phase == "offline" ? null : _state.Players; }
    }

    public async Task<Dictionary<string, object?>> StartAsync(CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        bool now;
        lock (_lock)
        {
            if (_state.Phase != "lobby")
                throw new ModHostRefused(409, _state.Phase != "offline" ? "the match isn't waiting in the lobby" : "the game doesn't answer");
            if (_loading) throw new ModHostRefused(409, "the start is already loading everyone's game");
            int waiting = _lobby.Count("waiting");
            _expected = _state.Players + waiting;
            now = waiting == 0;
            if (!now)
            {
                _loading = true;
                _connected = null;
                _deadline = _clock() + Deadline;
            }
        }
        if (now) await SendStartAsync(ct).ConfigureAwait(false);
        Wake();
        return Describe();
    }

    public Dictionary<string, object?> Cancel()
    {
        lock (_lock) _loading = false;
        return Describe();
    }

    public async Task<Dictionary<string, object?>> BackToLobbyAsync(CancellationToken ct)
    {
        if (_probe == null) throw new ModHostRefused(503, "the game doesn't answer");
        try { await _probe.LobbyNowAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new ModHostRefused(503, "the game doesn't answer"); }
        Wake();
        return new Dictionary<string, object?> { ["reply"] = new[] { "lobby now" } };
    }

    public Dictionary<string, object?> Describe()
    {
        lock (_lock)
        {
            int? left = _loading ? Math.Max(0, (int)(_deadline - _clock()).TotalSeconds) : null;
            return new Dictionary<string, object?>
            {
                ["phase"] = _state.Phase,
                ["map"] = _state.Map,
                ["next"] = _state.Next,
                ["players"] = _state.Players,
                ["teams"] = _state.Teams,
                ["inGame"] = _names.ToList(),
                ["start"] = _loading ? "loading" : "none",
                ["startIn"] = left,
            };
        }
    }
}
