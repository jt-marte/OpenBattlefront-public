namespace OpenBattlefront.Core.Rcon;

/// <summary>A running server's settings, read over rcon: what Connect to server as admin shows
/// before the admin changes anything.</summary>
public static class ServerReader
{
    /// <summary>The rotation, the game options, the name, the player limit and the tick rate. A
    /// setting the server doesn't answer keeps the launcher's default.</summary>
    public static async Task<ServerProfile> ReadAsync(IServerAdmin admin, CancellationToken ct = default)
    {
        var profile = new ServerProfile { AnsweredOptions = new(StringComparer.OrdinalIgnoreCase) };
        foreach (string command in profile.Options().Keys)
            if (await admin.ReadOptionAsync(command, ct).ConfigureAwait(false) is int value)
            {
                profile.SetOption(command, value);
                profile.AnsweredOptions.Add(command);
            }
        if (await admin.ReadTextAsync("gamename", ct).ConfigureAwait(false) is { Length: > 0 } name) profile.Name = name;
        if (await admin.ReadOptionAsync("playerlimit", ct).ConfigureAwait(false) is int limit) profile.MaxPlayers = limit;
        if (await admin.ReadOptionAsync("tps", ct).ConfigureAwait(false) is int tick) profile.TickRate = tick;
        profile.Playlist = Playlist(await admin.RotationAsync(ct).ConfigureAwait(false));
        profile.Normalize();
        return profile;
    }

    /// <summary>/maplist as a playlist; 0 tickets is the map's own number.</summary>
    public static List<PlaylistEntry> Playlist(IEnumerable<RotationEntry> rotation) =>
        rotation.Where(r => PlaylistEntry.IsValidMission(r.Mission)).Select(r => new PlaylistEntry
        {
            Mission = r.Mission,
            Tickets1 = r.Tickets1 > 0 ? r.Tickets1 : null,
            Tickets2 = r.Tickets2 > 0 ? r.Tickets2 : null,
        }).ToList();
}
