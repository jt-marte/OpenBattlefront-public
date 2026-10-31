using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

public enum ScriptModKind
{
    /// <summary>user_script_N.lvl: runs at the start of every match.</summary>
    Match,
    /// <summary>custom_gc_N.lvl: runs in the menus (Galactic Conquest campaigns and such).</summary>
    Menus,
}

public sealed record ScriptModInfo(string Name, string Path, bool Enabled, ScriptModKind Kind);

/// <summary>
/// Script mods are the numbered lvls in data\_lvl_pc that OpenSWBF2's built-in 1.3-style mod
/// support loads (slots 0 to 99): user_script_N.lvl in every match, custom_gc_N.lvl in the menus.
/// Turning one off moves it to data\_lvl_pc\_disabled\. The unofficial 1.3 patch's own files are
/// never listed or imported: OpenSWBF2 doesn't need the patch.
/// </summary>
public sealed class ScriptMods
{
    public const string PatchAddOn = "AAA-v1.3patch";

    /// <summary>The first OpenSWBF2 release with 1.3-style mod support built in.</summary>
    public static readonly Version BuiltInSince = new(0, 2, 0);

    private static readonly string[] PatchFiles = { "user_script_10.lvl", "custom_gc_10.lvl", "v1.3patch_strings.lvl" };
    private static readonly Regex Slot = new(@"^(user_script|custom_gc)_\d{1,2}\.lvl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly InstallLayout _layout;

    public ScriptMods(InstallLayout layout) => _layout = layout;

    public string DisabledDir => Path.Combine(_layout.LvlDir, "_disabled");

    /// <summary>One of the 1.3 patch's own lvls in data\_lvl_pc.</summary>
    public static bool IsPatchFile(string name) => PatchFiles.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsPatchAddOn(string folder) => string.Equals(folder, PatchAddOn, StringComparison.OrdinalIgnoreCase);

    /// <summary>A file name that is a script mod: a numbered slot, and not the patch's own.</summary>
    public static bool IsScriptMod(string name) => Slot.IsMatch(name) && !IsPatchFile(name);

    /// <summary>Whether an OpenSWBF2 version loads script mods and 1.3-style mods by itself.</summary>
    public static bool IsBuiltIn(string? version)
    {
        if (version == null) return false;
        int cut = version.IndexOfAny(new[] { '-', '+' });
        return Version.TryParse(cut < 0 ? version : version[..cut], out var parsed) && parsed >= BuiltInSince;
    }

    /// <summary>On and off script mods. Empty files are left out: players keep 0-byte
    /// placeholders in the slots, and the game skips them.</summary>
    public IReadOnlyList<ScriptModInfo> List()
    {
        var mods = new List<ScriptModInfo>();
        foreach (var (dir, enabled) in new[] { (_layout.LvlDir, true), (DisabledDir, false) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.lvl"))
                if (IsScriptMod(file.Name) && file.Length > 0)
                    mods.Add(new ScriptModInfo(Path.GetFileNameWithoutExtension(file.Name), file.FullName, enabled, KindOf(file.Name)));
        }
        return mods.OrderBy(m => m.Kind).ThenBy(m => SlotNumber(m.Name)).ToList();
    }

    public ScriptModInfo SetEnabled(ScriptModInfo mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod;
        string to = Path.Combine(enabled ? _layout.LvlDir : DisabledDir, Path.GetFileName(mod.Path));
        if (File.Exists(to)) throw new IOException($"{to} already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(mod.Path, to);
        return mod with { Path = to, Enabled = enabled };
    }

    /// <summary>The script mods anywhere under a folder (a download unpacked), leaving out
    /// files inside the given addon mod folders.</summary>
    public static IReadOnlyList<string> FindIn(string dir, IReadOnlyCollection<string> skipRoots)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.lvl", SearchOption.AllDirectories)
            .Where(f => IsScriptMod(Path.GetFileName(f)))
            .Where(f => !skipRoots.Any(r => f.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Copies script mod files into data\_lvl_pc. Returns their names.</summary>
    public IReadOnlyList<string> Install(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return Array.Empty<string>();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (File.Exists(Path.Combine(_layout.LvlDir, name)) || File.Exists(Path.Combine(DisabledDir, name)))
                throw new IOException($"A script mod in slot {Path.GetFileNameWithoutExtension(name)} is already installed. Remove it first to replace it.");
        }
        Directory.CreateDirectory(_layout.LvlDir);
        var added = new List<string>();
        foreach (string file in files)
        {
            File.Copy(file, Path.Combine(_layout.LvlDir, Path.GetFileName(file)));
            added.Add(Path.GetFileNameWithoutExtension(file));
        }
        return added;
    }

    /// <summary>
    /// Turns the unofficial 1.3 patch off in the game copy: AddOn\AAA-v1.3patch moves to
    /// AddOn_disabled\ and its lvls to data\_lvl_pc\_disabled\. Returns what moved; nothing the
    /// second time.
    /// </summary>
    public static IReadOnlyList<string> DisablePatch(InstallLayout layout)
    {
        var moved = new List<string>();
        string addOn = Path.Combine(layout.AddOn, PatchAddOn);
        string addOnTo = Path.Combine(layout.AddOnDisabled, PatchAddOn);
        if (Directory.Exists(addOn) && !Directory.Exists(addOnTo))
        {
            Directory.CreateDirectory(layout.AddOnDisabled);
            Directory.Move(addOn, addOnTo);
            moved.Add(PatchAddOn);
        }
        string disabled = new ScriptMods(layout).DisabledDir;
        foreach (string name in PatchFiles)
        {
            string from = Path.Combine(layout.LvlDir, name);
            string to = Path.Combine(disabled, name);
            if (!File.Exists(from) || File.Exists(to)) continue;
            Directory.CreateDirectory(disabled);
            File.Move(from, to);
            moved.Add(name);
        }
        return moved;
    }

    private static ScriptModKind KindOf(string name) =>
        name.StartsWith("custom_gc_", StringComparison.OrdinalIgnoreCase) ? ScriptModKind.Menus : ScriptModKind.Match;

    private static int SlotNumber(string name) => int.TryParse(name[(name.LastIndexOf('_') + 1)..], out int n) ? n : 0;
}
