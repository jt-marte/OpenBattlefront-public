using System.IO.Compression;

namespace OpenBattlefront.Core;

public sealed record ModInfo(string Name, string Path, bool Enabled);

/// <summary>
/// Mods live in Game\AddOn\&lt;folder&gt; (each with an addme.script). Turning one off moves it to
/// AddOn_disabled\, so the game never sees it.
/// </summary>
public sealed class ModManager
{
    public const string AddMe = "addme.script";
    private readonly InstallLayout _layout;

    public ModManager(InstallLayout layout) => _layout = layout;

    public IReadOnlyList<ModInfo> List()
    {
        var mods = new List<ModInfo>();
        foreach (var (dir, enabled) in new[] { (_layout.AddOn, true), (_layout.AddOnDisabled, false) })
            if (Directory.Exists(dir))
                mods.AddRange(Directory.GetDirectories(dir).Select(d => new ModInfo(Path.GetFileName(d), d, enabled)));
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

    /// <summary>Installs every mod found in a folder or .zip. Returns the folder names added.</summary>
    public IReadOnlyList<string> Install(string path)
    {
        string? extracted = null;
        try
        {
            string searchIn = path;
            if (File.Exists(path))
            {
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only folders and .zip files can be added. Extract other archives first.");
                extracted = Path.Combine(_layout.Temp, "mod-" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(path, extracted);
                searchIn = extracted;
            }

            var roots = FindModRoots(searchIn);
            if (roots.Count == 0)
                throw new InvalidDataException("No mod found there. A mod folder has an addme.script file in it.");

            var added = new List<string>();
            foreach (string root in roots)
            {
                string name = root == extracted ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(root);
                if (Directory.Exists(Path.Combine(_layout.AddOn, name)) || Directory.Exists(Path.Combine(_layout.AddOnDisabled, name)))
                    throw new IOException($"A mod named {name} is already installed. Remove it first to replace it.");
                CopyDirectory(root, Path.Combine(_layout.AddOn, name));
                added.Add(name);
            }
            return added;
        }
        finally
        {
            if (extracted != null && Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        }
    }

    public ModInfo SetEnabled(ModInfo mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod;
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
