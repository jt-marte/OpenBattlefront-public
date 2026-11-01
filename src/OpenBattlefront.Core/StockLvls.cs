using System.Security.Cryptography;

namespace OpenBattlefront.Core;

/// <summary>
/// The four core lvls that OpenSWBF2 rebuilds with its own scripts. Only these are checked:
/// every other game file is copied as it is, mods included.
/// </summary>
public static class StockLvls
{
    public static readonly string[] Names = { "shell", "common", "ingame", "mission" };

    /// <summary>The hashes pinned in OpenSWBF2's tools\lvl_build.cpp: Steam depot 6061 (v1.1), then
    /// GOG v1.1 (installer build 10935), whose mission.lvl is Steam's. Each release manifest says
    /// which ones its build takes.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Known = new Dictionary<string, string[]>
    {
        ["shell"] = new[]
        {
            "82335ab55e17689cd620ba174568e6a5481d4523b821549aa153a6b88a831009",
            "7cde6a5fcb121653d4f647ce7a2b158305e88bad01bf55d14cfabcd4d0b3d00b",
        },
        ["common"] = new[]
        {
            "60dfca80aaa21a01744eeddd4e2f71a5e732b492b5def9decca78e4b4afe9a58",
            "7a6c9a52a76e5ba7e30ac7a9b5d3e2a1fbbd0f4567c4684361b138c0ce68b2f1",
        },
        ["ingame"] = new[]
        {
            "cc78b5dfd6f4a082cb5a5da8cdfd67de5d45a092e25f57c47ad55c41499182dc",
            "338b9d1db70383b2eeed26b600b987f8e0bb6d9a4151c79eef6848edabecdbdd",
        },
        ["mission"] = new[] { "f1000a76ec8d642b5e531833a6d53b9b5f37dbc0a3bbb4bfe605710fc66da10b" },
    };

    public const string GogInstaller =
        "That's GOG's installer. Install the game with it first, then drop the installed folder " +
        @"(C:\GOG Games\Star Wars - Battlefront 2 unless you picked another).";

    /// <summary>The GOG offline installer (setup_star_wars_-_battlefront_2_*.exe and its .bin parts).</summary>
    public static bool IsGogInstaller(string path) =>
        Path.GetFileName(path).StartsWith("setup_star_wars_-_battlefront_2", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string[]> Merge(IReadOnlyDictionary<string, string[]>? extra)
    {
        var merged = Known.ToDictionary(k => k.Key, k => k.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (extra != null)
            foreach (var (name, hashes) in extra)
                if (merged.TryGetValue(name, out var set))
                    set.UnionWith(hashes);
        return merged.ToDictionary(k => k.Key, k => k.Value.ToArray());
    }

    /// <summary>The stock copy of a core lvl in a GameData folder. OpenSWBF2's own setup keeps it
    /// as .orig beside the rebuilt one, so that is read first.</summary>
    public static string? FindIn(string gameData, string name)
    {
        string path = Path.Combine(gameData, "data", "_lvl_pc", name + ".lvl");
        if (File.Exists(path + ".orig")) return path + ".orig";
        return File.Exists(path) ? path : null;
    }
}

public static class Hashing
{
    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record LvlCheck(string Name, string? Path, string? Sha256, bool Ok);

public sealed record ValidationResult(string GameData, IReadOnlyList<LvlCheck> Lvls)
{
    public bool Ok => Lvls.All(l => l.Ok);
    public IEnumerable<string> Missing => Lvls.Where(l => l.Path == null).Select(l => l.Name + ".lvl");
    public IEnumerable<string> Modified => Lvls.Where(l => l.Path != null && !l.Ok).Select(l => l.Name + ".lvl");
}

public static class GameValidator
{
    public static ValidationResult Validate(string gameData, IReadOnlyDictionary<string, string[]> accepted)
    {
        var checks = new List<LvlCheck>();
        foreach (string name in StockLvls.Names)
        {
            string? path = StockLvls.FindIn(gameData, name);
            if (path == null)
            {
                checks.Add(new LvlCheck(name, null, null, false));
                continue;
            }
            string hash = Hashing.Sha256(path);
            bool ok = accepted.TryGetValue(name, out var hashes) && hashes.Contains(hash, StringComparer.OrdinalIgnoreCase);
            checks.Add(new LvlCheck(name, path, hash, ok));
        }
        return new ValidationResult(gameData, checks);
    }
}
