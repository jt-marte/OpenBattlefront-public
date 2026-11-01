using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ModFixupsTests
{
    private static string BaseFile(InstallLayout layout) => Path.Combine(layout.LvlDir, "SIDE", "swbf1.lvl");
    private static string Bf1File(InstallLayout layout) => Path.Combine(layout.AddOn, "BF1", "data", "_LVL_PC", "SIDE", "swbf1.lvl");

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void The_pack_s_base_folder_soldiers_are_copied_into_BF1_once()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        Write(BaseFile(layout), "classic soldiers");
        Directory.CreateDirectory(Path.Combine(layout.AddOn, "BF1"));

        Assert.True(ModFixups.CompleteBf1(layout));
        Assert.Equal("classic soldiers", File.ReadAllText(Bf1File(layout)));
        Assert.False(File.Exists(Bf1File(layout) + ".copying"));
        // the base copy stays, and a second run leaves BF1 alone
        Assert.True(File.Exists(BaseFile(layout)));
        Assert.False(ModFixups.CompleteBf1(layout));
    }

    [Fact]
    public void Nothing_happens_without_BF1_or_without_the_file_or_when_BF1_has_its_own()
    {
        using var temp = new TempDir();
        var layout = new InstallLayout(temp.Sub("L"));
        Write(BaseFile(layout), "classic soldiers");
        Assert.False(ModFixups.CompleteBf1(layout)); // no BF1

        var other = new InstallLayout(temp.Sub("M"));
        Directory.CreateDirectory(Path.Combine(other.AddOn, "BF1"));
        Assert.False(ModFixups.CompleteBf1(other)); // BF1 but no base file (a stock copy)

        Write(Bf1File(layout), "the pack's own");
        Assert.False(ModFixups.CompleteBf1(layout));
        Assert.Equal("the pack's own", File.ReadAllText(Bf1File(layout)));
    }
}
