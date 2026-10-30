using System.IO.Compression;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ModManagerTests
{
    [Fact]
    public void Installs_a_mod_folder_and_toggles_it()
    {
        using var temp = new TempDir();
        temp.File("Download\\ABC\\addme.script", "mod");
        temp.File("Download\\ABC\\data\\_LVL_PC\\abc.lvl", "map");
        var layout = new InstallLayout(temp.Sub("L"));
        var mods = new ModManager(layout);

        Assert.Equal(new[] { "ABC" }, mods.Install(temp.Sub("Download")));
        Assert.True(File.Exists(Path.Combine(layout.AddOn, "ABC", "data", "_LVL_PC", "abc.lvl")));

        var mod = mods.List().Single();
        Assert.True(mod.Enabled);
        mod = mods.SetEnabled(mod, false);
        Assert.False(Directory.Exists(Path.Combine(layout.AddOn, "ABC")));
        Assert.True(Directory.Exists(Path.Combine(layout.AddOnDisabled, "ABC")));
        Assert.False(mods.List().Single().Enabled);
        mods.SetEnabled(mod, true);
        Assert.True(mods.List().Single().Enabled);
    }

    [Fact]
    public void Installs_every_mod_in_a_zip()
    {
        using var temp = new TempDir();
        temp.File("src\\addon\\ABC\\addme.script");
        temp.File("src\\addon\\XYZ\\addme.script");
        string zip = temp.Sub("pack.zip");
        ZipFile.CreateFromDirectory(temp.Sub("src"), zip);
        var mods = new ModManager(new InstallLayout(temp.Sub("L")));

        Assert.Equal(new[] { "ABC", "XYZ" }, mods.Install(zip).Order());
        Assert.Empty(Directory.GetDirectories(new InstallLayout(temp.Sub("L")).Temp));
    }

    [Fact]
    public void A_zip_with_the_mod_at_its_root_is_named_after_the_zip()
    {
        using var temp = new TempDir();
        temp.File("src\\addme.script");
        string zip = temp.Sub("KOR.zip");
        ZipFile.CreateFromDirectory(temp.Sub("src"), zip);
        Assert.Equal(new[] { "KOR" }, new ModManager(new InstallLayout(temp.Sub("L"))).Install(zip));
    }

    [Fact]
    public void Refuses_non_mods_and_duplicates()
    {
        using var temp = new TempDir();
        temp.File("NotAMod\\readme.txt");
        temp.File("ABC\\addme.script");
        temp.File("mod.rar");
        var mods = new ModManager(new InstallLayout(temp.Sub("L")));

        Assert.Throws<InvalidDataException>(() => mods.Install(temp.Sub("NotAMod")));
        Assert.Throws<InvalidDataException>(() => mods.Install(temp.Sub("mod.rar")));
        mods.Install(temp.Sub("ABC"));
        Assert.Throws<IOException>(() => mods.Install(temp.Sub("ABC")));
    }
}
