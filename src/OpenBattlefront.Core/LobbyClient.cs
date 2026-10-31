using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenBattlefront.Core;

/// <param name="State">"waiting" (mods in place) or "downloading".</param>
/// <param name="Progress">0 to 100 while downloading.</param>
public sealed record LobbyPlayer(string Name, string State, int Progress)
{
    public bool IsDownloading => State == "downloading";
}

/// <summary>GET /status from a server's mod service: what the game is doing, and who waits in the
/// launchers' lobby.</summary>
public sealed class MatchStatus
{
    public static readonly IReadOnlyList<string> Phases = new[] { "offline", "shell", "lobby", "warmup", "playing", "ended" };

    public int Schema { get; set; }
    public string Server { get; set; } = "";

    /// <summary>offline (the game doesn't answer), shell, lobby (held until Start), warmup, playing or ended.</summary>
    public string Phase { get; set; } = "offline";
    public string Map { get; set; } = "";
    public string Next { get; set; } = "";

    /// <summary>Everyone connected to the game.</summary>
    public int Players { get; set; }
    public List<string> InGame { get; set; } = new();
    public List<LobbyPlayer> Lobby { get; set; } = new();

    /// <summary>"loading": Start was pressed and waiting players' games are opening.</summary>
    public string Start { get; set; } = "none";

    /// <summary>Seconds left before a loading start goes ahead anyway.</summary>
    public int? StartIn { get; set; }

    /// <summary>The asking launcher's lobby entry is still there.</summary>
    public bool InLobby { get; set; }

    public bool IsLoading => Start == "loading";

    public static MatchStatus Parse(string json)
    {
        MatchStatus? status;
        try { status = JsonSerializer.Deserialize<MatchStatus>(json, ServerModsManifest.Options); }
        catch (JsonException e) { throw new InvalidDataException("The server's status is not valid JSON.", e); }
        if (status == null || status.Schema != 1) throw new InvalidDataException("The server's status has a format this launcher doesn't know; update the launcher.");
        if (!Phases.Contains(status.Phase)) status.Phase = "offline";
        status.Server = Clean(status.Server, 63);
        status.Map = Clean(status.Map, 63);
        status.Next = Clean(status.Next, 63);
        status.InGame = status.InGame.Take(64).Select(n => Clean(n, 32)).ToList();
        status.Lobby = status.Lobby.Take(64).Select(p => p with { Name = Clean(p.Name, 15), Progress = Math.Clamp(p.Progress, 0, 100) }).ToList();
        return status;
    }

    /// <summary>Printable text of a sane length: names come from other players.</summary>
    private static string Clean(string? text, int max)
    {
        var kept = new string((text ?? "").Where(c => c >= ' ' && c != '\u007f').Take(max).ToArray());
        return kept.Trim();
    }
}

public static class LobbyRules
{
    /// <summary>When a player waiting in the launcher's lobby opens the game: once Start loads
    /// everyone, or as soon as the match runs (it started elsewhere, or the server has no lobby).
    /// Never while their mods are still downloading: they join when done.</summary>
    public static bool ShouldLaunch(MatchStatus status, bool downloading) =>
        !downloading && (status.IsLoading || status.Phase is "warmup" or "playing");
}

/// <summary>A player's side of the launchers' lobby on a server's mod service: join with the player
/// name, say how the download goes, read the status (which keeps the entry), leave.</summary>
public sealed class LobbyClient
{
    private readonly HttpClient _http;
    private readonly string? _password;

    public LobbyClient(HttpClient http, ServerAddress server, string? password)
    {
        _http = http;
        Server = server;
        _password = password;
    }

    public ServerAddress Server { get; }
    public string? Token { get; private set; }

    /// <summary>False when the server's service has no lobby (an older one).</summary>
    public async Task<bool> JoinAsync(string name, CancellationToken ct)
    {
        using var response = await PostAsync("lobby/join", new { name }, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw await ModService.ErrorAsync(response, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        Token = doc.RootElement.GetProperty("token").GetString();
        return Token != null;
    }

    public async Task UpdateAsync(bool downloading, int progress, CancellationToken ct)
    {
        if (Token == null) return;
        using var response = await PostAsync("lobby/update", new { token = Token, state = downloading ? "downloading" : "waiting", progress }, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ModService.ErrorAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<MatchStatus> StatusAsync(CancellationToken ct)
    {
        string path = Token == null ? "status" : $"status?token={Uri.EscapeDataString(Token)}";
        using var request = ModService.Request(HttpMethod.Get, Server, path, _password);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ModService.ErrorAsync(response, ct).ConfigureAwait(false);
        return MatchStatus.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task LeaveAsync(CancellationToken ct)
    {
        if (Token == null) return;
        string token = Token;
        Token = null;
        try
        {
            using var _ = await PostAsync("lobby/leave", new { token }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The entry goes by itself 20 s later.
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, CancellationToken ct)
    {
        using var request = ModService.Request(HttpMethod.Post, Server, path, _password);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }
}
