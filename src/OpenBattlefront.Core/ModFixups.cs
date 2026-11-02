using System.Runtime.InteropServices;

namespace OpenBattlefront.Core;

/// <summary>Fixes to mods as their installers left them.</summary>
public static class ModFixups
{
    /// <summary>Files BF1's missions load by a plain path (the base game's data\_lvl_pc) that the
    /// Conversion Pack's installer put in the base game instead of addon\BF1: the Classic Conquest
    /// soldiers (swbf1.lvl), the heroes, the extra classes and clone legions, the vehicles, and
    /// three sound banks. Found by listing the plain paths in BF1's mission.lvl that a stock game
    /// lacks.</summary>
    public static readonly IReadOnlyList<string> Bf1BaseFiles = new[]
    {
        @"SIDE\swbf1.lvl", @"SIDE\heroes.lvl", @"SIDE\infantry.lvl", @"SIDE\vehicles.lvl", @"SIDE\rvs.lvl",
        @"SIDE\rep41st.lvl", @"SIDE\rep146th.lvl", @"SIDE\rep212th.lvl", @"SIDE\rep327th.lvl", @"SIDE\repmarine.lvl",
        @"SIDE\items.lvl", @"SOUND\bs1.lvl", @"SOUND\psa.lvl", @"SOUND\stm.lvl",
    };

    /// <summary>A server or a friend gets only addon\BF1, so there those files were missing:
    /// Classic Conquest had no soldiers (the timer stopped at 1 s, nobody spawned) and BF1's maps
    /// lost their heroes, classes and vehicles. OpenSWBF2 0.2.8 reads a mod mission's base files
    /// from the mod's own folder too, so a copy there travels with BF1. Each one the base game
    /// has and BF1 lacks is hard-linked in (no extra disk space; both are under the game folder),
    /// or copied where a link can't be made. Returns the ones it added.</summary>
    public static IReadOnlyList<string> CompleteBf1(InstallLayout layout)
    {
        var added = new List<string>();
        string bf1 = Path.Combine(layout.AddOn, "BF1");
        if (!Directory.Exists(bf1)) return added;
        foreach (string file in Bf1BaseFiles)
        {
            string source = Path.Combine(layout.LvlDir, file);
            string target = Path.Combine(bf1, "data", "_LVL_PC", file);
            if (!File.Exists(source) || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string partial = target + ".copying";
            File.Delete(partial);
            if (!CreateHardLink(partial, source, IntPtr.Zero)) File.Copy(source, partial, overwrite: true);
            File.Move(partial, target);
            added.Add(file);
        }
        return added;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}
