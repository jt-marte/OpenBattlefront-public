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
    public static readonly IReadOnlyList<CatalogMod> All = new[]
    {
        new CatalogMod(
            "xbox-dlc",
            "Xbox DLC and Title Update content",
            "Bespin, Rhen Var, Rhen Var Harbor and Yavin Arena, Kit Fisto and Asajj Ventress, and the Kashyyyk Hero Assault.",
            "iamastupid",
            "https://www.moddb.com/mods/xbox-dlc-content-for-pc/downloads/xbox-dlc-and-title-update-content-for-pc",
            new[] { "DLC_XBOX", "UPDATE_XBOX" },
            Available: true),
        // OpenSWBF2 0.2.0 runs it (1.3-style mod support is built in), but it ships as an .exe
        // installer, which the launcher doesn't unpack yet. (The Remaster is not offered: it
        // replaces OpenSWBF2's own menus, and its AI heroes and award options are built in.)
        new CatalogMod(
            "conversion-pack",
            "Battlefront Conversion Pack",
            "The original Battlefront's maps and sides, rebuilt for Battlefront II.",
            "Conversion Pack team",
            "https://www.moddb.com/addons/battlefront-conversion-pack-20",
            new[] { "BF1" },
            Available: false,
            Note: "It comes as an installer the launcher can't unpack yet. Install it into your own game, then drop its BF1 folder here."),
    };

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
