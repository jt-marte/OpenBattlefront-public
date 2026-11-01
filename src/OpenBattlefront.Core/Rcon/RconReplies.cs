using System.Globalization;

namespace OpenBattlefront.Core.Rcon;

public enum ServerPhase { Shell, Lobby, Warmup, Playing, Ended }

/// <summary>/state: what the server is doing. Players is team 1 / team 2 / everyone connected
/// (those still choosing a team included); TimeLeft is seconds, or -1 without a clock.</summary>
public sealed record ServerState(
    ServerPhase Phase, bool Hold, int Team1, int Team2, int Players, int TimeLeft,
    int Tickets1, int Tickets2, int Score1, int Score2, string Map, string Next);

/// <summary>/playerinfo: one player. Slot is 1-based, as /kick and /swap take it; team 0 is
/// still choosing. The name can be empty (a headless client).</summary>
public sealed record PlayerInfo(int Slot, int Team, bool Alive, int Score, int Kills, int Deaths, int Ping, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Player {Slot}" : Name;
}

/// <summary>/maplist: one rotation entry, 1-based.</summary>
public sealed record RotationEntry(int Index, string Mission, int Tickets1, int Tickets2, bool IsCurrent, bool IsNext);

/// <summary>A page of a paged reply: its rows, and where the next page starts (null at the end).</summary>
public sealed record RconPage<T>(IReadOnlyList<T> Rows, int? More, int Total);

/// <summary>Reads the server manager's replies (NetAdmin.cpp, "#&lt;command&gt;" first line).</summary>
public static class RconReplies
{
    /// <summary>The lines after "#command" up to the next command's header, or null when the
    /// reply has none (an older build that doesn't know the command).</summary>
    public static IReadOnlyList<string>? Section(IReadOnlyList<string> lines, string command)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), "#" + command, StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }
        if (start < 0) return null;
        var section = new List<string>();
        for (int i = start; i < lines.Count && !lines[i].StartsWith('#'); i++) section.Add(lines[i]);
        return section;
    }

    public static bool HasSection(IReadOnlyList<string> lines, string command) => Section(lines, command) != null;

    public static ServerState ParseState(IReadOnlyList<string> lines)
    {
        var section = Section(lines, "state") ?? throw Unsupported("state");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in section)
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (token.IndexOf('=') is var eq and > 0) values[token[..eq]] = token[(eq + 1)..];

        ServerPhase phase = values.GetValueOrDefault("state")?.ToLowerInvariant() switch
        {
            "lobby" => ServerPhase.Lobby,
            "warmup" => ServerPhase.Warmup,
            "playing" => ServerPhase.Playing,
            "ended" => ServerPhase.Ended,
            _ => ServerPhase.Shell,
        };
        int[] players = Numbers(values.GetValueOrDefault("players"), 3);
        int[] tickets = Numbers(values.GetValueOrDefault("tickets"), 2);
        int[] scores = Numbers(values.GetValueOrDefault("scores"), 2);
        return new ServerState(
            phase,
            values.GetValueOrDefault("hold") == "1",
            players[0], players[1], players[2],
            Int(values.GetValueOrDefault("time"), -1),
            tickets[0], tickets[1], scores[0], scores[1],
            values.GetValueOrDefault("map") ?? "",
            values.GetValueOrDefault("next") ?? "");
    }

    /// <summary>"p &lt;slot&gt; &lt;team&gt; &lt;alive&gt; &lt;score&gt; &lt;kills&gt; &lt;deaths&gt; &lt;ping&gt; &lt;name&gt;" rows,
    /// then "more &lt;slot&gt;" or "end &lt;n&gt;".</summary>
    public static RconPage<PlayerInfo> ParsePlayers(IReadOnlyList<string> lines)
    {
        var section = Section(lines, "playerinfo") ?? throw Unsupported("playerinfo");
        var rows = new List<PlayerInfo>();
        int? more = null;
        int total = 0;
        foreach (string line in section)
        {
            string[] parts = line.Split(' ', 9);
            if (parts[0] == "p" && parts.Length >= 8)
            {
                rows.Add(new PlayerInfo(Int(parts[1]), Int(parts[2]), parts[3] == "1", Int(parts[4]), Int(parts[5]),
                    Int(parts[6]), Int(parts[7]), parts.Length > 8 ? parts[8] : ""));
            }
            else if (parts[0] == "more" && parts.Length > 1) more = Int(parts[1]);
            else if (parts[0] == "end" && parts.Length > 1) total = Int(parts[1]);
        }
        return new RconPage<PlayerInfo>(rows, more, total);
    }

    /// <summary>"m &lt;idx&gt; &lt;mission&gt; &lt;t1&gt; &lt;t2&gt; &lt;cur|next|-&gt;" rows, then "more &lt;idx&gt;" or
    /// "end &lt;n&gt; next=&lt;i&gt;".</summary>
    public static RconPage<RotationEntry> ParseRotation(IReadOnlyList<string> lines)
    {
        var section = Section(lines, "maplist") ?? throw Unsupported("maplist");
        var rows = new List<RotationEntry>();
        int? more = null;
        int total = 0;
        foreach (string line in section)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] == "m" && parts.Length >= 5)
            {
                string flag = parts.Length > 5 ? parts[5] : "-";
                rows.Add(new RotationEntry(Int(parts[1]), parts[2], Int(parts[3]), Int(parts[4]), flag == "cur", flag == "next"));
            }
            else if (parts[0] == "more" && parts.Length > 1) more = Int(parts[1]);
            else if (parts[0] == "end" && parts.Length > 1) total = Int(parts[1]);
        }
        return new RconPage<RotationEntry>(rows, more, total);
    }

    /// <summary>A setting's value, as the game answers "/difficulty" with no value: one line, a
    /// number. Null for anything else ("command not found", say).</summary>
    public static int? ParseNumber(IReadOnlyList<string> lines) =>
        lines.Count == 1 && int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static readonly string[] PushPrefixes =
    {
        "game started: ", "game over", "score: ", "removed player ", "added player ", "lobby: ", "match started: ",
        "detected and set bandwidth",
    };

    /// <summary>Messages the server pushes on its own: chat ("name: text"), game started/over,
    /// players joining and leaving. A reply never comes as one line of this shape.</summary>
    public static bool LooksLikePush(IReadOnlyList<string> lines)
    {
        if (lines.Count != 1) return false;
        string line = lines[0];
        if (PushPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal))) return true;
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon <= 40 && !line.Contains(" : ");
    }

    private static int[] Numbers(string? text, int count)
    {
        var result = new int[count];
        if (text == null) return result;
        string[] parts = text.Split('/');
        for (int i = 0; i < count && i < parts.Length; i++) result[i] = Int(parts[i]);
        return result;
    }

    private static int Int(string? text, int fallback = 0) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static NotSupportedException Unsupported(string command) =>
        new($"The server doesn't know /{command}. Update OpenSWBF2 on the server.");
}
