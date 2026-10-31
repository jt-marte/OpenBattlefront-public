namespace OpenBattlefront.Core;

/// <summary>
/// The launcher's jobs, in the order a first run does them: import the player's game data,
/// install an OpenSWBF2 build, build the core lvls, add the included content. Updates and
/// rollback reuse the same steps.
/// </summary>
public sealed class GameSetup
{
    private const long SpareBytes = 512L << 20;
    private readonly string _stateFile;
    private readonly Version _launcherVersion;
    private readonly HttpClient _http;

    /// <param name="content">The included content; Content\ next to the exe by default.</param>
    public GameSetup(LauncherState state, string stateFile, Version launcherVersion, GameContent? content = null)
    {
        State = state;
        _stateFile = stateFile;
        _launcherVersion = launcherVersion;
        _http = ReleaseClient.CreateHttp(launcherVersion);
        Content = content ?? new GameContent(GameContent.DefaultDir);
    }

    public LauncherState State { get; }
    public GameContent Content { get; }
    public InstallLayout Layout => State.Layout;
    public BuildStore Builds => new(Layout);
    public ReleaseClient Releases => new(State.UpdateRepo, _http);
    public BuildManifest? Installed => Builds.TryLoad(State.InstalledVersion);
    public bool CanRollBack => Builds.TryLoad(State.PreviousVersion) != null;

    public bool IsReady =>
        State.ImportComplete && State.InstalledVersion != null && File.Exists(Layout.GameExe) && Layout.HasCoreLvls;

    public IReadOnlyDictionary<string, string[]> AcceptedLvls => StockLvls.Merge(Installed?.StockLvls);

    public void Save() => State.Save(_stateFile);

    public static string Explain(ValidationResult result)
    {
        if (result.IsGog)
            return StockLvls.GogNotYet;
        if (result.Missing.Any())
            return $"Some core game files are missing ({string.Join(", ", result.Missing)}). Drop a complete copy of the game.";
        return $"This copy has modified core files ({string.Join(", ", result.Modified)}), usually from the 1.3 patch or a mod " +
               "that replaces them. OpenBattlefront needs the original v1.1 files. In Steam: Properties > Installed Files > " +
               "Verify integrity. In GOG Galaxy: Manage installation > Verify / Repair. Mods in your addon folder are fine.";
    }

    public Task<ValidationResult> ValidateAsync(string gameData, CancellationToken ct) =>
        Task.Run(() => GameValidator.Validate(gameData, AcceptedLvls), ct);

    public async Task ImportAsync(string gameData, ImportOptions options, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        var layout = Layout;
        if (GameLocator.IsInside(gameData, layout.Root))
            throw new InvalidOperationException("That is OpenBattlefront's own folder. Drop your original copy of the game.");

        progress.Report(new StepUpdate("Checking your game files", "Core lvl SHA-256"));
        var validation = await ValidateAsync(gameData, ct);
        if (!validation.Ok) throw new InvalidDataException(Explain(validation));

        progress.Report(new StepUpdate("Getting ready to copy", "Listing files"));
        var plan = await Task.Run(() => Importer.Plan(gameData, layout, options), ct);
        Directory.CreateDirectory(layout.Root);
        long free = Importer.FreeBytes(layout.Root);
        if (plan.PendingBytes + SpareBytes > free)
            throw new IOException($"Not enough disk space: the copy needs {Sizes.Format(plan.PendingBytes)} and the drive has {Sizes.Format(free)} free. " +
                                  "Free some space or pick another install folder in Settings.");

        State.ImportedFrom = gameData;
        State.ImportComplete = false;
        Save();

        var copy = new Progress<TransferProgress>(t => progress.Report(new StepUpdate(
            "Copying game files",
            $"{Sizes.Format(t.BytesDone)} of {Sizes.Format(t.BytesTotal)}  ·  {Sizes.Format((long)t.BytesPerSecond)}/s",
            t.Fraction)));
        await Task.Run(() => Importer.CopyAsync(plan, copy, ct), ct);

        State.ImportComplete = true;
        State.ImportedAt = DateTime.Now;
        Save();

        if (Installed is { } manifest) await BuildLvlsAsync(manifest, progress, ct);
        await InstallContentAsync(progress, ct);
    }

    /// <summary>Whether the launcher carries included content the game copy doesn't have yet. A
    /// broken content.json counts, so that installing it reports the problem.</summary>
    public bool NeedsContent
    {
        get
        {
            if (!State.ImportComplete) return false;
            try { return Content.Load() is { } manifest && manifest.Version != State.ContentVersion; }
            catch (Exception e) when (e is InvalidDataException or IOException) { return true; }
        }
    }

    /// <summary>Puts the included content in Game\AddOn once the game is imported, the first time
    /// and whenever the launcher brings a new version of it. Returns whether it did.</summary>
    public async Task<bool> InstallContentAsync(IProgress<StepUpdate> progress, CancellationToken ct)
    {
        if (!State.ImportComplete) return false;
        var content = Content.Load();
        if (content == null || content.Version == State.ContentVersion) return false;
        if (GameLauncher.IsRunning(Layout)) throw new InvalidOperationException("Close the game first.");
        await Task.Run(() => Content.InstallAsync(content, Layout, progress, ct), ct);
        State.ContentVersion = content.Version;
        Save();
        return true;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct) => await Releases.GetLatestAsync(ct);

    public async Task DownloadAndInstallAsync(ReleaseInfo release, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        string zip = Path.Combine(Layout.Downloads, release.AssetName);
        var download = new Progress<TransferProgress>(t => progress.Report(new StepUpdate(
            $"Downloading OpenSWBF2 {release.Version}",
            $"{Sizes.Format(t.BytesDone)} of {Sizes.Format(t.BytesTotal)}  ·  {Sizes.Format((long)t.BytesPerSecond)}/s",
            t.Fraction)));
        await Releases.DownloadAsync(release, zip, download, ct);
        try { await InstallBuildAsync(zip, progress, ct); }
        finally { File.Delete(zip); }
    }

    public async Task InstallBuildAsync(string zip, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        var builds = Builds;
        var manifest = await Task.Run(() => builds.Unpack(zip, progress, ct), ct);
        if (manifest.NeedsNewerLauncher(_launcherVersion))
            throw new InvalidOperationException($"OpenSWBF2 {manifest.Version} needs OpenBattlefront {manifest.MinLauncherVersion} or newer.");
        await ActivateAsync(manifest, progress, ct);
    }

    public async Task RollBackAsync(IProgress<StepUpdate> progress, CancellationToken ct)
    {
        var previous = Builds.TryLoad(State.PreviousVersion) ?? throw new InvalidOperationException("There is no earlier build to go back to.");
        await ActivateAsync(previous, progress, ct);
    }

    public async Task RebuildLvlsAsync(IProgress<StepUpdate> progress, CancellationToken ct)
    {
        var manifest = Installed ?? throw new InvalidOperationException("Install a game build first.");
        await BuildLvlsAsync(manifest, progress, ct);
    }

    /// <summary>Every game file that differs from the installed build, or none.</summary>
    public IReadOnlyList<string> Verify()
    {
        var manifest = Installed ?? throw new InvalidOperationException("Install a game build first.");
        var problems = manifest.Verify(Builds.Dir(manifest.Version)).Select(p => "build: " + p).ToList();
        foreach (var file in manifest.RuntimeFiles)
        {
            string path = Path.Combine(Layout.Game, file.Path.Replace('/', '\\'));
            if (!File.Exists(path)) problems.Add($"missing {file.Path}");
            else if (!string.Equals(Hashing.Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"changed {file.Path}");
        }
        if (!Layout.HasCoreLvls) problems.Add("core lvls not built");
        return problems;
    }

    /// <summary>Puts a build in Game\ and builds the core lvls with it. If that fails, the build
    /// that was there before is put back.</summary>
    private async Task ActivateAsync(BuildManifest manifest, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        if (GameLauncher.IsRunning(Layout)) throw new InvalidOperationException("Close the game first.");
        string? before = State.InstalledVersion;
        var beforeManifest = before != manifest.Version ? Builds.TryLoad(before) : null;
        try
        {
            progress.Report(new StepUpdate($"Installing OpenSWBF2 {manifest.Version}"));
            await Task.Run(() => Builds.Deploy(manifest), ct);
            await BuildLvlsAsync(manifest, progress, ct);
        }
        catch when (beforeManifest != null)
        {
            progress.Report(new StepUpdate($"Putting OpenSWBF2 {beforeManifest.Version} back"));
            try
            {
                await Task.Run(() => Builds.Deploy(beforeManifest), CancellationToken.None);
                await BuildLvlsAsync(beforeManifest, progress, CancellationToken.None);
            }
            catch (Exception)
            {
                // The original error is the one worth reporting.
            }
            throw;
        }
        if (before != null && before != manifest.Version) State.PreviousVersion = before;
        State.InstalledVersion = manifest.Version;
        Save();
        Builds.Prune(State.InstalledVersion, State.PreviousVersion);
    }

    private async Task BuildLvlsAsync(BuildManifest manifest, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        if (!State.ImportComplete) return;
        progress.Report(new StepUpdate("Building core game files", "shell, common, ingame and mission"));
        await LvlBuilder.BuildAsync(Layout, Builds.Dir(manifest.Version), manifest, ct);
    }
}
