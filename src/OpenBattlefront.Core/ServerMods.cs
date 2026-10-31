using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

/// <summary>One mod folder a server shares, as mods.json lists it. File paths are relative to the
/// folder, with forward slashes.</summary>
public sealed class ServerModFolder
{
    public string Name { get; set; } = "";
    public long Size { get; set; }

    /// <summary>"server" (the server's own addons folder) or "uploaded" (an admin uploaded it).</summary>
    public string Source { get; set; } = "server";
    public List<ManifestFile> Files { get; set; } = new();

    public bool IsUploaded => Source == "uploaded";
}

/// <summary>An upload or removal that waits for the game to restart (admins only).</summary>
public sealed class PendingModChange
{
    public string Name { get; set; } = "";

    /// <summary>"add" or "remove".</summary>
    public string Action { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>GET /mods.json from a server's mod service (the Docker server's modserver.py): every mod
/// folder the game there loads, with each file's size and SHA-256.</summary>
public sealed partial class ServerModsManifest
{
    public const int MaxFolders = 64;
    public const int MaxFiles = 50_000;
    public const long MaxFileSize = 4L << 30;
    public const long MaxTotal = 32L << 30;

    public int Schema { get; set; }
    public string Server { get; set; } = "";

    /// <summary>The OpenSWBF2 version the server runs.</summary>
    public string Openswbf2 { get; set; } = "";
    public bool Uploads { get; set; }

    /// <summary>"none" or "when-empty".</summary>
    public string Restart { get; set; } = "none";
    public List<ServerModFolder> Folders { get; set; } = new();
    public List<PendingModChange>? Pending { get; set; }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static ServerModsManifest Parse(string json)
    {
        ServerModsManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ServerModsManifest>(json, Options); }
        catch (JsonException e) { throw new InvalidDataException("The server's mod list is not valid JSON.", e); }
        if (manifest == null) throw new InvalidDataException("The server's mod list is empty.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>Every name safe to write on this PC, no programs, no 1.3 patch, nothing twice; one
    /// bad entry and none of it is used.</summary>
    public void Validate()
    {
        if (Schema != 1) throw new InvalidDataException($"The server's mod list has a format ({Schema}) this launcher doesn't know; update the launcher.");
        if (Folders.Count > MaxFolders) throw new InvalidDataException($"The server lists more than {MaxFolders} mods.");
        if (Folders.Sum(f => f.Files.Count) > MaxFiles) throw new InvalidDataException($"The server lists more than {MaxFiles} mod files.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var folder in Folders)
        {
            if (!IsSafeFolderName(folder.Name)) throw new InvalidDataException($"The server lists a mod folder with an unsafe name: {folder.Name}");
            if (ScriptMods.IsPatchAddOn(folder.Name)) throw new InvalidDataException("The server offers the unofficial 1.3 patch. OpenSWBF2 has its support built in, so it is never downloaded.");
            if (!names.Add(folder.Name)) throw new InvalidDataException($"The server lists {folder.Name} twice.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in folder.Files)
            {
                if (!IsSafeModPath(file.Path)) throw new InvalidDataException($"Unsafe path in the server's mod list: {folder.Name}/{file.Path}");
                if (GameContent.IsProgram(file.Path)) throw new InvalidDataException($"The server's mod list holds a program: {folder.Name}/{file.Path}");
                if (!paths.Add(file.Path)) throw new InvalidDataException($"The server lists {folder.Name}/{file.Path} twice.");
                if (!Sha256Pattern().IsMatch(file.Sha256)) throw new InvalidDataException($"Bad SHA-256 for {folder.Name}/{file.Path}");
                if (file.Size < 0 || file.Size > MaxFileSize) throw new InvalidDataException($"Bad size for {folder.Name}/{file.Path}");
                total += file.Size;
            }
            if (!folder.Files.Any(f => f.Path.Equals(ModManager.AddMe, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The server's {folder.Name} has no {ModManager.AddMe}.");
            folder.Size = folder.Files.Sum(f => f.Size);
        }
        if (total > MaxTotal) throw new InvalidDataException("The server's mods add up to more than 32 GB.");
    }

    /// <summary>A mod folder name the server and the game both take: letters, digits, _ . -</summary>
    public static bool IsSafeFolderName(string? name) =>
        name != null && FolderName().IsMatch(name) && !Device().IsMatch(name);

    /// <summary>A file path inside a mod folder (forward slashes) that Windows writes as it is: no
    /// trailing dots or spaces, no device names.</summary>
    public static bool IsSafeModPath(string? path) =>
        BuildManifest.IsSafeRelative(path) && path!.Length <= 400 &&
        path.Split('/').All(part => part.Length <= 255 && !part.EndsWith('.') && !part.EndsWith(' ') && !Device().IsMatch(part));

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex FolderName();

    [GeneratedRegex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Device();
}

public enum ModSyncAction
{
    /// <summary>The player has the same files.</summary>
    UpToDate,

    /// <summary>The player doesn't have it.</summary>
    New,

    /// <summary>The player has another version: the differing files are fetched.</summary>
    Update,

    /// <summary>The player has it, switched off: it is switched on.</summary>
    Enable,
}

/// <param name="Missing">The files to download.</param>
public sealed record ModSyncItem(ServerModFolder Folder, ModSyncAction Action, IReadOnlyList<ManifestFile> Missing)
{
    public string Name => Folder.Name;
    public long Bytes => Missing.Sum(f => f.Size);
}

/// <summary>What joining a server needs: for each of its mods, nothing, a download, an update or
/// switching it on. The player's other mods, and extra files in theirs, are left alone.</summary>
public sealed class ModSyncPlan
{
    public ModSyncPlan(IReadOnlyList<ModSyncItem> items) => Items = items;

    public IReadOnlyList<ModSyncItem> Items { get; }
    public IReadOnlyList<ModSyncItem> Needed => Items.Where(i => i.Action != ModSyncAction.UpToDate).ToList();
    public bool IsEmpty => Needed.Count == 0;
    public long Bytes => Items.Sum(i => i.Bytes);

    public static ModSyncPlan Create(ServerModsManifest manifest, InstallLayout layout, FileHashCache hashes)
    {
        var items = new List<ModSyncItem>();
        foreach (var folder in manifest.Folders)
        {
            string on = Path.Combine(layout.AddOn, folder.Name);
            string off = Path.Combine(layout.AddOnDisabled, folder.Name);
            if (Directory.Exists(on))
            {
                var missing = Differing(folder, on, hashes);
                items.Add(new ModSyncItem(folder, missing.Count == 0 ? ModSyncAction.UpToDate : ModSyncAction.Update, missing));
            }
            else if (Directory.Exists(off))
            {
                var missing = Differing(folder, off, hashes);
                items.Add(new ModSyncItem(folder, missing.Count == 0 ? ModSyncAction.Enable : ModSyncAction.Update, missing));
            }
            else items.Add(new ModSyncItem(folder, ModSyncAction.New, folder.Files));
        }
        hashes.Save();
        return new ModSyncPlan(items);
    }

    /// <summary>The server's files that <paramref name="dir"/> lacks or holds another version of.</summary>
    public static IReadOnlyList<ManifestFile> Differing(ServerModFolder folder, string dir, FileHashCache hashes)
    {
        var missing = new List<ManifestFile>();
        foreach (var file in folder.Files)
        {
            string local = Path.Combine(dir, file.Path.Replace('/', '\\'));
            var info = new FileInfo(local);
            if (!info.Exists || info.Length != file.Size || !string.Equals(hashes.Sha256(local), file.Sha256, StringComparison.OrdinalIgnoreCase))
                missing.Add(file);
        }
        return missing;
    }
}

/// <summary>SHA-256s of local files, kept by path, size and modification time, so a second look at a
/// mod (every join, the admin screen) reads nothing again.</summary>
public sealed class FileHashCache
{
    private sealed record Entry(long Size, long Ticks, string Sha256);

    private readonly string? _file;
    private readonly Dictionary<string, Entry> _entries;
    private bool _dirty;

    /// <param name="file">Where it is kept (Cache\addon-hashes.json), or null for this run only.</param>
    public FileHashCache(string? file = null)
    {
        _file = file;
        _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (file == null || !File.Exists(file)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file));
            if (saved != null)
                foreach (var (path, entry) in saved)
                    _entries[path] = entry;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Hashing again is only slower.
        }
    }

    public static FileHashCache For(InstallLayout layout) => new(Path.Combine(layout.Cache, "addon-hashes.json"));

    public int Hashed { get; private set; }

    public string Sha256(string path)
    {
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        long ticks = info.LastWriteTimeUtc.Ticks;
        lock (_entries)
        {
            if (_entries.TryGetValue(full, out var known) && known.Size == info.Length && known.Ticks == ticks) return known.Sha256;
        }
        string sha = Hashing.Sha256(full);
        lock (_entries)
        {
            _entries[full] = new Entry(info.Length, ticks, sha);
            _dirty = true;
            Hashed++;
        }
        return sha;
    }

    public void Save()
    {
        if (_file == null) return;
        lock (_entries)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                string temp = _file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Where(e => File.Exists(e.Key)).ToDictionary(e => e.Key, e => e.Value)));
                File.Move(temp, _file, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Next time hashes again.
            }
        }
    }
}
