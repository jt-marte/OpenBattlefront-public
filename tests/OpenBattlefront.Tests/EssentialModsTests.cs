using System.Diagnostics;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class EssentialModsTests
{
    private static readonly CatalogMod Xbox = new(
        "xbox-dlc", "Xbox DLC", "Maps and heroes.", "Author", "https://www.moddb.com/mods/xbox",
        new[] { "DLC_XBOX", "UPDATE_XBOX" }, Available: true);

    [Fact]
    public void Nothing_is_offered_now_the_launcher_includes_the_pack_and_the_DLC()
    {
        Assert.Empty(EssentialMods.All);
        using var temp = new TempDir();
        Assert.Empty(EssentialMods.Missing(new InstallLayout(temp.Sub("L"))));
    }

    [Fact]
    public void Installed_means_every_folder_is_there_on_or_off()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        Assert.False(EssentialMods.IsInstalled(layout, Xbox));

        Directory.CreateDirectory(Path.Combine(layout.AddOn, "DLC_XBOX"));
        Assert.False(EssentialMods.IsInstalled(layout, Xbox));
        Directory.CreateDirectory(Path.Combine(layout.AddOnDisabled, "UPDATE_XBOX"));
        Assert.True(EssentialMods.IsInstalled(layout, Xbox));

        // What the launcher recorded at install time wins over the catalog's folder names.
        var recorded = new Dictionary<string, string[]> { ["xbox-dlc"] = new[] { "ADDON_XBOX" } };
        Assert.False(EssentialMods.IsInstalled(layout, Xbox, recorded));
        Directory.CreateDirectory(Path.Combine(layout.AddOn, "ADDON_XBOX"));
        Assert.True(EssentialMods.IsInstalled(layout, Xbox, recorded));
    }

    [Fact]
    public void Installs_mods_from_a_7z()
    {
        using var temp = new TempDir();
        temp.File(@"src\DLC_XBOX\addme.script", "dlc");
        temp.File(@"src\UPDATE_XBOX\addme.script", "update");
        string archive = temp.Sub("xbox.7z");
        // Windows' own tar.exe writes 7z too, so the test needs no extra tools.
        var tar = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
        {
            ArgumentList = { "--format", "7zip", "-cf", archive, "-C", temp.Sub("src"), "DLC_XBOX", "UPDATE_XBOX" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        tar.WaitForExit();
        Assert.Equal(0, tar.ExitCode);

        var layout = new InstallLayout(temp.Sub("L"));
        Assert.Equal(new[] { "DLC_XBOX", "UPDATE_XBOX" }, new ModManager(layout).Install(archive).Order());
        Assert.True(EssentialMods.IsInstalled(layout, Xbox));
    }

    [Theory]
    [InlineData("mod.zip", true)]
    [InlineData("mod.7z", true)]
    [InlineData("MOD.RAR", true)]
    [InlineData("mod.tar.gz", true)]
    [InlineData("setup.exe", false)]
    [InlineData("readme.txt", false)]
    public void Archive_types(string name, bool archive) => Assert.Equal(archive, Archives.IsArchive(name));
}
