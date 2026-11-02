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
    /// <summary>The longest hero unlock timer and respawn time, in seconds (OpenSWBF2 0.2.9+;
    /// Instant Action stops at 120).</summary>
    public const int MaxHeroSeconds = 300;

    public string Name { get; set; } = "OpenSWBF2 Server";
    public string JoinPassword { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public int MaxPlayers { get; set; } = 64;
    public int TickRate { get; set; } = 60;
    public string AdminPassword { get; set; } = NewAdminPassword();

    /// <summary>Bots a team, per mode, as the game's mode settings take them: Conquest, CTF (2 and
    /// 1 flag), and Assault (space and hero assault, and hunt). A team holds up to
    /// <see cref="MaxTeamSoldiers"/> soldiers on OpenSWBF2 0.2.6+, so players push bots out past that.</summary>
    public int ConquestBots { get; set; } = RecommendedConquestBots;
    public int CtfBots { get; set; } = RecommendedCtfBots;
    public int AssaultBots { get; set; } = RecommendedAssaultBots;

    /// <summary>The team sizes the stock maps were made for.</summary>
    public const int RecommendedConquestBots = 32;
    public const int RecommendedCtfBots = 32;
    public const int RecommendedAssaultBots = 32;
    public const int MaxBots = 64;
    public const int MaxTeamSoldiers = 64;

    /// <summary>2 Medium, 3 Hard, 4 Overhaul (the AI overhaul's third difficulty).</summary>
    public int Difficulty { get; set; } = 3;
    public bool TeamDamage { get; set; } = true;
    public bool ShowNames { get; set; } = true;
    public bool AutoAssignTeams { get; set; } = true;
    public bool Awards { get; set; } = true;

    public bool Heroes { get; set; } = true;
    public int HeroUnlock { get; set; } = HeroUnlockPoints;
    /// <summary>Points (1-50) or seconds (0-300).</summary>
    public int HeroUnlockValue { get; set; } = 10;
    public int HeroPlayer { get; set; } = HeroPlayerBest;
    /// <summary>Seconds after a hero dies until one can be taken again (0 right away, up to
    /// <see cref="MaxHeroSeconds"/>), or -1 never; the game's default is 120.</summary>
    public int HeroRespawnValue { get; set; } = 120;
    /// <summary>A bot takes the hero when no player has within 30 seconds (OpenSWBF2 0.2.1+).</summary>
    public bool AiHeroes { get; set; } = true;

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

    /// <summary>The launcher shares this PC's mods with joining launchers and runs their lobby
    /// (ModHostService), on TCP at the game's port number.</summary>
    public bool ShareMods { get; set; } = true;

    /// <summary>All mod downloads together, in Mbit/s; 0 = no cap.</summary>
    public int ModRate { get; set; } = 50;

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
        ConquestBots = Math.Clamp(ConquestBots, 0, MaxBots);
        CtfBots = Math.Clamp(CtfBots, 0, MaxBots);
        AssaultBots = Math.Clamp(AssaultBots, 0, MaxBots);
        Difficulty = Math.Clamp(Difficulty, 2, 4);
        if (HeroUnlock != HeroUnlockTimer) HeroUnlock = HeroUnlockPoints;
        HeroUnlockValue = HeroUnlock == HeroUnlockTimer ? Math.Clamp(HeroUnlockValue, 0, MaxHeroSeconds) : Math.Clamp(HeroUnlockValue, 1, 50);
        if (HeroPlayer != HeroPlayerRandom) HeroPlayer = HeroPlayerBest;
        HeroRespawnValue = Math.Clamp(HeroRespawnValue, -1, MaxHeroSeconds);
        ConquestReinforcements = Math.Clamp(ConquestReinforcements, 10, 500);
        ConquestTimeLimit = Math.Clamp(ConquestTimeLimit, 0, 60);
        CtfCaptures = Math.Clamp(CtfCaptures, 1, 15);
        CtfTimeLimit = Math.Clamp(CtfTimeLimit, 0, 60);
        HuntTimeLimit = Math.Clamp(HuntTimeLimit, 0, 60);
        HuntScore = Math.Clamp(HuntScore, 50, 150);
        AssaultScore = Math.Clamp(AssaultScore, 150, 300);
        ModRate = Math.Clamp(ModRate, 0, 10000);
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
            ("conaiperteam", ConquestBots),
            ("ctfaiperteam", CtfBots),
            ("assaiperteam", AssaultBots),
            // the game keeps an elimination count too, but no map reads it
            ("eliaiperteam", AssaultBots),
            ("teamdamage", On(TeamDamage)),
            ("shownames", On(ShowNames)),
            ("autoassignteams", On(AutoAssignTeams)),
            ("awards", On(Awards)),
            ("heroes", On(Heroes)),
            ("hrunlock", HeroUnlock),
            ("hrunlockvalue", HeroUnlockValue),
            ("hrplayer", HeroPlayer),
            ("hrrespawnvalue", HeroRespawnValue),
            ("aiheroes", On(AiHeroes)),
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

    /// <summary>The game options by command, each once (the four bot counts are one setting here).</summary>
    public Dictionary<string, int> Options()
    {
        var options = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (command, value) in OptionCommands()) options.TryAdd(command, value);
        return options;
    }

    /// <summary>Takes one game option back from its admin command (the other way round from
    /// <see cref="OptionCommands"/>); false for one the launcher doesn't show. Call
    /// <see cref="Normalize"/> after.</summary>
    public bool SetOption(string command, int value)
    {
        bool On() => value != 0;
        switch (command.ToLowerInvariant())
        {
            case "difficulty": Difficulty = value; break;
            case "conaiperteam": ConquestBots = value; break;
            case "ctfaiperteam": CtfBots = value; break;
            case "assaiperteam": AssaultBots = value; break;
            case "teamdamage": TeamDamage = On(); break;
            case "shownames": ShowNames = On(); break;
            case "autoassignteams": AutoAssignTeams = On(); break;
            case "awards": Awards = On(); break;
            case "heroes": Heroes = On(); break;
            case "hrunlock": HeroUnlock = value; break;
            case "hrunlockvalue": HeroUnlockValue = value; break;
            case "hrplayer": HeroPlayer = value; break;
            case "hrrespawnvalue": HeroRespawnValue = value; break;
            case "aiheroes": AiHeroes = On(); break;
            case "conreinforcements": ConquestReinforcements = value; break;
            case "contimelimit": ConquestTimeLimit = value; break;
            case "ctfscorelimit": CtfCaptures = value; break;
            case "ctftimelimit": CtfTimeLimit = value; break;
            case "huntimelimit": HuntTimeLimit = value; break;
            case "huntscorelimit": HuntScore = value; break;
            case "assscorelimit": AssaultScore = value; break;
            default: return false;
        }
        return true;
    }

    /// <summary>The game options a server answered over rcon (<see cref="Rcon.ServerReader"/>);
    /// null for a server this launcher runs. An older server lacks newer options and refuses
    /// them, so only these are sent to it.</summary>
    [JsonIgnore]
    public HashSet<string>? AnsweredOptions { get; set; }

    public bool Answers(string command) => AnsweredOptions == null || AnsweredOptions.Contains(command);

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
