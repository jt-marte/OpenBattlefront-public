using System.Reflection;
using OpenBattlefront.Core;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.ViewModels;

/// <summary>The sample map list shipped for development (--fake-server) and screenshots.</summary>
public static class SampleCatalog
{
    public static MapCatalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenBattlefront.Assets.sample-missions.json")
                           ?? throw new InvalidOperationException("The sample map list is missing.");
        using var reader = new StreamReader(stream);
        return MapCatalog.Parse(reader.ReadToEnd());
    }
}

/// <summary>A pretend server that follows the lobby and rotation rules, so the Server manager
/// can be tried and screenshotted without the game (the --fake-server switch).</summary>
public sealed class FakeServerAdmin : IServerAdmin
{
    private readonly List<PlaylistEntry> _rotation;
    private readonly List<PlayerInfo> _players = new()
    {
        new(1, 1, true, 42, 9, 3, 28, "JayTee"),
        new(2, 2, true, 35, 7, 5, 64, "Rex CT-7567"),
        new(3, 1, false, 18, 4, 6, 91, "Commander Cody"),
        new(5, 2, true, 27, 6, 4, 45, "Wedge"),
        new(6, 0, false, 0, 0, 0, 120, "NewRecruit"),
    };
    private int _current;
    private ServerPhase _phase = ServerPhase.Lobby;
    private bool _hold = true;
    private DateTime _phaseSince = DateTime.UtcNow;
    private ServerPhase _after;

    public FakeServerAdmin(MapCatalog catalog, ServerProfile profile)
    {
        _rotation = profile.Playlist.Select(e => e.Clone()).ToList();
        if (_rotation.Count == 0) _rotation.Add(new PlaylistEntry { Mission = catalog.Maps[0].Missions[0].Mission });
    }

    public event Action<string>? Pushed;
    public event Action<Exception>? Lost { add { } remove { } }

    public bool IsLoggedIn { get; private set; }

    /// <summary>Screenshots: jump to a state.</summary>
    public void Force(ServerPhase phase, int secondsIn = 0)
    {
        _phase = phase;
        _hold = phase == ServerPhase.Lobby;
        _phaseSince = DateTime.UtcNow - TimeSpan.FromSeconds(secondsIn);
    }

    public Task ConnectAsync(string password, CancellationToken ct)
    {
        IsLoggedIn = true;
        return Task.CompletedTask;
    }

    private string Mission => _rotation[Math.Clamp(_current, 0, _rotation.Count - 1)].Mission;
    private int NextIndex => (_current + 1) % _rotation.Count;
    private double Elapsed => (DateTime.UtcNow - _phaseSince).TotalSeconds;

    private void Advance()
    {
        if (_phase == ServerPhase.Shell && Elapsed > 2)
        {
            _phase = _after;
            _phaseSince = DateTime.UtcNow;
            Pushed?.Invoke(_phase == ServerPhase.Lobby ? "lobby: " + Mission : "game started: " + Mission);
        }
        else if (_phase == ServerPhase.Playing && Elapsed > 20 * 60) EndMatch();
        else if (_phase == ServerPhase.Ended && Elapsed > 8) LoadNext(NextIndex);
    }

    private void LoadNext(int index)
    {
        _current = index;
        _phase = ServerPhase.Shell;
        _after = _hold ? ServerPhase.Lobby : ServerPhase.Playing;
        _phaseSince = DateTime.UtcNow;
    }

    private void EndMatch()
    {
        _phase = ServerPhase.Ended;
        _phaseSince = DateTime.UtcNow;
        Pushed?.Invoke("game over");
    }

    public Task<ServerState> StateAsync(CancellationToken ct = default)
    {
        Advance();
        int playing = _players.Count(p => p.Team != 0);
        int timeLeft = _phase == ServerPhase.Playing ? Math.Max(0, 20 * 60 - (int)Elapsed) : -1;
        int tickets = _phase is ServerPhase.Playing or ServerPhase.Ended ? Math.Max(0, 150 - (int)Elapsed / 8) : 150;
        string next = _rotation[NextIndex].Mission;
        return Task.FromResult(new ServerState(_phase, _hold, _players.Count(p => p.Team == 1), _players.Count(p => p.Team == 2), _players.Count,
            timeLeft, tickets, Math.Max(0, tickets - 12), playing * 7, playing * 5, _phase == ServerPhase.Shell ? "" : Mission, next));
    }

    public Task<IReadOnlyList<PlayerInfo>> PlayersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PlayerInfo>>(_players.ToList());

    public Task<IReadOnlyList<RotationEntry>> RotationAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RotationEntry>>(_rotation.Select((e, i) =>
            new RotationEntry(i + 1, e.Mission, e.Tickets1 ?? 0, e.Tickets2 ?? 0, i == _current, i == NextIndex)).ToList());

    public Task SetRotationAsync(IEnumerable<PlaylistEntry> playlist, CancellationToken ct = default)
    {
        var current = _rotation[_current];
        _rotation.Clear();
        _rotation.Add(current);
        _rotation.AddRange(playlist.Select(e => e.Clone()));
        _current = 0;
        return Task.CompletedTask;
    }

    public Task<string> StartAsync(int warmupSeconds = 0, CancellationToken ct = default)
    {
        if (_phase != ServerPhase.Lobby) return Task.FromResult("not in lobby");
        _hold = false;
        _phase = ServerPhase.Playing;
        _phaseSince = DateTime.UtcNow;
        Pushed?.Invoke("match started: " + Mission);
        return Task.FromResult("match started");
    }

    public Task<string> LobbyAsync(bool now, CancellationToken ct = default)
    {
        _hold = true;
        if (_phase == ServerPhase.Lobby) return Task.FromResult("already in lobby");
        if (!now) return Task.FromResult("lobby after this match");
        LoadNext(NextIndex);
        return Task.FromResult("lobby now");
    }

    public Task EndMatchAsync(CancellationToken ct = default)
    {
        EndMatch();
        return Task.CompletedTask;
    }

    public Task NextMapAsync(string mission, CancellationToken ct = default)
    {
        int index = _rotation.FindIndex(NextIndex, e => e.Mission == mission);
        LoadNext(index >= 0 ? index : NextIndex);
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken ct = default)
    {
        LoadNext(_current);
        return Task.CompletedTask;
    }

    public Task KickAsync(int slot, CancellationToken ct = default)
    {
        var player = _players.FirstOrDefault(p => p.Slot == slot);
        if (player != null)
        {
            _players.Remove(player);
            Pushed?.Invoke("removed player " + player.Name);
        }
        return Task.CompletedTask;
    }

    public Task SwapAsync(int slot, CancellationToken ct = default)
    {
        int i = _players.FindIndex(p => p.Slot == slot);
        if (i >= 0 && _players[i].Team is 1 or 2) _players[i] = _players[i] with { Team = 3 - _players[i].Team };
        return Task.CompletedTask;
    }

    public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOptionAsync(string name, int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        IsLoggedIn = false;
        return ValueTask.CompletedTask;
    }
}
