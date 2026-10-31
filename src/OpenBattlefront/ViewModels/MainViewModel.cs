using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

public enum Page { Play, Mods, Servers, Settings }

public sealed class MainViewModel : ObservableObject
{
    private Page _page;
    private string? _notice;
    private bool _noticeIsError;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private TaskCompletionSource<bool>? _confirm;
    private string _confirmTitle = "";
    private string _confirmText = "";
    private string _confirmOk = "OK";
    private string _confirmCancel = "Cancel";
    private string? _confirmInput;

    public MainViewModel(GameSetup setup)
    {
        Setup = setup;
        Activity = new ActivityViewModel();
        Activity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityViewModel.IsBusy)) Raise(nameof(ShowFooterActivity));
        };
        Play = new PlayViewModel(this);
        Mods = new ModsViewModel(this);
        Settings = new SettingsViewModel(this);
        Servers = new ServersViewModel(this);

        DismissNoticeCommand = new RelayCommand(() => Notice = null);
        ConfirmYesCommand = new RelayCommand(() => CloseConfirm(true));
        ConfirmNoCommand = new RelayCommand(() => CloseConfirm(false));
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice = null;
        };
    }

    public static Version LauncherVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1, 0);
    public string LauncherVersionText => $"Launcher {LauncherVersion.ToString(3)}";

    public GameSetup Setup { get; }
    public ActivityViewModel Activity { get; }
    public PlayViewModel Play { get; }
    public ModsViewModel Mods { get; }
    public SettingsViewModel Settings { get; }
    public ServersViewModel Servers { get; }

    public event EventHandler? MinimiseRequested;
    public event EventHandler? RestoreRequested;
    public void RequestMinimise() => MinimiseRequested?.Invoke(this, EventArgs.Empty);
    public void RequestRestore() => RestoreRequested?.Invoke(this, EventArgs.Empty);

    public Page Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(CurrentView));
            Raise(nameof(ShowFooterActivity));
            if (value == Page.Mods) Mods.Refresh();
            if (value == Page.Settings) Settings.Refresh();
            if (value == Page.Play) Play.Refresh();
            if (value == Page.Servers) Servers.Refresh();
        }
    }

    public object CurrentView => Page switch
    {
        Page.Mods => Mods,
        Page.Servers => Servers,
        Page.Settings => Settings,
        _ => Play,
    };

    /// <summary>The slim progress strip shown on the other pages while a job runs.</summary>
    public bool ShowFooterActivity => Activity.IsBusy && Page != Page.Play;

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value)) Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice != null;
    public bool NoticeIsError { get => _noticeIsError; private set => Set(ref _noticeIsError, value); }
    public ICommand DismissNoticeCommand { get; }

    public void ShowNotice(string text, bool error = false)
    {
        _noticeTimer.Stop();
        NoticeIsError = error;
        Notice = text;
        if (!error) _noticeTimer.Start();
    }

    public void ShowError(Exception e, string? cancelled = null)
    {
        if (e is OperationCanceledException) ShowNotice(cancelled ?? "Cancelled.");
        else ShowNotice(e.Message, error: true);
    }

    public bool ConfirmOpen => _confirm != null;
    public string ConfirmTitle { get => _confirmTitle; private set => Set(ref _confirmTitle, value); }
    public string ConfirmText { get => _confirmText; private set => Set(ref _confirmText, value); }
    public string ConfirmOk { get => _confirmOk; private set => Set(ref _confirmOk, value); }
    public string ConfirmCancel { get => _confirmCancel; private set => Set(ref _confirmCancel, value); }
    public ICommand ConfirmYesCommand { get; }
    public ICommand ConfirmNoCommand { get; }

    public Task<bool> ConfirmAsync(string title, string text, string ok, string cancel = "Cancel")
    {
        _confirm?.TrySetResult(false);
        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmOk = ok;
        ConfirmCancel = cancel;
        _confirm = new TaskCompletionSource<bool>();
        Raise(nameof(ConfirmOpen));
        return _confirm.Task;
    }

    /// <summary>The dialog's text box, shown while AskAsync waits; null hides it.</summary>
    public string? ConfirmInput
    {
        get => _confirmInput;
        set
        {
            if (Set(ref _confirmInput, value)) Raise(nameof(ConfirmHasInput));
        }
    }

    public bool ConfirmHasInput => ConfirmInput != null;

    /// <summary>The dialog with a text box: what was typed, or null when cancelled.</summary>
    public async Task<string?> AskAsync(string title, string text, string initial, string ok, string cancel = "Cancel")
    {
        ConfirmInput = initial;
        bool yes = await ConfirmAsync(title, text, ok, cancel);
        string? answer = ConfirmInput;
        ConfirmInput = null;
        return yes ? answer : null;
    }

    private void CloseConfirm(bool result)
    {
        var confirm = _confirm;
        _confirm = null;
        Raise(nameof(ConfirmOpen));
        confirm?.TrySetResult(result);
    }

    /// <summary>Opens the in-app download window for a catalog mod and returns the archive it
    /// caught, or null. Set by the window layer.</summary>
    public Func<CatalogMod, string?>? DownloadMod { get; set; }

    public async Task GetEssentialAsync(CatalogMod mod)
    {
        if (DownloadMod == null || Activity.IsBusy) return;
        string? file = DownloadMod(mod);
        if (file == null) return;
        try
        {
            var added = await Mods.InstallAsync(new[] { file });
            if (added.Count > 0)
            {
                Setup.State.EssentialFolders[mod.Id] = added.ToArray();
                Setup.Save();
                Mods.Refresh();
                ShowNotice($"{mod.Name} is installed. It shows up in Instant Action.");
            }
        }
        finally
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    /// <summary>Right after the first setup, fetch the Essential mods the player kept ticked.
    /// It happens once; after that they are on the Mods page.</summary>
    public async Task OfferEssentialsAsync()
    {
        var state = Setup.State;
        if (!state.EssentialMods || state.EssentialsOffered || !Setup.IsReady) return;
        state.EssentialsOffered = true;
        Setup.Save();
        foreach (var mod in EssentialMods.Missing(Setup.Layout, state.EssentialFolders).ToList())
            await GetEssentialAsync(mod);
    }

    public async Task StartAsync()
    {
        DisablePatch();
        Play.Refresh();
        if (!Directory.Exists(Setup.Content.Dir) && GameContent.StartedFromTemp(AppContext.BaseDirectory, Path.GetTempPath()))
            ShowNotice(GameContent.UnzipFirst, error: true);
        await InstallContentAsync();
        if (Setup.IsReady && Setup.State.CheckUpdatesOnStart) await Play.CheckForUpdateAsync(silent: true);
        else if (Setup.State.ImportComplete && !Setup.IsReady) await Play.LoadLatestAsync();
        await Servers.Control.ReattachAsync();
    }

    /// <summary>Closing while hosting: stop the server too, or leave it running for the next
    /// launch to attach to. Returns once the window may close.</summary>
    public async Task BeforeCloseAsync()
    {
        var control = Servers.Control;
        if (!control.IsRunning) return;
        bool stop = await ConfirmAsync("Stop the server too?",
            "It is still hosting. Keep it running and the launcher finds it again next time.", "Stop server", "Keep running");
        if (stop) await control.StopNowAsync();
        else await control.DetachAsync();
    }

    /// <summary>The Conversion Pack and the Xbox DLC come with the launcher: put them in the game
    /// copy once it is imported, and again when a launcher brings a new version of them.</summary>
    private async Task InstallContentAsync()
    {
        if (!Setup.NeedsContent) return;
        bool added = false;
        var error = await Activity.RunAsync("Adding the included content",
            async (progress, ct) => added = await Setup.InstallContentAsync(progress, ct), cancellable: true);
        if (error != null) ShowError(error);
        else if (added) ShowNotice(GameContent.AddedNotice);
        Mods.Refresh();
    }

    /// <summary>An older import brought the unofficial 1.3 patch along; switch it off once.</summary>
    private void DisablePatch()
    {
        if (!Setup.State.ImportComplete) return;
        try
        {
            if (ScriptMods.DisablePatch(Setup.Layout).Count > 0)
                ShowNotice("Switched off the unofficial 1.3 patch: OpenSWBF2 has 1.3-style mod support built in, so mods made for it don't need it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError(e);
        }
    }
}

