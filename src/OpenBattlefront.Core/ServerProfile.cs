using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace OpenBattlefront.Core;

/// <summary>A dedicated server's settings, saved in launcher.json. The Server manager's options
/// screen edits it; <see cref="ServerHost"/> turns it into ServerSettings.cfg and arguments.</summary>
public sealed class ServerProfile
{
    public const int DefaultPort = 3658;

    /// <summary>The only tick rates the game runs at. 0 is never passed: it is a parser pitfall.</summary>
    public static readonly IReadOnlyList<int> TickRates = new[] { 30, 60, 120 };

    public const int HeroUnlockPoints = 3;
    public const int HeroUnlockTimer = 5;
    public const int HeroPlayerBest = 1;
    public const int HeroPlayerRandom = 7;

    public string Name { get; set; } = "OpenSWBF2 Server";
    public string JoinPassword { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public int MaxPlayers { get; set; } = 64;
    public int TickRate { get; set; } = 60;
    public string AdminPassword { get; set; } = NewAdminPassword();

    public int BotsPerTeam { get; set; } = 16;

    /// <summary>2 Medium, 3 Hard, 4 Overhaul (the AI overhaul's third difficulty).</summary>
    public int Difficulty { get; set; } = 3;
    public bool TeamDamage { get; set; } = true;
    public bool ShowNames { get; set; } = true;
    public bool AutoAssignTeams { get; set; } = true;
    public bool Awards { get; set; } = true;

    public bool Heroes { get; set; } = true;
    public int HeroUnlock { get; set; } = HeroUnlockPoints;
    /// <summary>Points (1-50) or seconds (0-120), as Instant Action offers them.</summary>
    public int HeroUnlockValue { get; set; } = 10;
    public int HeroPlayer { get; set; } = HeroPlayerBest;

    /// <summary>Per cent of the map's reinforcements, 10-500.</summary>
    public int ConquestReinforcements { get; set; } = 100;
    /// <summary>Minutes, 0 = no limit.</summary>
    public int ConquestTimeLimit { get; set; }
    public int CtfCaptures { get; set; } = 5;
    public int CtfTimeLimit { get; set; }
    public int HuntTimeLimit { get; set; }
    public int HuntScore { get; set; } = 50;
    public int AssaultScore { get; set; } = 180;

    public List<PlaylistEntry> Playlist { get; set; } = new();

    [JsonIgnore]
    public string DifficultyName => Difficulty switch { 2 => "Medium", 4 => "Overhaul", _ => "Hard" };

    /// <summary>12 letters and digits: no spaces, since the game reads the password as one word.</summary>
    public static string NewAdminPassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(12, alphabet, (span, chars) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    /// <summary>Puts every value back in the range the game accepts.</summary>
    public void Normalize()
    {
        Name = CleanText(Name, 31);
        if (Name.Length == 0) Name = "OpenSWBF2 Server";
        JoinPassword = CleanText(JoinPassword, 15).Replace(" ", "");
        if (!IsValidAdminPassword(AdminPassword)) AdminPassword = NewAdminPassword();
        Port = Math.Clamp(Port, 1024, 65534);
        MaxPlayers = Math.Clamp(MaxPlayers, 2, 64);
        if (!TickRates.Contains(TickRate)) TickRate = 60;
        BotsPerTeam = Math.Clamp(BotsPerTeam, 0, 32);
        Difficulty = Math.Clamp(Difficulty, 2, 4);
        if (HeroUnlock != HeroUnlockTimer) HeroUnlock = HeroUnlockPoints;
        HeroUnlockValue = HeroUnlock == HeroUnlockTimer ? Math.Clamp(HeroUnlockValue, 0, 120) : Math.Clamp(HeroUnlockValue, 1, 50);
        if (HeroPlayer != HeroPlayerRandom) HeroPlayer = HeroPlayerBest;
        ConquestReinforcements = Math.Clamp(ConquestReinforcements, 10, 500);
        ConquestTimeLimit = Math.Clamp(ConquestTimeLimit, 0, 60);
        CtfCaptures = Math.Clamp(CtfCaptures, 1, 15);
        CtfTimeLimit = Math.Clamp(CtfTimeLimit, 0, 60);
        HuntTimeLimit = Math.Clamp(HuntTimeLimit, 0, 60);
        HuntScore = Math.Clamp(HuntScore, 50, 150);
        AssaultScore = Math.Clamp(AssaultScore, 150, 300);
        Playlist.RemoveAll(e => !PlaylistEntry.IsValidMission(e.Mission));
        foreach (var entry in Playlist) entry.Normalize();
    }

    public static bool IsValidAdminPassword(string? password) =>
        password is { Length: >= 6 and <= 31 } && password.All(char.IsAsciiLetterOrDigit);

    /// <summary>Quotes would end the argument early and break the command line.</summary>
    private static string CleanText(string? text, int max)
    {
        var clean = new string((text ?? "").Where(c => c != '"' && !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max].TrimEnd() : clean;
    }

    /// <summary>The game options as admin commands, the same in ServerSettings.cfg and over rcon.
    /// They take effect from the next map.</summary>
    public IReadOnlyList<(string Command, int Value)> OptionCommands()
    {
        int On(bool value) => value ? 1 : 0;
        return new (string, int)[]
        {
            ("difficulty", Difficulty),
            ("conaiperteam", BotsPerTeam),
            ("ctfaiperteam", BotsPerTeam),
            ("assaiperteam", BotsPerTeam),
            ("eliaiperteam", BotsPerTeam),
            ("teamdamage", On(TeamDamage)),
            ("shownames", On(ShowNames)),
            ("autoassignteams", On(AutoAssignTeams)),
            ("awards", On(Awards)),
            ("heroes", On(Heroes)),
            ("hrunlock", HeroUnlock),
            ("hrunlockvalue", HeroUnlockValue),
            ("hrplayer", HeroPlayer),
            ("conreinforcements", ConquestReinforcements),
            ("contimelimit", ConquestTimeLimit),
            ("ctfscorelimit", CtfCaptures),
            ("ctftimelimit", CtfTimeLimit),
            ("huntimelimit", HuntTimeLimit),
            ("huntscorelimit", HuntScore),
            ("assscorelimit", AssaultScore),
        };
    }

    /// <summary>ServerSettings.cfg: one admin command per line, as the engine reads it at start.</summary>
    public IReadOnlyList<string> SettingsLines() => OptionCommands().Select(o => $"/{o.Command} {o.Value}").ToList();

    public ServerProfile Clone()
    {
        var copy = (ServerProfile)MemberwiseClone();
        copy.Playlist = Playlist.Select(e => e.Clone()).ToList();
        return copy;
    }
}

/// <summary>One map in the rotation: a mission name such as geo1c_con, and optional tickets.</summary>
public sealed class PlaylistEntry
{
    public string Mission { get; set; } = "";
    /// <summary>For display while the catalog is not loaded.</summary>
    public string Title { get; set; } = "";
    public int? Tickets1 { get; set; }
    public int? Tickets2 { get; set; }

    /// <summary>Mission names are one word of letters, digits and underscores (geo1c_con).</summary>
    public static bool IsValidMission(string? mission) =>
        mission is { Length: > 0 and <= 31 } && mission.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public void Normalize()
    {
        if (Tickets1 is < 1 or > 9999) Tickets1 = Math.Clamp(Tickets1!.Value, 1, 9999);
        if (Tickets2 is < 1 or > 9999) Tickets2 = Math.Clamp(Tickets2!.Value, 1, 9999);
    }

    /// <summary>Both numbers or neither: the game reads a lone number's partner from the next
    /// argument. A blank one takes the other's value; zero would read as "no tickets".</summary>
    public (int, int)? Tickets => (Tickets1, Tickets2) switch
    {
        (int a, int b) => (Math.Clamp(a, 1, 9999), Math.Clamp(b, 1, 9999)),
        (int a, null) => (Math.Clamp(a, 1, 9999), Math.Clamp(a, 1, 9999)),
        (null, int b) => (Math.Clamp(b, 1, 9999), Math.Clamp(b, 1, 9999)),
        _ => null,
    };

    /// <summary>"geo1c_con 150 150" or "geo1c_con".</summary>
    public string ToArgument() => Tickets is var (a, b) ? $"{Mission} {a} {b}" : Mission;

    public PlaylistEntry Clone() => (PlaylistEntry)MemberwiseClone();
}
