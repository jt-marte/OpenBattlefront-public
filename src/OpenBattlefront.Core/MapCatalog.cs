using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBattlefront.Core;

public sealed record MapEra(string Id, string Name);

/// <summary>A game mode on one map, with the mission name for each era it has (era id → mission).</summary>
public sealed record MapMode(string Id, string Name, IReadOnlyDictionary<string, string> Missions);

/// <summary>One mission a playlist can hold: geo1c_con is Geonosis, Clone Wars, Conquest.</summary>
public sealed record MissionChoice(string Mission, string MapCode, string MapName, string EraName, string ModeName, bool IsMod)
{
    public string Label => $"{EraName} · {ModeName}";
    public string Title => $"{MapName} · {EraName} · {ModeName}";
}

public sealed record MapInfo(string Code, string Name, bool IsMod, IReadOnlyList<MapEra> Eras, IReadOnlyList<MapMode> Modes)
{
    /// <summary>Every era × mode the map has, eras first in the dump's order.</summary>
    public IReadOnlyList<MissionChoice> Missions { get; } = ListMissions(Code, Name, IsMod, Eras, Modes);

    private static List<MissionChoice> ListMissions(string code, string name, bool isMod, IReadOnlyList<MapEra> eras, IReadOnlyList<MapMode> modes)
    {
        var list = new List<MissionChoice>();
        var eraOrder = eras.Select(e => e.Id).ToList();
        foreach (var mode in modes)
        {
            foreach (var (era, mission) in mode.Missions.OrderBy(m => eraOrder.IndexOf(m.Key) is var i and >= 0 ? i : int.MaxValue))
            {
                string eraName = eras.FirstOrDefault(e => e.Id == era)?.Name ?? era;
                list.Add(new MissionChoice(mission, code, name, eraName, mode.Name, isMod));
            }
        }
        return list;
    }
}

/// <summary>
/// The maps, eras and modes the installed game offers, mods included, from the game's own
/// mission list (BattlefrontII.exe -listmissions, format 1). Mission names come straight from
/// the dump, so the launcher never builds them itself.
/// </summary>
public sealed class MapCatalog
{
    public const int SupportedFormat = 1;

    public MapCatalog(string language, IReadOnlyList<MapInfo> maps)
    {
        Language = language;
        Maps = maps;
        _missions = new Dictionary<string, MissionChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in maps.SelectMany(m => m.Missions)) _missions.TryAdd(choice.Mission, choice);
    }

    private readonly Dictionary<string, MissionChoice> _missions;

    public string Language { get; }
    public IReadOnlyList<MapInfo> Maps { get; }

    public MissionChoice? Find(string mission) => _missions.GetValueOrDefault(mission);
    public bool Contains(string mission) => _missions.ContainsKey(mission);

    public static MapCatalog Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new InvalidDataException("The game's mission list is not valid JSON.", e); }
        using (doc)
        {
            var root = doc.RootElement;
            int format = root.TryGetProperty("format", out var f) && f.TryGetInt32(out int n) ? n : 0;
            if (format != SupportedFormat) throw new InvalidDataException($"Unsupported mission list format {format}; update the launcher.");

            var eraNames = new Dictionary<string, string>();
            foreach (var era in Array(root, "eras"))
                if (Text(era, "id") is { Length: > 0 } id) eraNames[id] = Text(era, "name") ?? id;
            var modeNames = new Dictionary<string, string>();
            foreach (var mode in Array(root, "modes"))
                if (Text(mode, "id") is { Length: > 0 } id) modeNames[id] = Text(mode, "name") ?? id;

            var maps = new List<MapInfo>();
            foreach (var map in Array(root, "maps"))
            {
                string? code = Text(map, "code");
                if (string.IsNullOrEmpty(code)) continue;
                var eras = new List<MapEra>();
                foreach (var era in Array(map, "eras"))
                {
                    string? id = era.ValueKind == JsonValueKind.String ? era.GetString() : Text(era, "id");
                    if (string.IsNullOrEmpty(id) || eras.Any(e => e.Id == id)) continue;
                    eras.Add(new MapEra(id, (era.ValueKind == JsonValueKind.Object ? Text(era, "name") : null) ?? eraNames.GetValueOrDefault(id) ?? id));
                }
                var modes = new List<MapMode>();
                foreach (var mode in Array(map, "modes"))
                {
                    string? id = Text(mode, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var missions = new Dictionary<string, string>();
                    if (mode.TryGetProperty("missions", out var m) && m.ValueKind == JsonValueKind.Object)
                        foreach (var p in m.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String && PlaylistEntry.IsValidMission(p.Value.GetString()))
                                missions[p.Name] = p.Value.GetString()!;
                    if (missions.Count == 0) continue;
                    foreach (string era in missions.Keys.Where(k => eras.All(e => e.Id != k)))
                        eras.Add(new MapEra(era, eraNames.GetValueOrDefault(era) ?? era));
                    modes.Add(new MapMode(id, ModeName(id, Text(mode, "name") ?? modeNames.GetValueOrDefault(id)), missions));
                }
                if (modes.Count == 0) continue;
                bool isMod = map.TryGetProperty("mod", out var mod) && mod.ValueKind == JsonValueKind.True;
                maps.Add(new MapInfo(code, Text(map, "name") is { Length: > 0 } name ? name : code, isMod, eras, modes));
            }
            return new MapCatalog(Text(root, "language") ?? "english", maps);
        }
    }

    /// <summary>The game calls both eli and ass "Assault"; eli is the heroes-only one.</summary>
    private static string ModeName(string id, string? name) =>
        id.Equals("eli", StringComparison.OrdinalIgnoreCase) ? "Hero Assault" : name ?? id;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Builds the catalog by running the game headless, and caches it in Root\Cache keyed by
/// the build and the AddOn folder, so it is rebuilt only when either changes.</summary>
public sealed class MapCatalogStore
{
    /// <summary>Runs a process to the end and returns its exit code.</summary>
    public delegate Task<int> ProcessRunner(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct);

    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(2);

    private readonly InstallLayout _layout;
    private readonly string? _buildVersion;
    private readonly ProcessRunner _run;

    public MapCatalogStore(InstallLayout layout, string? buildVersion, ProcessRunner? run = null)
    {
        _layout = layout;
        _buildVersion = buildVersion;
        _run = run ?? RunAsync;
    }

    public string CacheFile => Path.Combine(_layout.Cache, "missions.json");
    public string KeyFile => Path.Combine(_layout.Cache, "missions.key");

    /// <summary>Game builds from before -listmissions start the game instead, so look for the
    /// switch's name in the exe rather than run it.</summary>
    public bool GameSupportsListing => SupportsListMissions(_layout.GameExe);

    private static readonly Dictionary<(string, long, DateTime), bool> Scanned = new();

    public static bool SupportsListMissions(string exe)
    {
        var info = new FileInfo(exe);
        if (!info.Exists) return false;
        var key = (info.FullName.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc);
        lock (Scanned)
        {
            if (Scanned.TryGetValue(key, out bool known)) return known;
        }
        bool found = File.ReadAllBytes(exe).AsSpan().IndexOf("listmissions"u8) >= 0;
        lock (Scanned) Scanned[key] = found;
        return found;
    }

    /// <summary>The build version, the exe, and every AddOn folder with its addme.script time.</summary>
    public string ComputeKey()
    {
        var text = new StringBuilder();
        text.Append("v=").Append(_buildVersion).Append('\n');
        var exe = new FileInfo(_layout.GameExe);
        if (exe.Exists) text.Append("exe=").Append(exe.Length).Append(':').Append(exe.LastWriteTimeUtc.Ticks).Append('\n');
        if (Directory.Exists(_layout.AddOn))
        {
            foreach (string dir in Directory.GetDirectories(_layout.AddOn).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                text.Append(Path.GetFileName(dir).ToLowerInvariant());
                string addme = Path.Combine(dir, "addme.script");
                if (File.Exists(addme)) text.Append(':').Append(File.GetLastWriteTimeUtc(addme).Ticks);
                text.Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>The cached catalog when it still matches the game and its mods, otherwise null.</summary>
    public MapCatalog? TryLoadCached()
    {
        try
        {
            if (!File.Exists(CacheFile) || !File.Exists(KeyFile)) return null;
            if (File.ReadAllText(KeyFile).Trim() != ComputeKey()) return null;
            return MapCatalog.Parse(File.ReadAllText(CacheFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public async Task<MapCatalog> GetAsync(bool refresh, IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        if (!refresh && TryLoadCached() is { } cached) return cached;
        return await BuildAsync(progress, ct);
    }

    public async Task<MapCatalog> BuildAsync(IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        if (!File.Exists(_layout.GameExe)) throw new FileNotFoundException("The game is not installed yet.", _layout.GameExe);
        if (!GameSupportsListing) throw new NotSupportedException(UpdateNeeded);
        string key = ComputeKey();
        Directory.CreateDirectory(_layout.Temp);
        string output = Path.Combine(_layout.Temp, "missions.json");
        File.Delete(output);

        progress?.Report(new StepUpdate("Reading the map list", "The game lists its maps and mods; this takes a few seconds"));
        Directory.CreateDirectory(_layout.Logs);
        string log = Path.Combine(_layout.Logs, "missions.log");
        var info = new ProcessStartInfo(_layout.GameExe, $"-listmissions \"{output}\" -lang 0 -log \"{log}\"")
        {
            WorkingDirectory = _layout.Game,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        int code = await _run(info, BuildTimeout, ct);
        if (code != 0 || !File.Exists(output))
            throw new InvalidOperationException($"The game couldn't list its maps (exit code {code}). Check Logs\\missions.log.");

        string json = await File.ReadAllTextAsync(output, ct);
        var catalog = MapCatalog.Parse(json);
        Directory.CreateDirectory(_layout.Cache);
        await File.WriteAllTextAsync(CacheFile, json, ct);
        await File.WriteAllTextAsync(KeyFile, key, ct);
        File.Delete(output);
        return catalog;
    }

    public const string UpdateNeeded = "Update OpenSWBF2 to host from here: this build can't list its maps for the launcher.";

    private static async Task<int> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The game did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("The game took too long to list its maps.");
        }
    }
}
