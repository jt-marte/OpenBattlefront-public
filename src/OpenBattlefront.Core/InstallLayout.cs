namespace OpenBattlefront.Core;

/// <summary>
/// Where everything lives. The game runs from <see cref="Game"/> (its working directory):
/// the copied game data, our exe and DLLs, the rebuilt core lvls, mods and saves.
/// </summary>
public sealed class InstallLayout
{
    public InstallLayout(string root) => Root = Path.GetFullPath(root);

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBattlefront");

    public string Root { get; }
    public string Game => Path.Combine(Root, "Game");
    public string GameExe => Path.Combine(Game, "BattlefrontII.exe");
    public string LvlDir => Path.Combine(Game, "data", "_lvl_pc");
    public string AddOn => Path.Combine(Game, "AddOn");
    public string AddOnDisabled => Path.Combine(Game, "AddOn_disabled");
    public string SaveGames => Path.Combine(Game, "SaveGames");

    /// <summary>The player's four stock core lvls, kept so every update can rebuild them.</summary>
    public string Stock => Path.Combine(Root, "Stock");
    public string StockLvlDir => Path.Combine(Stock, "data", "_lvl_pc");

    public string Builds => Path.Combine(Root, "Builds");
    public string Downloads => Path.Combine(Root, "Downloads");
    public string Temp => Path.Combine(Root, "Temp");
    public string Logs => Path.Combine(Root, "Logs");
    public string Cache => Path.Combine(Root, "Cache");

    /// <summary>The dedicated server's /configdir: ServerSettings.cfg and ServerRotation.cfg.</summary>
    public string ServerSettings => Path.Combine(Root, "Server", "settings");

    /// <summary>The game's own crash reports (pcCrash).</summary>
    public static string CrashReports =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSWBF2", "crashes");

    public bool HasCoreLvls => StockLvls.Names.All(n => File.Exists(Path.Combine(LvlDir, n + ".lvl")));
}
