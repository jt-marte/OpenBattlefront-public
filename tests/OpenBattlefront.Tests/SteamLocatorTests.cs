using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class SteamLocatorTests
{
    [Fact]
    public void Reads_library_folders()
    {
        const string vdf = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"C:\\Program Files (x86)\\Steam"
                "label"		""
                "apps"
                {
                    "6060"		"9530000000"
                }
            }
            "1"
            {
                "path"		"D:\\SteamLibrary"
            }
        }
        """;
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLocator.ReadLibraryPaths(vdf));
    }

    [Fact]
    public void Reads_the_install_folder_from_the_app_manifest()
    {
        const string acf = """
        "AppState"
        {
            "appid"		"6060"
            "name"		"STAR WARS™ Battlefront™ II (Classic, 2005)"
            "installdir"		"Star Wars Battlefront II Classic"
        }
        """;
        Assert.Equal("Star Wars Battlefront II Classic", SteamLocator.ReadInstallDir(acf));
        Assert.Null(SteamLocator.ReadInstallDir("{}"));
    }

    [Fact]
    public void Lists_the_steam_folder_and_its_libraries()
    {
        using var temp = new TempDir();
        temp.File(@"Steam\steamapps\libraryfolders.vdf",
            "\"libraryfolders\" { \"0\" { \"path\" \"" + temp.Sub("Steam").Replace(@"\", @"\\") + "\" } " +
            "\"1\" { \"path\" \"" + temp.Sub("Lib2").Replace(@"\", @"\\") + "\" } \"2\" { \"path\" \"Z:\\\\gone\" } }");
        Directory.CreateDirectory(temp.Sub("Lib2"));
        Assert.Equal(new[] { temp.Sub("Steam"), temp.Sub("Lib2") }, SteamLocator.Libraries(temp.Sub("Steam")));
        Assert.Empty(SteamLocator.Libraries(null));
    }
}
