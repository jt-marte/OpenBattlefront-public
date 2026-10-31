using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

public enum PlayStage { Welcome, Checking, Review, Invalid, NeedsBuild, Ready }

public sealed class PlayViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private PlayStage _stage;
    private string? _source;
    private ImportPreview? _preview;
    private long _free;
    private bool _importMods = true;
    private string? _invalidMessage;
    private ReleaseInfo? _latest;
    private string _releaseNote = "";
    private bool _dragHover;
    private bool _isGameRunning;
    private Process? _game;
    private IReadOnlyList<Prerequisite> _missing = Array.Empty<Prerequisite>();
    private string? _steamCopy;
    private bool _steamSearched;

    public PlayViewModel(MainViewModel main)
    {
        _main = main;
        BrowseCommand = new RelayCommand(Browse, () => !Activity.IsBusy);
        ResumeCommand = new RelayCommand(() => _ = HandleDropAsync(Setup.State.ImportedFrom!), () => CanResume && !Activity.IsBusy);
        ImportCommand = new RelayCommand(() => _ = ImportAsync(), () => _source != null && !Activity.IsBusy);
        BackCommand = new RelayCommand(() => Refresh(keepReview: false));
        PlayCommand = new RelayCommand(Launch, () => Stage == PlayStage.Ready && !IsGameRunning && !Activity.IsBusy);
        UpdateCommand = new RelayCommand(() => _ = InstallLatestAsync(), () => Latest != null && !Activity.IsBusy && !IsGameRunning);
        ChoosePackageCommand = new RelayCommand(ChoosePackage, () => !Activity.IsBusy && !IsGameRunning);
        OpenUrlCommand = new RelayCommand(p => ShellOpen.Url((string)p!));
        UseSteamCopyCommand = new RelayCommand(() => _ = HandleDropAsync(SteamCopy!), () => SteamCopy != null && !Activity.IsBusy);
    }

    public ActivityViewModel Activity => _main.Activity;
    private GameSetup Setup => _main.Setup;

    public ICommand BrowseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand ChoosePackageCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand UseSteamCopyCommand { get; }

    /// <summary>GameData of an installed Steam copy, found through Steam's library list.</summary>
    public string? SteamCopy
    {
        get => _steamCopy;
        private set
        {
            if (Set(ref _steamCopy, value)) Raise(nameof(HasSteamCopy));
        }
    }

    public bool HasSteamCopy => SteamCopy != null;

    public PlayStage Stage { get => _stage; private set => Set(ref _stage, value); }

    public string? SourcePath => _source;
    public string ReviewSize => _preview == null ? "" :
        $"{Sizes.Format(_preview.DataBytes)} to copy  ·  {Sizes.Format(_free)} free on {Path.GetPathRoot(Setup.Layout.Root)?.TrimEnd('\\')}";
    public bool HasMods => _preview?.ModCount > 0;
    public string ModsLabel => $"Bring my mods ({_preview?.ModCount}, {Sizes.Format(_preview?.ModBytes ?? 0)})";
    public bool ImportMods { get => _importMods; set => Set(ref _importMods, value); }

    public bool WantEssentials
    {
        get => Setup.State.EssentialMods;
        set
        {
            Setup.State.EssentialMods = value;
            Raise();
        }
    }

    public bool OffersEssentials => EssentialMods.All.Any(m => m.Available);

    public string EssentialsLabel => "Get the essential mods: " + string.Join(", ", EssentialMods.All.Where(m => m.Available).Select(m => m.Name));

    public string? InvalidMessage { get => _invalidMessage; private set => Set(ref _invalidMessage, value); }

    public bool CanResume => !Setup.State.ImportComplete && Setup.State.ImportedFrom != null && Directory.Exists(Setup.State.ImportedFrom);
    public string InstallHint => $"Installs to {Setup.Layout.Root}";

    public string VersionCaption => $"OPENSWBF2 {Setup.State.InstalledVersion}  ·  64-BIT";

    public ReleaseInfo? Latest
    {
        get => _latest;
        private set
        {
            if (!Set(ref _latest, value)) return;
            Raise(nameof(UpdateAvailable));
            Raise(nameof(UpdateText));
            Raise(nameof(DownloadLabel));
        }
    }

    public bool UpdateAvailable => Latest != null && Setup.State.InstalledVersion != null && ReleaseClient.IsNewer(Latest.Version, Setup.State.InstalledVersion);
    public string UpdateText => $"OpenSWBF2 {Latest?.Version} is out";
    public string DownloadLabel => Latest == null ? "Download" : $"Download {Latest.Version}";
    public string ReleaseNote { get => _releaseNote; private set => Set(ref _releaseNote, value); }

    public bool DragHover { get => _dragHover; set => Set(ref _dragHover, value); }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (Set(ref _isGameRunning, value)) Raise(nameof(PlayLabel));
        }
    }

    public string PlayLabel => IsGameRunning ? "RUNNING" : "PLAY";

    public IReadOnlyList<Prerequisite> MissingPrereqs { get => _missing; private set => Set(ref _missing, value); }

    public void Refresh(bool keepReview = true)
    {
        if (!(keepReview && Stage is PlayStage.Review or PlayStage.Invalid or PlayStage.Checking))
        {
            Stage = !Setup.State.ImportComplete ? PlayStage.Welcome : Setup.IsReady ? PlayStage.Ready : PlayStage.NeedsBuild;
            if (Stage != PlayStage.Review) _source = null;
        }
        MissingPrereqs = Prerequisites.Check().Where(p => !p.Present).ToList();
        if (_game == null) IsGameRunning = Setup.IsReady && GameLauncher.IsRunning(Setup.Layout, _main.Servers?.Control.ServerProcessId);
        if (Stage == PlayStage.Welcome && !_steamSearched)
        {
            _steamSearched = true;
            _ = FindSteamCopyAsync();
        }
        Raise(nameof(CanResume));
        Raise(nameof(InstallHint));
        Raise(nameof(VersionCaption));
        Raise(nameof(UpdateAvailable));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task FindSteamCopyAsync()
    {
        try { SteamCopy = (await Task.Run(SteamLocator.FindGameData)).FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task HandleDropAsync(string path)
    {
        if (Activity.IsBusy) return;
        _main.Page = Page.Play;
        if (StockLvls.IsGogInstaller(path))
        {
            ShowInvalid(StockLvls.GogNotYet);
            return;
        }
        string? gameData = GameLocator.FindGameData(path);
        if (gameData == null)
        {
            ShowInvalid("That doesn't look like Battlefront II. Drop the game's folder (the one with GameData inside), or GameData itself.");
            return;
        }
        if (GameLocator.IsInside(gameData, Setup.Layout.Root))
        {
            ShowInvalid("That is OpenBattlefront's own folder. Drop your original copy of the game.");
            return;
        }

        Stage = PlayStage.Checking;
        try
        {
            var validation = await Setup.ValidateAsync(gameData, CancellationToken.None);
            if (!validation.Ok)
            {
                ShowInvalid(GameSetup.Explain(validation));
                return;
            }
            _preview = await Task.Run(() => Importer.Preview(gameData));
            _free = Importer.FreeBytes(Setup.Layout.Root);
            _source = gameData;
            ImportMods = _preview.ModCount > 0;
            foreach (string name in new[] { nameof(SourcePath), nameof(ReviewSize), nameof(HasMods), nameof(ModsLabel) })
                Raise(name);
            Stage = PlayStage.Review;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowInvalid(e.Message);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void ShowInvalid(string message)
    {
        InvalidMessage = message;
        Stage = PlayStage.Invalid;
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose your Star Wars Battlefront II folder" };
        if (dialog.ShowDialog() == true) _ = HandleDropAsync(dialog.FolderName);
    }

    private async Task ImportAsync()
    {
        string source = _source!;
        var options = new ImportOptions(ImportMods);
        string? contentBefore = Setup.State.ContentVersion;
        var error = await Activity.RunAsync("Importing", async (progress, ct) =>
        {
            await Setup.ImportAsync(source, options, progress, ct);
            if (Setup.Installed != null) return;
            progress.Report(new StepUpdate("Looking for the latest OpenSWBF2"));
            try { Latest = await Setup.GetLatestReleaseAsync(ct); }
            catch (Exception e) when (e is HttpRequestException or JsonException) { }
            if (Latest != null) await Setup.DownloadAndInstallAsync(Latest, progress, ct);
        }, cancellable: true);

        Refresh(keepReview: false);
        bool addedContent = Setup.State.ContentVersion != contentBefore;
        if (error != null) _main.ShowError(error, "Import paused. Use Resume to carry on where it stopped.");
        else if (Setup.IsReady) _main.ShowNotice((addedContent ? GameContent.AddedNotice + " " : "") + "All set. Have fun out there.");
        else if (addedContent) _main.ShowNotice(GameContent.AddedNotice);
        if (Stage == PlayStage.NeedsBuild) await LoadLatestAsync();
        if (error == null) await _main.OfferEssentialsAsync();
    }

    public async Task LoadLatestAsync()
    {
        ReleaseNote = "Looking for the latest release…";
        try
        {
            Latest = await Setup.GetLatestReleaseAsync(CancellationToken.None);
            ReleaseNote = Latest == null
                ? "No OpenSWBF2 release is published yet. Choose a package file instead."
                : $"OpenSWBF2 {Latest.Version} is ready to download ({Sizes.Format(Latest.Size)}).";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            ReleaseNote = "Couldn't reach GitHub. Check your connection, or choose a package file.";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task CheckForUpdateAsync(bool silent)
    {
        try
        {
            Latest = await Setup.GetLatestReleaseAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (!silent) _main.ShowNotice("Couldn't check for updates: " + e.Message, error: true);
            return;
        }
        Raise(nameof(UpdateAvailable));
        if (!silent)
            _main.ShowNotice(Latest == null ? "No OpenSWBF2 release is published yet."
                : UpdateAvailable ? $"OpenSWBF2 {Latest.Version} is available." : "You're up to date.");
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task InstallLatestAsync()
    {
        var release = Latest;
        if (release == null) return;
        var error = await Activity.RunAsync($"Downloading OpenSWBF2 {release.Version}",
            (progress, ct) => Setup.DownloadAndInstallAsync(release, progress, ct), cancellable: true);
        AfterInstall(error);
    }

    public async Task InstallFromFileAsync(string zip)
    {
        var error = await Activity.RunAsync("Installing", (progress, ct) => Setup.InstallBuildAsync(zip, progress, ct));
        AfterInstall(error);
    }

    public async Task RollBackAsync()
    {
        var error = await Activity.RunAsync("Rolling back", (progress, ct) => Setup.RollBackAsync(progress, ct));
        AfterInstall(error);
    }

    private void AfterInstall(Exception? error)
    {
        Refresh(keepReview: false);
        _main.Settings.Refresh();
        if (error != null) _main.ShowError(error);
        else _main.ShowNotice(Setup.IsReady ? $"OpenSWBF2 {Setup.State.InstalledVersion} is installed." : $"OpenSWBF2 {Setup.State.InstalledVersion} is ready; import your game to play.");
        if (error == null) _ = _main.OfferEssentialsAsync();
    }

    public void ChoosePackage()
    {
        var dialog = new OpenFileDialog { Title = "Choose an OpenSWBF2 package", Filter = "OpenSWBF2 package (*.zip)|*.zip" };
        if (dialog.ShowDialog() == true) _ = InstallFromFileAsync(dialog.FileName);
    }

    private void Launch()
    {
        try
        {
            var game = GameLauncher.Start(Setup.Layout, Setup.State);
            _game = game;
            IsGameRunning = true;
            game.EnableRaisingEvents = true;
            game.Exited += (_, _) => Application.Current.Dispatcher.Invoke(() =>
            {
                _game = null;
                IsGameRunning = false;
                game.Dispose();
                _main.RequestRestore();
                CommandManager.InvalidateRequerySuggested();
            });
            if (Setup.State.MinimiseWhilePlaying) _main.RequestMinimise();
        }
        catch (Exception e)
        {
            _main.ShowError(e);
        }
    }
}
