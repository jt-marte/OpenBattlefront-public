using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

/// <summary>
/// The whole first run on real files: import a stock GameData, install a real release package,
/// build the core lvls. It copies about 10 GB, so it only runs when asked:
///   OBF_E2E_GAMEDATA = a stock Steam GameData folder (only read)
///   OBF_E2E_PACKAGE  = an OpenSWBF2-x.y.z-x64.zip from tools\package-release.ps1
///   OBF_E2E_ROOT     = an empty folder to install into (launcher.json goes there too)
/// </summary>
public class EndToEndTests
{
    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task Imports_installs_and_builds_from_real_files()
    {
        string? gameData = Environment.GetEnvironmentVariable("OBF_E2E_GAMEDATA");
        string? package = Environment.GetEnvironmentVariable("OBF_E2E_PACKAGE");
        string? root = Environment.GetEnvironmentVariable("OBF_E2E_ROOT");
        if (gameData == null || package == null || root == null) return;

        string stateFile = Path.Combine(root, "launcher.json");
        var setup = new GameSetup(new LauncherState { InstallRoot = root }, stateFile, new Version(0, 1, 0));
        var steps = new List<string>();
        var progress = new SyncProgress(u => { if (steps.LastOrDefault() != u.Title) steps.Add(u.Title); });

        await setup.ImportAsync(gameData, new ImportOptions(), progress, CancellationToken.None);
        Assert.True(setup.State.ImportComplete);
        Assert.False(setup.IsReady);

        await setup.InstallBuildAsync(package, progress, CancellationToken.None);
        Assert.True(setup.IsReady);
        Assert.Empty(setup.Verify());
        Assert.Contains("Building core game files", steps);

        // Resuming finds nothing left to copy.
        Assert.Equal(0, Importer.Plan(gameData, setup.Layout, new ImportOptions()).PendingBytes);
        Assert.Equal(setup.State.InstalledVersion, LauncherState.Load(stateFile).InstalledVersion);
    }

    private sealed class SyncProgress : IProgress<StepUpdate>
    {
        private readonly Action<StepUpdate> _report;
        public SyncProgress(Action<StepUpdate> report) => _report = report;
        public void Report(StepUpdate value) => _report(value);
    }
}
