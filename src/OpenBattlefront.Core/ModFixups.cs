namespace OpenBattlefront.Core;

/// <summary>Fixes to mods as their installers left them.</summary>
public static class ModFixups
{
    /// <summary>The BF1 Conversion Pack's installer puts its Classic Conquest soldiers,
    /// SIDE\swbf1.lvl, in the base game folder, not in addon\BF1. A server or a friend gets only
    /// addon\BF1, so there Classic Conquest had no soldiers: nobody spawned and games crashed in
    /// the spawn screen. OpenSWBF2 0.2.8 reads a mod mission's base files from the mod's own
    /// folder too, so a copy there travels with BF1. True when it copied.</summary>
    public static bool CompleteBf1(InstallLayout layout)
    {
        string bf1 = Path.Combine(layout.AddOn, "BF1");
        string source = Path.Combine(layout.LvlDir, "SIDE", "swbf1.lvl");
        string target = Path.Combine(bf1, "data", "_LVL_PC", "SIDE", "swbf1.lvl");
        if (!Directory.Exists(bf1) || !File.Exists(source) || File.Exists(target)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string partial = target + ".copying";
        File.Copy(source, partial, overwrite: true);
        File.Move(partial, target);
        return true;
    }
}
