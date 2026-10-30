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
        Servers = new ServersViewModel();

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
    public ICommand ConfirmYesCommand { get; }
    public ICommand ConfirmNoCommand { get; }

    public Task<bool> ConfirmAsync(string title, string text, string ok)
    {
        _confirm?.TrySetResult(false);
        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmOk = ok;
        _confirm = new TaskCompletionSource<bool>();
        Raise(nameof(ConfirmOpen));
        return _confirm.Task;
    }

    private void CloseConfirm(bool result)
    {
        var confirm = _confirm;
        _confirm = null;
        Raise(nameof(ConfirmOpen));
        confirm?.TrySetResult(result);
    }

    public async Task StartAsync()
    {
        Play.Refresh();
        if (Setup.IsReady && Setup.State.CheckUpdatesOnStart) await Play.CheckForUpdateAsync(silent: true);
        else if (Setup.State.ImportComplete && !Setup.IsReady) await Play.LoadLatestAsync();
    }
}

public sealed class ServersViewModel { }
