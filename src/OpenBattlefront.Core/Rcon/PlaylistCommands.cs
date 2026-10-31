namespace OpenBattlefront.Core.Rcon;

public static class PlaylistCommands
{
    /// <summary>The rotation as admin commands: /clearmaps, then /appendmap per entry. Each fits
    /// in one admin line with its "/admin " prefix (63 characters).</summary>
    public static IReadOnlyList<string> ForRotation(IEnumerable<PlaylistEntry> playlist)
    {
        var commands = new List<string> { "/clearmaps" };
        foreach (var entry in playlist)
        {
            if (!PlaylistEntry.IsValidMission(entry.Mission)) throw new ArgumentException($"'{entry.Mission}' is not a mission name.");
            commands.Add("/appendmap " + entry.ToArgument());
        }
        return commands;
    }

    public static bool FitsOneLine(string command) => RconPacket.AdminPrefix.Length + command.Length <= RconPacket.LineMax;
}
