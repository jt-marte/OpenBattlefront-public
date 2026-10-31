using System.Diagnostics;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class EssentialModsTests
{
    [Fact]
    public void Only_the_Xbox_DLC_is_offered_while_the_pack_needs_its_installer()
    {
        Assert.Equal(new[] { "xbox-dlc" }, EssentialMods.All.Where(m => m.Available).Select(m => m.Id));
        Assert.All(EssentialMods.All.Where(m => !m.Available), m => Assert.NotNull(m.Note));
        Assert.All(EssentialMods.All, m => Assert.StartsWith("https://www.moddb.com/", m.PageUrl));
    }

    [Fact]
    public void Installed_means_every_folder_is_there_on_or_off()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        var xbox = EssentialMods.All.Single(m => m.Id == "xbox-dlc");
        Assert.Contains(xbox, EssentialMods.Missing(layout));

        Directory.CreateDirectory(Path.Combine(layout.AddOn, "DLC_XBOX"));
        Assert.False(EssentialMods.IsInstalled(layout, xbox));
        Directory.CreateDirectory(Path.Combine(layout.AddOnDisabled, "UPDATE_XBOX"));
        Assert.True(EssentialMods.IsInstalled(layout, xbox));
        Assert.Empty(EssentialMods.Missing(layout));

        // What the launcher recorded at install time wins over the catalog's folder names.
        var recorded = new Dictionary<string, string[]> { ["xbox-dlc"] = new[] { "ADDON_XBOX" } };
        Assert.False(EssentialMods.IsInstalled(layout, xbox, recorded));
        Directory.CreateDirectory(Path.Combine(layout.AddOn, "ADDON_XBOX"));
        Assert.True(EssentialMods.IsInstalled(layout, xbox, recorded));
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
        Assert.True(EssentialMods.IsInstalled(layout, EssentialMods.All[0]));
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
