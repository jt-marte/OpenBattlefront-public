using System.IO.Compression;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ScriptModsTests
{
    [Theory]
    [InlineData("user_script_9.lvl", true)]
    [InlineData("USER_SCRIPT_0.LVL", true)]
    [InlineData("custom_gc_99.lvl", true)]
    [InlineData("user_script_100.lvl", false)]
    [InlineData("user_script_10.lvl", false)] // the 1.3 patch's own
    [InlineData("custom_gc_10.lvl", false)]
    [InlineData("v1.3patch_strings.lvl", false)]
    [InlineData("user_script_9.lvl.orig", false)]
    [InlineData("shell.lvl", false)]
    public void Names_a_script_mod(string name, bool expected) => Assert.Equal(expected, ScriptMods.IsScriptMod(name));

    [Theory]
    [InlineData(null, false)]
    [InlineData("0.1.1", false)]
    [InlineData("0.2.0", true)]
    [InlineData("0.2.0-beta", true)]
    [InlineData("1.0", true)]
    [InlineData("junk", false)]
    public void Knows_which_builds_have_it_built_in(string? version, bool expected) => Assert.Equal(expected, ScriptMods.IsBuiltIn(version));

    [Fact]
    public void Lists_and_toggles_script_mods_but_not_placeholders_or_the_patch()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        temp.File("L\\Game\\data\\_lvl_pc\\user_script_9.lvl", "skins");
        temp.File("L\\Game\\data\\_lvl_pc\\custom_gc_1.lvl", "gc");
        temp.File("L\\Game\\data\\_lvl_pc\\user_script_7.lvl", ""); // a 0-byte placeholder
        temp.File("L\\Game\\data\\_lvl_pc\\user_script_10.lvl", "patch");
        var scripts = new ScriptMods(layout);

        var list = scripts.List();
        Assert.Equal(new[] { "user_script_9", "custom_gc_1" }, list.Select(m => m.Name));
        Assert.Equal(ScriptModKind.Match, list[0].Kind);
        Assert.Equal(ScriptModKind.Menus, list[1].Kind);

        var off = scripts.SetEnabled(list[0], false);
        Assert.False(File.Exists(Path.Combine(layout.LvlDir, "user_script_9.lvl")));
        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "_disabled", "user_script_9.lvl")));
        Assert.False(scripts.List().Single(m => m.Name == "user_script_9").Enabled);
        scripts.SetEnabled(off, true);
        Assert.True(scripts.List().All(m => m.Enabled));
    }

    [Fact]
    public void Switches_the_patch_off_once()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        temp.File("L\\Game\\AddOn\\AAA-v1.3patch\\addme.script");
        temp.File("L\\Game\\AddOn\\BF1\\addme.script");
        temp.File("L\\Game\\data\\_lvl_pc\\custom_gc_10.lvl", "patch");
        temp.File("L\\Game\\data\\_lvl_pc\\v1.3patch_strings.lvl", "patch");
        temp.File("L\\Game\\data\\_lvl_pc\\custom_gc_1.lvl", "mod");

        Assert.Equal(new[] { "AAA-v1.3patch", "custom_gc_10.lvl", "v1.3patch_strings.lvl" }, ScriptMods.DisablePatch(layout));
        Assert.True(Directory.Exists(Path.Combine(layout.AddOnDisabled, "AAA-v1.3patch")));
        Assert.True(Directory.Exists(Path.Combine(layout.AddOn, "BF1")));
        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "_disabled", "custom_gc_10.lvl")));
        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "custom_gc_1.lvl")));
        Assert.Empty(ScriptMods.DisablePatch(layout));

        // It shows as installed but can't be switched back on.
        var mods = new ModManager(layout);
        var patch = mods.List().Single(m => m.IsPatch);
        Assert.False(patch.Enabled);
        Assert.Throws<IOException>(() => mods.SetEnabled(patch, true));
    }

    [Fact]
    public void Installs_script_mods_from_a_file_or_an_archive()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        var mods = new ModManager(layout);

        string single = temp.File("Download\\user_script_9.lvl", "skins");
        Assert.Equal(new[] { "user_script_9" }, mods.Install(single));
        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "user_script_9.lvl")));
        Assert.Throws<IOException>(() => mods.Install(single));

        // An archive with an addon mod and a script mod outside it; the addon's own copy of a
        // script lvl stays inside the addon.
        temp.File("src\\addon\\XYZ\\addme.script");
        temp.File("src\\addon\\XYZ\\data\\_lvl_pc\\user_script_5.lvl", "inside");
        temp.File("src\\data\\_lvl_pc\\custom_gc_3.lvl", "gc");
        string zip = temp.Sub("pack.zip");
        ZipFile.CreateFromDirectory(temp.Sub("src"), zip);
        Assert.Equal(new[] { "XYZ", "custom_gc_3" }, mods.Install(zip).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(layout.LvlDir, "custom_gc_3.lvl")));
        Assert.False(File.Exists(Path.Combine(layout.LvlDir, "user_script_5.lvl")));
    }

    [Fact]
    public void Refuses_the_13_patch()
    {
        using var temp = new TempDir();
        temp.File("src\\AAA-v1.3patch\\addme.script");
        temp.File("src\\data\\_lvl_pc\\user_script_10.lvl", "patch");
        var mods = new ModManager(new InstallLayout(temp.Sub("L")));

        var e = Assert.Throws<InvalidDataException>(() => mods.Install(temp.Sub("src")));
        Assert.Contains("1.3 patch", e.Message);
    }
}
