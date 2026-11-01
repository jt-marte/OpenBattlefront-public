using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBattlefront.Core;

/// <summary>A playlist entry as a server's mod service keeps it.</summary>
public sealed class SavedMap
{
    public string Mission { get; set; } = "";
    public int? Tickets1 { get; set; }
    public int? Tickets2 { get; set; }
}

/// <summary>What a server keeps for its next game start (the Docker server's /admin/settings):
/// any of the playlist, the game options, the name, the player limit and the tick rate. A
/// setting left null isn't sent, so the server keeps what it has for it.</summary>
public sealed class AdminSettings
{
    public string? Name { get; set; }
    public int? MaxPlayers { get; set; }
    public int? TickRate { get; set; }
    public List<SavedMap>? Maps { get; set; }
    public Dictionary<string, int>? Options { get; set; }

    public static AdminSettings ForPlaylist(IEnumerable<PlaylistEntry> playlist) => new()
    {
        Maps = playlist.Select(e => new SavedMap { Mission = e.Mission, Tickets1 = e.Tickets?.Item1, Tickets2 = e.Tickets?.Item2 }).ToList(),
    };

    public static AdminSettings ForOptions(ServerProfile profile) => new() { Options = profile.Options() };

    public static AdminSettings ForServer(ServerProfile profile) => new()
    {
        Name = profile.Name,
        MaxPlayers = profile.MaxPlayers,
        TickRate = profile.TickRate,
    };

    /// <summary>Puts the name, limits, playlist and options that are set into a profile.</summary>
    public void ApplyTo(ServerProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(Name)) profile.Name = Name;
        if (MaxPlayers is int players) profile.MaxPlayers = players;
        if (TickRate is int tick) profile.TickRate = tick;
        if (Maps != null)
            profile.Playlist = Maps.Where(m => PlaylistEntry.IsValidMission(m.Mission))
                .Select(m => new PlaylistEntry { Mission = m.Mission, Tickets1 = m.Tickets1, Tickets2 = m.Tickets2 }).ToList();
        if (Options != null)
            foreach (var (command, value) in Options)
                profile.SetOption(command, value);
        profile.Normalize();
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>GET /admin/settings: what the next game start uses, which of it an admin saved (the
/// rest is compose.yaml's), compose.yaml's own values, and whether the running game needs a
/// restart for its name or limits.</summary>
public sealed class AdminSettingsReply
{
    public int Schema { get; set; }
    public AdminSettings Settings { get; set; } = new();

    /// <summary>"maps", "name", "options.difficulty" …</summary>
    public List<string> Saved { get; set; } = new();
    public AdminSettings Compose { get; set; } = new();
    public bool RestartNeeded { get; set; }

    public bool HasSaved => Saved.Count > 0;

    public static AdminSettingsReply Parse(string json)
    {
        AdminSettingsReply? reply;
        try { reply = JsonSerializer.Deserialize<AdminSettingsReply>(json, AdminSettings.Json); }
        catch (JsonException e) { throw new InvalidDataException("The server's settings are not valid JSON.", e); }
        if (reply is not { Schema: 1 }) throw new InvalidDataException("The server's settings have a format this launcher doesn't know; update the launcher.");
        return reply;
    }
}
