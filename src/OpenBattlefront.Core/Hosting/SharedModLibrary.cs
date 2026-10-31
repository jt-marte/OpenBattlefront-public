namespace OpenBattlefront.Core.Hosting;

/// <param name="Path">Relative to the mod folder, with forward slashes.</param>
public sealed record SharedFile(string Path, long Size, string Sha256, string FullPath);

public sealed record SharedFolder(string Name, IReadOnlyList<SharedFile> Files)
{
    public long Size => Files.Sum(f => f.Size);
}

/// <summary>The mod folders a hosted server loads (Game\AddOn) and their files' SHA-256, filtered as
/// the Docker server's modserver.py does: only folders with an addme.script and a usable name,
/// never the unofficial 1.3 patch, no programs, no names Windows can't write, nothing that links
/// outside its folder. Hashes come from the launcher's own cache, so each file is read once.</summary>
public sealed class SharedModLibrary
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);
    private readonly string _addOn;
    private readonly FileHashCache _hashes;
    private readonly object _lock = new();
    private IReadOnlyList<SharedFolder> _folders = Array.Empty<SharedFolder>();
    private DateTime? _scanned;

    public SharedModLibrary(string addOn, FileHashCache hashes)
    {
        _addOn = addOn;
        _hashes = hashes;
    }

    /// <summary>The last scan's folders, without looking again (for the UI: a scan hashes new files).</summary>
    public IReadOnlyList<SharedFolder> Folders
    {
        get { lock (_lock) return _folders; }
    }

    public IReadOnlyList<SharedFolder> Current()
    {
        lock (_lock)
        {
            if (_scanned is { } at && DateTime.UtcNow - at < MaxAge) return _folders;
        }
        return Scan();
    }

    public SharedFile? Lookup(string folder, string path) =>
        Current().FirstOrDefault(f => f.Name == folder)?.Files.FirstOrDefault(f => f.Path == path);

    public IReadOnlyList<SharedFolder> Scan()
    {
        lock (_lock)
        {
            var folders = new List<SharedFolder>();
            string[] names;
            try { names = Directory.GetDirectories(_addOn).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { names = Array.Empty<string>(); }
            foreach (string name in names)
            {
                if (name.StartsWith('.') || ScriptMods.IsPatchAddOn(name) || !ServerModsManifest.IsSafeFolderName(name)) continue;
                var folder = ScanFolder(name, System.IO.Path.Combine(_addOn, name));
                if (folder != null) folders.Add(folder);
            }
            _hashes.Save();
            _folders = folders;
            _scanned = DateTime.UtcNow;
            return folders;
        }
    }

    private SharedFolder? ScanFolder(string name, string dir)
    {
        string root = Resolve(new DirectoryInfo(dir)) ?? System.IO.Path.GetFullPath(dir);
        string prefix = root.TrimEnd('\\') + "\\";
        try
        {
            if (!Directory.EnumerateFiles(root).Any(f => System.IO.Path.GetFileName(f).Equals(ModManager.AddMe, StringComparison.OrdinalIgnoreCase)))
                return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        var files = new List<SharedFile>();
        void Walk(string current)
        {
            foreach (string full in Directory.GetFiles(current).Order(StringComparer.Ordinal))
            {
                string rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
                if (GameContent.IsProgram(rel) || !ServerModsManifest.IsSafeModPath(rel)) continue;
                var info = new FileInfo(full);
                string real = Resolve(info) ?? info.FullName;
                if (!real.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(real)) continue;
                try { files.Add(new SharedFile(rel, new FileInfo(real).Length, _hashes.Sha256(real), real)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            // A linked folder inside a mod isn't followed (os.walk doesn't either)
            foreach (string sub in Directory.GetDirectories(current).Order(StringComparer.Ordinal))
                if (!new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) Walk(sub);
        }
        try { Walk(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return new SharedFolder(name, files);
    }

    /// <summary>Where a link (symlink or junction) finally points, or null when it isn't one.</summary>
    private static string? Resolve(FileSystemInfo info)
    {
        try
        {
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
