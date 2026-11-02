using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ModFixupsTests
{
    private static string BaseFile(InstallLayout layout, string file) => Path.Combine(layout.LvlDir, file);
    private static string Bf1File(InstallLayout layout, string file) => Path.Combine(layout.AddOn, "BF1", "data", "_LVL_PC", file);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void The_pack_s_base_folder_files_go_into_BF1_once()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        Write(BaseFile(layout, @"SIDE\swbf1.lvl"), "classic soldiers");
        Write(BaseFile(layout, @"SIDE\heroes.lvl"), "heroes");
        Write(BaseFile(layout, @"sound\bs1.lvl"), "bespin sounds");
        Write(BaseFile(layout, @"SIDE\rep.lvl"), "stock side, not BF1's");
        Directory.CreateDirectory(Path.Combine(layout.AddOn, "BF1", "data", "_LVL_PC", "Sound"));

        Assert.Equal(new[] { @"SIDE\swbf1.lvl", @"SIDE\heroes.lvl", @"SOUND\bs1.lvl" }, ModFixups.CompleteBf1(layout));
        Assert.Equal("classic soldiers", File.ReadAllText(Bf1File(layout, @"SIDE\swbf1.lvl")));
        Assert.Equal("heroes", File.ReadAllText(Bf1File(layout, @"SIDE\heroes.lvl")));
        Assert.Equal("bespin sounds", File.ReadAllText(Bf1File(layout, @"Sound\bs1.lvl")));
        Assert.False(File.Exists(Bf1File(layout, @"SIDE\rep.lvl")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(layout.AddOn, "BF1"), "*.copying", SearchOption.AllDirectories));
        // the base copies stay, and a second run leaves BF1 alone
        Assert.True(File.Exists(BaseFile(layout, @"SIDE\heroes.lvl")));
        Assert.Empty(ModFixups.CompleteBf1(layout));
    }

    [Fact]
    public void Nothing_happens_without_BF1_or_without_the_files_or_when_BF1_has_its_own()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        Write(BaseFile(layout, @"SIDE\swbf1.lvl"), "classic soldiers");
        Assert.Empty(ModFixups.CompleteBf1(layout)); // no BF1

        var other = new InstallLayout(temp.Sub("M"));
        Directory.CreateDirectory(Path.Combine(other.AddOn, "BF1"));
        Assert.Empty(ModFixups.CompleteBf1(other)); // BF1 but no base files (a stock copy)

        Write(Bf1File(layout, @"SIDE\swbf1.lvl"), "the pack's own");
        Assert.Empty(ModFixups.CompleteBf1(layout));
        Assert.Equal("the pack's own", File.ReadAllText(Bf1File(layout, @"SIDE\swbf1.lvl")));
    }
}
