using System.Windows.Input;
using Microsoft.Win32;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Func<bool> idle = () => !_main.Activity.IsBusy;
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(), idle);
        InstallFromFileCommand = new RelayCommand(() => _main.Play.ChoosePackage(), idle);
        RollBackCommand = new RelayCommand(() => _ = _main.Play.RollBackAsync(), () => idle() && CanRollBack);
        OpenInstallFolderCommand = new RelayCommand(() => ShellOpen.Folder(Layout.Root, create: true));
        ChangeFolderCommand = new RelayCommand(ChangeFolder, () => idle() && CanChangeFolder);
        ReimportCommand = new RelayCommand(Reimport, () => idle() && State.ImportedFrom != null);
        VerifyCommand = new RelayCommand(() => _ = VerifyAsync(), () => idle() && State.InstalledVersion != null);
        RebuildCommand = new RelayCommand(() => _ = RebuildAsync(), () => idle() && State.InstalledVersion != null && State.ImportComplete);
        OpenSavesCommand = new RelayCommand(() => ShellOpen.Folder(Layout.SaveGames));
        OpenModsCommand = new RelayCommand(() => ShellOpen.Folder(Layout.AddOn));
        OpenCrashesCommand = new RelayCommand(() => ShellOpen.Folder(InstallLayout.CrashReports));
        OpenLogsCommand = new RelayCommand(() => ShellOpen.Folder(Layout.Logs));
        OpenUrlCommand = new RelayCommand(p => ShellOpen.Url((string)p!));
    }

    private LauncherState State => _main.Setup.State;
    private InstallLayout Layout => _main.Setup.Layout;

    public string LauncherVersion => MainViewModel.LauncherVersion.ToString(3);
    public string InstalledBuild => State.InstalledVersion == null ? "Not installed yet" : $"OpenSWBF2 {State.InstalledVersion}";
    public bool CanRollBack => _main.Setup.CanRollBack;
    public string RollBackLabel => $"Roll back to {State.PreviousVersion}";
    public string InstallFolder => Layout.Root;
    public bool CanChangeFolder => !State.ImportComplete && State.ImportedFrom == null;
    public string ImportedFrom => State.ImportedFrom ?? "Nothing imported yet";

    public string Renderer
    {
        get => State.Renderer;
        set
        {
            State.Renderer = value;
            Save();
        }
    }

    public bool Borderless
    {
        get => State.Borderless;
        set
        {
            State.Borderless = value;
            Save();
        }
    }

    public string ExtraArgs
    {
        get => State.ExtraArgs;
        set
        {
            State.ExtraArgs = value.Trim();
            Save();
        }
    }

    public bool MinimiseWhilePlaying
    {
        get => State.MinimiseWhilePlaying;
        set
        {
            State.MinimiseWhilePlaying = value;
            Save();
        }
    }

    public bool CheckUpdatesOnStart
    {
        get => State.CheckUpdatesOnStart;
        set
        {
            State.CheckUpdatesOnStart = value;
            Save();
        }
    }

    public ICommand CheckUpdatesCommand { get; }
    public ICommand InstallFromFileCommand { get; }
    public ICommand RollBackCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand ChangeFolderCommand { get; }
    public ICommand ReimportCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RebuildCommand { get; }
    public ICommand OpenSavesCommand { get; }
    public ICommand OpenModsCommand { get; }
    public ICommand OpenCrashesCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenUrlCommand { get; }

    public void Refresh()
    {
        foreach (string name in new[]
        {
            nameof(InstalledBuild), nameof(CanRollBack), nameof(RollBackLabel), nameof(InstallFolder), nameof(CanChangeFolder),
            nameof(ImportedFrom), nameof(Renderer), nameof(Borderless), nameof(ExtraArgs), nameof(MinimiseWhilePlaying), nameof(CheckUpdatesOnStart),
        })
            Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private void Save()
    {
        _main.Setup.Save();
        Refresh();
    }

    private async Task CheckUpdatesAsync()
    {
        await _main.Play.CheckForUpdateAsync(silent: State.InstalledVersion == null);
        var latest = _main.Play.Latest;
        if (latest == null) return;
        bool offer = State.InstalledVersion == null || _main.Play.UpdateAvailable;
        if (offer && await _main.ConfirmAsync($"Install OpenSWBF2 {latest.Version}?",
                $"Downloads {Sizes.Format(latest.Size)} from GitHub. The current build is kept so you can roll back.", "Install"))
            await _main.Play.InstallLatestAsync();
    }

    private void ChangeFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Where should OpenBattlefront keep the game? (about 10 GB)" };
        if (dialog.ShowDialog() != true) return;
        string root = Path.Combine(dialog.FolderName, "OpenBattlefront");
        if (GameLocator.IsInside(root, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) ||
            GameLocator.IsInside(root, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)))
        {
            _main.ShowNotice("Pick a folder outside Program Files, which needs admin rights to write.", error: true);
            return;
        }
        State.InstallRoot = root;
        Save();
        _main.Play.Refresh();
        _main.ShowNotice($"The game will install to {root}.");
    }

    private void Reimport()
    {
        _main.Page = Page.Play;
        _ = _main.Play.HandleDropAsync(State.ImportedFrom!);
    }

    private async Task VerifyAsync()
    {
        IReadOnlyList<string> problems = Array.Empty<string>();
        var error = await _main.Activity.RunAsync("Verifying game files", async (progress, ct) =>
        {
            progress.Report(new StepUpdate("Verifying game files", "SHA-256 of the installed build"));
            problems = await Task.Run(() => _main.Setup.Verify(), ct);
        });
        if (error != null)
        {
            _main.ShowError(error);
            return;
        }
        if (problems.Count == 0)
        {
            _main.ShowNotice("Every game file checks out.");
            return;
        }
        if (await _main.ConfirmAsync("Some files need repair", string.Join(Environment.NewLine, problems.Take(6)) +
                (problems.Count > 6 ? $"{Environment.NewLine}and {problems.Count - 6} more" : ""), "Repair"))
            await RepairAsync();
    }

    private async Task RepairAsync()
    {
        var manifest = _main.Setup.Installed;
        if (manifest == null) return;
        string zipless = _main.Setup.Builds.Dir(manifest.Version);
        var error = await _main.Activity.RunAsync("Repairing", async (progress, ct) =>
        {
            if (manifest.Verify(zipless).Count > 0)
                throw new InvalidDataException("The stored build is damaged too. Use Check for updates or Install from file to get a fresh copy.");
            progress.Report(new StepUpdate("Repairing", "Copying the game files back"));
            await Task.Run(() => _main.Setup.Builds.Deploy(manifest), ct);
            await _main.Setup.RebuildLvlsAsync(progress, ct);
        });
        if (error != null) _main.ShowError(error);
        else _main.ShowNotice("Repaired.");
        _main.Play.Refresh(keepReview: false);
    }

    private async Task RebuildAsync()
    {
        var error = await _main.Activity.RunAsync("Building core game files", (progress, ct) => _main.Setup.RebuildLvlsAsync(progress, ct));
        if (error != null) _main.ShowError(error);
        else _main.ShowNotice("Core game files rebuilt.");
        _main.Play.Refresh(keepReview: false);
    }
}
