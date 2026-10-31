namespace OpenBattlefront.Core;

/// <param name="IsIncluded">One of the folders that came with the launcher (GameContent).</param>
public sealed record ModInfo(string Name, string Path, bool Enabled, bool IsIncluded = false)
{
    /// <summary>The unofficial 1.3 patch's addon, which stays off: OpenSWBF2 has its support built in.</summary>
    public bool IsPatch => ScriptMods.IsPatchAddOn(Name);
}

/// <summary>
/// Mods live in Game\AddOn\&lt;folder&gt; (each with an addme.script). Turning one off moves it to
/// AddOn_disabled\, so the game never sees it.
/// </summary>
public sealed class ModManager
{
    public const string AddMe = "addme.script";
    private readonly InstallLayout _layout;

    public ModManager(InstallLayout layout) => _layout = layout;

    /// <param name="included">The folders the launcher put there itself (GameContent.Installed).</param>
    public IReadOnlyList<ModInfo> List(IReadOnlyCollection<string>? included = null)
    {
        var mods = new List<ModInfo>();
        bool IsIncluded(string name) => included?.Contains(name, StringComparer.OrdinalIgnoreCase) == true;
        foreach (var (dir, enabled) in new[] { (_layout.AddOn, true), (_layout.AddOnDisabled, false) })
            if (Directory.Exists(dir))
                mods.AddRange(Directory.GetDirectories(dir).Select(d => new ModInfo(Path.GetFileName(d), d, enabled, IsIncluded(Path.GetFileName(d)))));
        return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Folders holding an addme.script: the folder itself, or ones below it.</summary>
    public static IReadOnlyList<string> FindModRoots(string dir, int maxDepth = 3)
    {
        var roots = new List<string>();
        void Walk(string current, int depth)
        {
            if (File.Exists(Path.Combine(current, AddMe)))
            {
                roots.Add(current);
                return;
            }
            if (depth >= maxDepth) return;
            foreach (string child in Directory.GetDirectories(current))
                Walk(child, depth + 1);
        }
        if (Directory.Exists(dir)) Walk(dir, 0);
        return roots;
    }

    /// <summary>Installs every mod found in a folder or archive: addon folders into AddOn\, and
    /// script mods (user_script_N.lvl, custom_gc_N.lvl) into data\_lvl_pc. A script mod file can
    /// also be added on its own. Returns the names added.</summary>
    public IReadOnlyList<string> Install(string path)
    {
        string? extracted = null;
        try
        {
            string searchIn = path;
            if (File.Exists(path))
            {
                if (ScriptMods.IsScriptMod(Path.GetFileName(path)))
                    return new ScriptMods(_layout).Install(new[] { path });
                if (!Archives.IsArchive(path))
                    throw new InvalidDataException("Only folders, .zip, .7z or .rar files and script mods (user_script_N.lvl, custom_gc_N.lvl) can be added. Run installers yourself, or extract them first.");
                extracted = Path.Combine(_layout.Temp, "mod-" + Guid.NewGuid().ToString("N"));
                Archives.Extract(path, extracted);
                searchIn = extracted;
            }

            var found = FindModRoots(searchIn);
            var roots = found.Where(r => !ScriptMods.IsPatchAddOn(Path.GetFileName(r))).ToList();
            var scripts = ScriptMods.FindIn(searchIn, found);
            if (roots.Count == 0 && scripts.Count == 0)
                throw new InvalidDataException(found.Count > 0
                    ? "That's the unofficial 1.3 patch. OpenSWBF2 has 1.3-style mod support built in, so it isn't needed."
                    : "No mod found there. A mod folder has an addme.script file in it.");

            var names = roots.Select(r => r == extracted ? ArchiveName(path) : Path.GetFileName(r)).ToList();
            foreach (string name in names)
                if (Directory.Exists(Path.Combine(_layout.AddOn, name)) || Directory.Exists(Path.Combine(_layout.AddOnDisabled, name)))
                    throw new IOException($"A mod named {name} is already installed. Remove it first to replace it.");

            var added = new List<string>(new ScriptMods(_layout).Install(scripts));
            for (int i = 0; i < roots.Count; i++)
            {
                CopyDirectory(roots[i], Path.Combine(_layout.AddOn, names[i]));
                added.Add(names[i]);
            }
            return added;
        }
        finally
        {
            if (extracted != null && Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        }
    }

    private static string ArchiveName(string path)
    {
        string name = Path.GetFileName(path);
        foreach (string ext in new[] { ".tar.gz", ".tar.xz", ".tar.bz2" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public ModInfo SetEnabled(ModInfo mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod;
        if (enabled && mod.IsPatch)
            throw new IOException("The unofficial 1.3 patch stays off: OpenSWBF2 has 1.3-style mod support built in.");
        string to = Path.Combine(enabled ? _layout.AddOn : _layout.AddOnDisabled, mod.Name);
        if (Directory.Exists(to)) throw new IOException($"{to} already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(mod.Path, to);
        return mod with { Path = to, Enabled = enabled };
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
