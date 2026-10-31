using System.Text;
using System.Windows.Input;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

public enum MultiplayerScreen { Connect, Lobby }

/// <summary>A server in the recent list.</summary>
public sealed class RecentServerItem
{
    public RecentServerItem(RecentServer server)
    {
        Server = server;
    }

    public RecentServer Server { get; }
    public string Title => string.IsNullOrWhiteSpace(Server.Name) ? Server.Address : Server.Name;
    public string Detail => string.IsNullOrWhiteSpace(Server.Name) ? $"Joined {When}" : $"{Server.Address}  ·  joined {When}";

    private string When
    {
        get
        {
            var days = (DateTime.UtcNow.Date - Server.LastJoined.ToUniversalTime().Date).Days;
            return days <= 0 ? "today" : days == 1 ? "yesterday" : days < 30 ? $"{days} days ago" : Server.LastJoined.ToLocalTime().ToString("d MMM yyyy");
        }
    }
}

/// <summary>One player in the lobby list: waiting in a launcher, downloading, or already in the game.</summary>
public sealed record LobbyRow(string Name, string Detail, bool IsMe, bool InGame);

/// <summary>The Multiplayer page. Direct Connect (an address, its password, recent servers, as the
/// game's own tab has them), then the server's mods (asked once, downloaded, checked, installed),
/// then its lobby: the player waits here with the game closed, and the game opens on the server once
/// the match starts. A server without the lobby is joined at once.</summary>
public sealed class MultiplayerViewModel : ObservableObject
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    private const int MaxFailures = 15;

    private readonly MainViewModel _main;
    private readonly HttpClient _http = ModService.CreateHttp(MainViewModel.LauncherVersion);
    private MultiplayerScreen _screen;
    private string _address = "";
    private string _password = "";
    private IReadOnlyList<RecentServerItem> _recent = Array.Empty<RecentServerItem>();
    private LobbyClient? _lobby;
    private JoinTarget? _target;
    private string _myName = "";
    private CancellationTokenSource? _poll;
    private bool _downloading;
    private int _progress;
    private bool _launching;
    private MapCatalog? _catalog;
    private string _serverName = "";
    private string _mapText = "";
    private string _stateText = "";
    private string _countText = "";
    private bool _isLoading;
    private IReadOnlyList<LobbyRow> _players = Array.Empty<LobbyRow>();

    public MultiplayerViewModel(MainViewModel main)
    {
        _main = main;
        JoinCommand = new RelayCommand(() => _ = JoinAsync(), () => CanJoin && !string.IsNullOrWhiteSpace(Address));
        PickCommand = new RelayCommand(p =>
        {
            if (p is not RecentServerItem item) return;
            Address = item.Server.Address;
            Password = item.Server.JoinPassword;
        });
        ForgetCommand = new RelayCommand(p =>
        {
            if (p is not RecentServerItem item) return;
            State.RecentServers.Remove(item.Server);
            _main.Setup.Save();
            LoadRecent();
        });
        LeaveCommand = new RelayCommand(() => _ = LeaveAsync());
    }

    private LauncherState State => _main.Setup.State;
    public ActivityViewModel Activity => _main.Activity;

    public ICommand JoinCommand { get; }
    public ICommand PickCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand LeaveCommand { get; }

    public MultiplayerScreen Screen { get => _screen; private set => Set(ref _screen, value); }

    public string Address { get => _address; set => Set(ref _address, value); }

    /// <summary>The join password, if the server has one.</summary>
    public string Password { get => _password; set => Set(ref _password, value ?? ""); }

    public IReadOnlyList<RecentServerItem> Recent { get => _recent; private set => Set(ref _recent, value); }
    public bool HasRecent => Recent.Count > 0;
    public bool CanJoin => _main.Setup.IsReady && !Activity.IsBusy && !_main.Play.IsGameRunning && Screen == MultiplayerScreen.Connect;
    public string ReadyHint => _main.Setup.IsReady ? "" : "Set the game up on the Play page first.";

    // The lobby

    public string ServerName { get => _serverName; private set => Set(ref _serverName, value); }
    public string MapText { get => _mapText; private set => Set(ref _mapText, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public IReadOnlyList<LobbyRow> Players { get => _players; private set => Set(ref _players, value); }

    public bool IsDownloading
    {
        get => _downloading;
        private set => Set(ref _downloading, value);
    }

    public void Refresh()
    {
        LoadRecent();
        if (string.IsNullOrWhiteSpace(Address) && Recent.Count > 0)
        {
            Address = Recent[0].Server.Address;
            Password = Recent[0].Server.JoinPassword;
        }
        Raise(nameof(CanJoin));
        Raise(nameof(ReadyHint));
    }

    private void LoadRecent()
    {
        Recent = State.RecentServers.Select(s => new RecentServerItem(s)).ToList();
        Raise(nameof(HasRecent));
    }

    public async Task JoinAsync()
    {
        if (!ServerAddress.TryParse(Address, out var server))
        {
            _main.ShowNotice("That isn't a server address. Type it as address:port, such as 203.0.113.5:3658 or myserver.example.com.", error: true);
            return;
        }
        // A server shows players by name: the lobby's and the game's (-name) are the same one
        if (!await _main.Play.EnsurePlayerNameAsync(forJoin: true)) return;
        _myName = State.PlayerName!;
        string? password = string.IsNullOrWhiteSpace(Password)
            ? State.RecentServers.FirstOrDefault(s => string.Equals(s.Address, server.ToString(), StringComparison.OrdinalIgnoreCase))?.JoinPassword
            : Password.Trim();
        if (string.IsNullOrEmpty(password)) password = null;
        string title = $"Joining {server}";

        // The server's mod list, asking for the join password when it has one
        ModServiceReply reply = new(ModServiceStatus.Unavailable);
        while (true)
        {
            string? tried = password;
            string? worked = tried;
            var error = await Activity.RunAsync(title, async (progress, ct) =>
            {
                progress.Report(new StepUpdate(title, "Asking the server for its mods"));
                (reply, worked) = await ModService.GetManifestOrOpenAsync(_http, server, tried, ct);
            }, cancellable: true);
            if (error != null)
            {
                _main.ShowError(error, "Not joined.");
                return;
            }
            if (reply.Status != ModServiceStatus.NeedsPassword)
            {
                // A server that dropped its password: join without the old one, and forget it
                password = worked;
                break;
            }
            string? answer = await _main.AskAsync(password == null ? "This server has a password" : "Wrong password",
                "Type the server's join password. The launcher keeps it with the server in your recent list.", "", "Join");
            if (string.IsNullOrWhiteSpace(answer)) return;
            password = answer.Trim();
        }
        Password = password ?? "";
        var target = new JoinTarget(server, password);

        if (reply.Status == ModServiceStatus.Unavailable)
        {
            _main.ShowNotice($"{server} doesn't share its mods, so you join with the ones you have.");
            Remember(server, null, password);
            await _main.Play.LaunchAsync(target);
            return;
        }

        var manifest = reply.Manifest!;
        var plan = await PlanAsync(server, manifest);
        if (plan == null) return;
        string name = string.IsNullOrWhiteSpace(manifest.Server) ? server.ToString() : manifest.Server;
        if (!plan.IsEmpty && !await _main.ConfirmAsync("Get the server's mods?", Describe(name, plan),
                plan.Bytes > 0 ? $"Download {Sizes.Format(plan.Bytes)} and join" : "Switch on and join"))
            return;
        Remember(server, manifest.Server, password);

        // The lobby, for a service that has one
        var lobby = new LobbyClient(_http, server, password);
        bool hasLobby;
        try { hasLobby = await lobby.JoinAsync(_myName, CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or ModServiceException)
        {
            _main.ShowError(e);
            return;
        }
        if (!hasLobby)
        {
            if (await DownloadAsync(server, password, plan, null)) await _main.Play.LaunchAsync(target);
            return;
        }

        _lobby = lobby;
        _target = target;
        _launching = false;
        _catalog = new MapCatalogStore(_main.Setup.Layout, State.InstalledVersion).TryLoadCached();
        ServerName = name;
        MapText = "";
        StateText = "Joining the lobby…";
        Players = Array.Empty<LobbyRow>();
        Screen = MultiplayerScreen.Lobby;
        _poll = new CancellationTokenSource();
        _ = PollAsync(_poll.Token);

        if (!plan.IsEmpty && !await DownloadAsync(server, password, plan, lobby))
        {
            await LeaveAsync();
            return;
        }
        try { await lobby.UpdateAsync(downloading: false, 100, CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or ModServiceException) { }
    }

    private async Task<ModSyncPlan?> PlanAsync(ServerAddress server, ServerModsManifest manifest)
    {
        string name = string.IsNullOrWhiteSpace(manifest.Server) ? server.ToString() : manifest.Server;
        string? mine = State.InstalledVersion;
        if (!string.IsNullOrEmpty(manifest.Openswbf2) && mine != null && manifest.Openswbf2 != mine &&
            !await _main.ConfirmAsync("Another OpenSWBF2 version",
                $"{name} runs OpenSWBF2 {manifest.Openswbf2} and you have {mine}. Players need the same version to play together: " +
                "update on the Play page first, or ask the server's admin to.", "Join anyway"))
            return null;

        ModSyncPlan? plan = null;
        var hashes = FileHashCache.For(_main.Setup.Layout);
        var error = await Activity.RunAsync($"Joining {server}", async (progress, ct) =>
        {
            progress.Report(new StepUpdate($"Joining {server}", "Comparing the server's mods with yours"));
            plan = await Task.Run(() => ModSyncPlan.Create(manifest, _main.Setup.Layout, hashes), ct);
        });
        if (error != null)
        {
            _main.ShowError(error);
            return null;
        }
        return plan;
    }

    /// <summary>Gets the mods; in the lobby, the others see how far along it is.</summary>
    private async Task<bool> DownloadAsync(ServerAddress server, string? password, ModSyncPlan plan, LobbyClient? lobby)
    {
        var client = new ModSyncClient(_http, _main.Setup.Layout);
        IsDownloading = true;
        _progress = 0;
        DateTime told = DateTime.MinValue;
        var error = await Activity.RunAsync(ModSyncClient.Title, (progress, ct) =>
        {
            var tee = new Progress<StepUpdate>(u =>
            {
                progress.Report(u);
                if (u.Fraction is not { } f) return;
                _progress = (int)(f * 100);
                if (lobby != null && DateTime.UtcNow - told > TimeSpan.FromSeconds(2))
                {
                    told = DateTime.UtcNow;
                    _ = lobby.UpdateAsync(downloading: true, _progress, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            });
            return client.InstallAsync(server, password, plan, tee, ct);
        }, cancellable: true);
        IsDownloading = false;
        _main.Mods.Refresh();
        if (error != null)
        {
            _main.ShowError(error, "Stopped. Join again to pick up the download where it stopped.");
            return false;
        }
        return true;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested && _lobby is { } lobby)
        {
            try
            {
                var status = await lobby.StatusAsync(ct);
                failures = 0;
                if (!status.InLobby && !_launching)
                {
                    // The entry went (the PC slept, say): come back in
                    await lobby.JoinAsync(_myName, ct);
                    await lobby.UpdateAsync(IsDownloading, _progress, ct);
                }
                Show(status);
                if (!_launching && LobbyRules.ShouldLaunch(status, IsDownloading))
                {
                    await LaunchFromLobbyAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or ModServiceException or InvalidDataException or OperationCanceledException)
            {
                if (++failures >= MaxFailures)
                {
                    _main.ShowNotice($"Lost {ServerName}: {e.Message}", error: true);
                    await LeaveAsync();
                    return;
                }
                StateText = "Can't reach the server; trying again…";
            }
            try { await Task.Delay(PollEvery, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void Show(MatchStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.Server)) ServerName = status.Server;
        MapText = string.IsNullOrEmpty(status.Map) ? "" :
            MapTitle(status.Map) + (string.IsNullOrEmpty(status.Next) ? "" : $"   ·   next: {MapTitle(status.Next)}");
        IsLoading = status.IsLoading;
        StateText = status.Phase switch
        {
            "lobby" when status.IsLoading => $"Starting: everyone's game is opening{(status.StartIn is { } s ? $" ({s} s)" : "")}",
            "lobby" => "Waiting for the admin to start the match",
            "warmup" or "playing" => "Match in progress: joining",
            "ended" => "The match is over; the next map is loading",
            _ => "The server's game isn't answering yet",
        };
        if (IsDownloading && status.Phase is "lobby" or "warmup" or "playing")
            StateText += ". You join once your mods are in.";

        var rows = status.Lobby.Select(p => new LobbyRow(p.Name, p.IsDownloading ? $"Downloading {p.Progress}%" : "In the lobby",
                string.Equals(p.Name, _myName, StringComparison.Ordinal), false))
            .Concat(status.InGame.Select(n => new LobbyRow(n, "In the game", false, true)))
            .ToList();
        Players = rows;
        CountText = $"{status.Lobby.Count} in the lobby   ·   {status.Players} in the game";
    }

    private string MapTitle(string mission) =>
        _catalog?.Find(mission) is { } c ? $"{c.MapName} · {c.EraName} · {c.ModeName}" : mission;

    private async Task LaunchFromLobbyAsync()
    {
        _launching = true;
        StateText = "Opening the game…";
        var target = _target;
        bool started = !_main.Play.IsGameRunning && target != null && await _main.Play.LaunchAsync(target);
        if (!started && _main.Play.IsGameRunning) _main.ShowNotice("The game is already open: join from its Multiplayer > Direct Connect.", error: true);
        await LeaveAsync();
    }

    /// <summary>Out of the lobby, back to Direct Connect; a running download stops.</summary>
    public async Task LeaveAsync()
    {
        _poll?.Cancel();
        _poll = null;
        if (IsDownloading && Activity.CancelCommand.CanExecute(null)) Activity.CancelCommand.Execute(null);
        var lobby = _lobby;
        _lobby = null;
        Screen = MultiplayerScreen.Connect;
        Raise(nameof(CanJoin));
        CommandManager.InvalidateRequerySuggested();
        if (lobby != null) await lobby.LeaveAsync(CancellationToken.None);
    }

    private void Remember(ServerAddress server, string? name, string? password)
    {
        State.RememberServer(server, name, password);
        _main.Setup.Save();
        LoadRecent();
    }

    public static string Describe(string server, ModSyncPlan plan)
    {
        var text = new StringBuilder($"{server} uses mods you don't have yet:\n");
        foreach (var item in plan.Needed)
        {
            string what = item.Action switch
            {
                ModSyncAction.New => $"{Sizes.Format(item.Bytes)}, new",
                ModSyncAction.Update => $"{Sizes.Format(item.Bytes)}, an update of yours",
                _ => "yours, switched off: it is switched on",
            };
            text.Append($"\n  •  {item.Name}: {what}");
        }
        text.Append("\n\nMods can run scripts in the game, so only get them from servers you trust.");
        return text.ToString();
    }

    /// <summary>For screenshots: a lobby with players waiting and one still downloading.</summary>
    internal void ShowSample()
    {
        _myName = "Pat";
        ServerName = "Friday Night Battles";
        Screen = MultiplayerScreen.Lobby;
        Show(new MatchStatus
        {
            Schema = 1, Server = "Friday Night Battles", Phase = "lobby", Map = "BS1g_con", Next = "geo1c_con", Players = 1,
            InGame = { "Rex" },
            Lobby = { new LobbyPlayer("Pat", "waiting", 100), new LobbyPlayer("Cody", "waiting", 100), new LobbyPlayer("Echo", "downloading", 45) },
        });
    }

    internal void ShowConnect() => Screen = MultiplayerScreen.Connect;
}
