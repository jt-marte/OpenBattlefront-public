namespace OpenBattlefront.Core;

/// <summary>A community mod the launcher offers as part of "Essential mods". It is fetched from
/// its author's own page (the player clicks the download there) and unpacked into AddOn\.</summary>
public sealed record CatalogMod(
    string Id,
    string Name,
    string Summary,
    string Author,
    string PageUrl,
    string[] Folders,
    bool Available,
    string? Note = null);

public static class EssentialMods
{
    /// <summary>Empty for now: the Battlefront Conversion Pack and the Xbox DLC come inside the
    /// launcher's package (GameContent), and the Xbox Title Update is left out on purpose. (The
    /// Remaster is not offered: it replaces OpenSWBF2's own menus, and its AI heroes and award
    /// options are built in.) The page and the download window come back when a mod is listed.</summary>
    public static readonly IReadOnlyList<CatalogMod> All = Array.Empty<CatalogMod>();

    /// <summary>Installed means every folder it brings is in AddOn\ (or switched off in
    /// AddOn_disabled\). The folders the launcher recorded when it installed the mod win over
    /// the catalog's guess, since authors rename them between versions.</summary>
    public static bool IsInstalled(InstallLayout layout, CatalogMod mod, IReadOnlyDictionary<string, string[]>? recorded = null)
    {
        string[] folders = recorded != null && recorded.TryGetValue(mod.Id, out var seen) && seen.Length > 0 ? seen : mod.Folders;
        return folders.All(f => Directory.Exists(Path.Combine(layout.AddOn, f)) || Directory.Exists(Path.Combine(layout.AddOnDisabled, f)));
    }

    public static IEnumerable<CatalogMod> Missing(InstallLayout layout, IReadOnlyDictionary<string, string[]>? recorded = null) =>
        All.Where(m => m.Available && !IsInstalled(layout, m, recorded));
}
