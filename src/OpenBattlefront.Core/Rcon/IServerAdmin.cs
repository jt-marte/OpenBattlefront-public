namespace OpenBattlefront.Core.Rcon;

/// <summary>Controls a running server: <see cref="RconClient"/> over the network, or a fake one
/// for screenshots and development.</summary>
public interface IServerAdmin : IAsyncDisposable
{
    /// <summary>A line the server sent on its own: chat, game started/over, players joining.</summary>
    event Action<string>? Pushed;

    /// <summary>The server stopped answering.</summary>
    event Action<Exception>? Lost;

    bool IsLoggedIn { get; }

    Task ConnectAsync(string password, CancellationToken ct);
    Task<ServerState> StateAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlayerInfo>> PlayersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RotationEntry>> RotationAsync(CancellationToken ct = default);
    Task SetRotationAsync(IEnumerable<PlaylistEntry> playlist, CancellationToken ct = default);
    Task<string> StartAsync(int warmupSeconds = 0, CancellationToken ct = default);
    Task<string> LobbyAsync(bool now, CancellationToken ct = default);
    Task EndMatchAsync(CancellationToken ct = default);
    Task NextMapAsync(string mission, CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);
    Task KickAsync(int slot, CancellationToken ct = default);
    Task SwapAsync(int slot, CancellationToken ct = default);
    Task SayAsync(string text, CancellationToken ct = default);
    Task SetOptionAsync(string name, int value, CancellationToken ct = default);

    /// <summary>An option's value now (/name with no value answers with its number), or null.</summary>
    Task<int?> ReadOptionAsync(string name, CancellationToken ct = default);

    /// <summary>A text setting such as /gamename, or null.</summary>
    Task<string?> ReadTextAsync(string name, CancellationToken ct = default);

    Task ShutdownAsync(CancellationToken ct = default);
}
