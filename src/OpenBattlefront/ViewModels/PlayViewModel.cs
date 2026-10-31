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
    private IReadOnlyList<FoundCopy> _foundCopies = Array.Empty<FoundCopy>();
    private bool _searched;
    private bool _isSearching;

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
        UseCopyCommand = new RelayCommand(p => _ = HandleDropAsync(((FoundCopy)p!).GameData), p => p is FoundCopy && !Activity.IsBusy);
        SearchDrivesCommand = new RelayCommand(() => _ = FindCopiesAsync(), () => !IsSearching && !Activity.IsBusy);
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
    public ICommand UseCopyCommand { get; }
    public ICommand SearchDrivesCommand { get; }

    /// <summary>Copies of the game found on this PC (Steam libraries, GOG, retail, and the usual
    /// folders on every drive), most likely first.</summary>
    public IReadOnlyList<FoundCopy> FoundCopies
    {
        get => _foundCopies;
        private set
        {
            if (Set(ref _foundCopies, value)) Raise(nameof(SearchStatus));
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (Set(ref _isSearching, value)) Raise(nameof(SearchStatus));
        }
    }

    public string SearchStatus =>
        IsSearching ? "Looking for your game on this PC…" :
        !_searched ? "" :
        FoundCopies.Count == 0 ? "No copy found on your drives. Drop the folder above, or Browse." :
        FoundCopies.Count == 1 ? "Found 1 copy on this PC" : $"Found {FoundCopies.Count} copies on this PC";

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
        if (Stage == PlayStage.Welcome && !_searched && !IsSearching)
        {
            _ = FindCopiesAsync();
        }
        Raise(nameof(CanResume));
        Raise(nameof(InstallHint));
        Raise(nameof(VersionCaption));
        Raise(nameof(UpdateAvailable));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The registry's copies show at once; the drive search (a few seconds per drive)
    /// adds its finds after.</summary>
    private async Task FindCopiesAsync()
    {
        IsSearching = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var finder = GameFinder.ForThisPc();
            var registered = await Task.Run(() => finder.FindRegistered());
            FoundCopies = Outside(registered);
            var onDrives = await Task.Run(() => finder.ScanDrives());
            FoundCopies = Outside(GameFinder.Merge(registered, onDrives));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        finally
        {
            _searched = true;
            IsSearching = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Leaves out OpenBattlefront's own copy of the game.</summary>
    private IReadOnlyList<FoundCopy> Outside(IReadOnlyList<FoundCopy> copies) =>
        copies.Where(c => !GameLocator.IsInside(c.GameData, Setup.Layout.Root)).ToList();

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
        if (gameData == null && Directory.Exists(path))
        {
            if (GameFinder.HasGogInstaller(path))
            {
                ShowInvalid(StockLvls.GogNotYet);
                return;
            }
            // A folder that holds the game somewhere below it: X:\Games, steamapps\common …
            Stage = PlayStage.Checking;
            gameData = await Task.Run(() => GameFinder.SearchBelow(path, 3, TimeSpan.FromSeconds(6)));
        }
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

    /// <summary>Before the first start: the player's newest profile, or a name they choose for the game
    /// to make one with. False when they cancel.</summary>
    private async Task<bool> ChoosePlayerNameAsync()
    {
        string? name = PlayerProfiles.List(Setup.Layout).FirstOrDefault();
        string suggestion = PlayerProfiles.DefaultName(Setup.Layout, Environment.UserName);
        while (name == null)
        {
            string? answer = await _main.AskAsync("Choose your player name",
                $"Your profile in the game, and your name online. {PlayerProfiles.MinLength} to {PlayerProfiles.MaxLength} " +
                "letters or numbers; Settings can change it later.", suggestion, "Play");
            if (answer == null) return false;
            answer = answer.Trim();
            if (PlayerProfiles.CanCreate(answer)) name = answer;
            else
            {
                suggestion = answer;
                _main.ShowNotice($"A player name is {PlayerProfiles.MinLength} to {PlayerProfiles.MaxLength} letters or numbers.", error: true);
            }
        }
        Setup.State.PlayerName = name;
        Setup.Save();
        _main.Settings.Refresh();
        return true;
    }

    private async void Launch()
    {
        try
        {
            if (Setup.State.PlayerName == null && !await ChoosePlayerNameAsync()) return;
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
