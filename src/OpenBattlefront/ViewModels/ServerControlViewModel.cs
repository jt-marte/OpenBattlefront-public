using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OpenBattlefront.Core;
using OpenBattlefront.Core.Hosting;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.ViewModels;

public enum ServerStatus { NotRunning, Starting, Loading, Lobby, Warmup, Playing, Results, Stopping, NotAnswering }
public enum ControlTab { Playlist, Players, Chat, Options, Mods }
public enum CatalogStatus { Idle, Loading, Ready, NeedsUpdate, NotInstalled, Failed }
public enum BattleKind { Ground, Space }

/// <summary>An entry in the era or mode filter; a null id is "all".</summary>
public sealed record FilterChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The playlist and control screen: the map catalog, the playlist, the server's status and
/// match controls, players, chat and the options that apply from the next map. Hosting starts
/// the server here. Connect to server as admin uses it too, for a server elsewhere (remote
/// mode): rcon on its game port, and its mod service for the launcher lobby, its own map list
/// and keeping what the admin applies.
/// </summary>
public sealed class ServerControlViewModel : ObservableObject
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long a server elsewhere may be silent before it shows as not answering (its
    /// game restarts, say). One missed poll takes rcon's retries, about 6 s.</summary>
    private static readonly TimeSpan NotAnsweringAfter = TimeSpan.FromSeconds(15);

    private readonly MainViewModel _main;
    private readonly ServersViewModel _servers;
    private readonly RemoteAdminViewModel? _remote;
    private readonly ServerHost _host;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private IServerAdmin? _admin;
    private ModHostService? _mods;
    private ServerProfile _remoteProfile = new();
    private ModUploadClient? _remoteMods;
    private ServerAddress? _remoteAddress;
    private string _remotePassword = "";
    private int _unsupportedPolls;
    private bool _keepsSettings;
    private bool _remoteLobby;
    private bool _joining;
    private AdminSettingsReply? _remoteSettings;
    private MatchStatus? _match;
    private DateTime _lastAnswer;
    private string _catalogNote = "";
    private MapCatalog? _catalog;
    private CatalogStatus _catalogStatus;
    private string _catalogMessage = "";
    private string _search = "";
    private bool _showStock = true;
    private bool _showMods = true;
    private BattleKind _kind;
    private static readonly FilterChoice AllEras = new(null, "All eras");
    private static readonly FilterChoice AllModes = new(null, "All modes");
    private FilterChoice _era = AllEras;
    private FilterChoice _mode = AllModes;
    private IReadOnlyList<FilterChoice> _eraChoices = new[] { AllEras };
    private IReadOnlyList<FilterChoice> _modeChoices = new[] { AllModes };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ServerStatus _status;
    private ServerState? _state;
    private ControlTab _tab;
    private bool _playlistDirty;
    private bool _playlistDragHover;
    private PlaylistItemViewModel? _selectedEntry;
    private string _sayText = "";
    private bool _showServerLog = true;
    private bool _polling;
    private int _pollCount;
    private bool _stopRequested;
    private bool _quietExit;
    private bool _busy;

    /// <param name="remote">Connect to server as admin: the screen for a server elsewhere, whose
    /// mods that view model shows on the Mods tab. Null for the server this launcher hosts.</param>
    public ServerControlViewModel(MainViewModel main, ServersViewModel servers, RemoteAdminViewModel? remote = null)
    {
        _main = main;
        _servers = servers;
        _remote = remote;
        _host = new ServerHost(() => main.Setup.Layout);
        _host.LineReceived += line => Ui(() => OnServerLine(line));
        _host.Exited += () => Ui(OnServerExited);
        _poll.Tick += async (_, _) => await PollAsync();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            FilterMaps();
        };

        LiveOptions = new ServerOptionsViewModel(main, live: true, owner: this, profile: () => Profile) { ServerLocked = true };
        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.Filter = m => ShowServerLog || ((ChatLine)m).Kind != ChatKind.Log;

        LaunchCommand = new RelayCommand(() => _ = LaunchAsync(), () => CanLaunch);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => ShowStop && Status != ServerStatus.Stopping);
        StartMatchCommand = new RelayCommand(() =>
        {
            if (_mods != null || _match != null) _ = StartThroughLobbyAsync();
            else Run(a => a.StartAsync(), "Match started.");
        }, () => CanControl && Status == ServerStatus.Lobby);
        EndMatchCommand = new RelayCommand(() => Run(a => a.EndMatchAsync(), "Match ended; the next map loads."), () => CanControl && Status is ServerStatus.Playing or ServerStatus.Warmup);
        NextMapCommand = new RelayCommand(() => Run(a => a.NextMapAsync(_state?.Next ?? ""), "Going to the next map."), () => CanControl && Status is ServerStatus.Lobby or ServerStatus.Playing or ServerStatus.Warmup or ServerStatus.Results);
        RestartMapCommand = new RelayCommand(() => Run(a => a.RestartAsync(), "Restarting the map."), () => CanControl && Status is ServerStatus.Playing or ServerStatus.Warmup);
        BackToLobbyCommand = new RelayCommand(() => _ = BackToLobbyAsync(), () => CanControl && Status is ServerStatus.Playing or ServerStatus.Warmup or ServerStatus.Results);

        RefreshMapsCommand = new RelayCommand(() => _ = LoadCatalogAsync(refresh: true), () => CatalogStatus != CatalogStatus.Loading && !Activity.IsBusy);
        CheckUpdatesCommand = new RelayCommand(() => _main.Page = Page.Settings);
        ApplyPlaylistCommand = new RelayCommand(() => _ = ApplyPlaylistAsync(), () => CanControl && PlaylistDirty && Playlist.Count > 0);
        ShufflePlaylistCommand = new RelayCommand(Shuffle, () => Playlist.Count > 1);
        ClearPlaylistCommand = new RelayCommand(() => _ = ClearPlaylistAsync(), () => Playlist.Count > 0);
        RemoveSelectedCommand = new RelayCommand(() => { if (SelectedEntry != null) Remove(SelectedEntry); }, () => SelectedEntry != null);
        MoveSelectedUpCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedEntry != null && Playlist.IndexOf(SelectedEntry) > 0);
        MoveSelectedDownCommand = new RelayCommand(() => MoveSelected(1), () => SelectedEntry != null && Playlist.IndexOf(SelectedEntry) < Playlist.Count - 1);
        SayCommand = new RelayCommand(() => _ = SayAsync(), () => CanControl && SayText.Trim().Length > 0);
        ApplyOptionsCommand = new RelayCommand(() => _ = ApplyOptionsAsync(), () => CanControl && LiveOptions.IsDirty);
        SelectTabCommand = new RelayCommand(p => Tab = Enum.Parse<ControlTab>((string)p!));
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync());
        JoinGameCommand = new RelayCommand(() => _ = JoinGameAsync(), () => CanJoinGame);
        main.Play.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayViewModel.IsGameRunning)) return;
            Raise(nameof(JoinGameText));
            CommandManager.InvalidateRequerySuggested();
        };
        ResetSettingsCommand = new RelayCommand(() => _ = ResetSettingsAsync(), () => _remoteMods != null && HasSavedSettings && !_busy);
    }

    private ActivityViewModel Activity => _main.Activity;

    /// <summary>The hosted server's settings (launcher.json), or what a server elsewhere told us.</summary>
    private ServerProfile Profile => IsRemote ? _remoteProfile : _main.Setup.State.Server;

    /// <summary>Connect to server as admin: a server elsewhere, not one this launcher hosts.</summary>
    public bool IsRemote => _remote != null;

    /// <summary>The server elsewhere keeps what is applied here for its next starts (its mod
    /// service's /admin/settings).</summary>
    public bool KeepsSettings => _keepsSettings;

    /// <summary>The server elsewhere holds each map in the lobby until Start (from its next game start).</summary>
    internal bool RemoteLobby { get => _remoteLobby; set => _remoteLobby = value; }

    /// <summary>The server elsewhere's mods, next to this PC's (its mod service).</summary>
    public RemoteAdminViewModel? RemoteMods => _remote;
    public bool HasModsTab => IsRemote && _remoteMods != null;

    public string Title => IsRemote ? Profile.Name : "Playlist and control";

    public ICommand DisconnectCommand { get; }
    public ICommand ResetSettingsCommand { get; }

    /// <summary>Join as player: this PC's game joins the server while this screen stays on it.</summary>
    public ICommand JoinGameCommand { get; }

    /// <summary>"Join as player", or "Running" while the game is open (as Play shows it).</summary>
    public string JoinGameText => _main.Play.IsGameRunning ? "Running" : "Join as player";

    private bool CanJoinGame =>
        IsRunning && Status != ServerStatus.Stopping && !_joining && !_main.Play.IsGameRunning && !Activity.IsBusy &&
        _main.Setup.IsReady && (IsRemote ? _remoteAddress != null : !UseFakeServer);

    /// <summary>Development and screenshots: a simulated server and the sample map list, no game.</summary>
    public bool UseFakeServer { get; set; }

    /// <summary>The --loopback development switch: the server and its mod service answer only this
    /// PC, so tests raise no firewall prompt.</summary>
    public bool Loopback
    {
        get => _host.Loopback;
        set => _host.Loopback = value;
    }

    public ServerStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(IsRunning));
            Raise(nameof(StatusText));
            Raise(nameof(StatusDetail));
            Raise(nameof(ShowLaunch));
            Raise(nameof(ShowStop));
            Raise(nameof(ShowBack));
            Raise(nameof(ShowPlaylistScreen));
            if (_main.Servers != null && !IsRemote) _main.Servers.Options.ServerLocked = IsRunning;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => Status != ServerStatus.NotRunning;
    public bool ShowLaunch => !IsRunning && !IsRemote;

    /// <summary>Stop server for the hosted one; a server elsewhere is only disconnected from.</summary>
    public bool ShowStop => IsRunning && !IsRemote;

    /// <summary>The header's back button: to Server options before launching; always for a server elsewhere.</summary>
    public bool ShowBack => ShowLaunch || IsRemote;
    public string BackText => IsRemote ? "Server manager" : "Server options";

    /// <summary>The hosted server's process, so the Play page doesn't take it for the game.</summary>
    public int? ServerProcessId => _host.IsRunning ? _host.Process?.Id : null;

    private bool CanControl => _admin is { IsLoggedIn: true } && !_busy;

    private bool CanLaunch =>
        ShowLaunch && !Activity.IsBusy && Playlist.Count > 0 && (UseFakeServer || CatalogStatus == CatalogStatus.Ready);

    public string StatusText => Status switch
    {
        ServerStatus.NotRunning => "Not running",
        ServerStatus.Starting => "Starting",
        ServerStatus.Loading => "Loading",
        ServerStatus.Lobby => "Lobby",
        ServerStatus.Warmup => "Warm-up",
        ServerStatus.Playing => "Playing",
        ServerStatus.Results => "Results",
        ServerStatus.NotAnswering => "Not answering",
        _ => "Stopping",
    };

    public string StatusDetail
    {
        get
        {
            var s = _state;
            string map = s == null ? "" : MapTitle(s.Map);
            return Status switch
            {
                ServerStatus.NotRunning => Playlist.Count == 0 ? "Drag maps into the playlist, then launch." : $"{Playlist.Count} {(Playlist.Count == 1 ? "map" : "maps")} in the playlist.",
                ServerStatus.Starting => "Waiting for the server to load. Windows may ask to let BattlefrontII.exe through the firewall: that's the server.",
                ServerStatus.Loading => map.Length > 0 ? $"Loading {map}" : "Loading the next map",
                ServerStatus.Lobby when s != null => $"{Plural(s.Players, "player")} waiting on {map}. Start the match when everyone's in.",
                ServerStatus.Warmup when s != null => $"{map} · starting in {Clock(s.TimeLeft)}",
                ServerStatus.Playing when s != null => $"{map} · {(s.TimeLeft >= 0 ? Clock(s.TimeLeft) + " left · " : "")}{Plural(s.Players, "player")} · tickets {s.Tickets1}/{s.Tickets2}",
                ServerStatus.Results when s != null => $"Match over on {map}" + (s.Next.Length > 0 ? $" · next {MapTitle(s.Next)}" : ""),
                ServerStatus.Stopping => "Closing the server",
                ServerStatus.NotAnswering => "Its game may be restarting. This screen carries on once it answers.",
                _ => "",
            };
        }
    }

    public string ServerSummary => IsRemote
        ? $"{_remoteAddress}  ·  {Profile.TickRate} Hz  ·  {Profile.MaxPlayers} players  ·  {Profile.DifficultyName} AI"
        : $"{Profile.Name}  ·  port {Profile.Port}  ·  {Profile.TickRate} Hz  ·  {Profile.MaxPlayers} players  ·  {Profile.DifficultyName} AI";

    /// <summary>The Players tab with nobody on.</summary>
    public string NoPlayersText => IsRemote
        ? "No one is on yet. Players join from the launcher's Multiplayer page, or the game's Direct Connect, with the server's address."
        : "No one has joined yet. Players join from the launcher's Multiplayer page, or the game's Direct Connect, with this PC's address and the server's port.";

    public string OptionsNote => !IsRemote
        ? "These apply from the next map. Name, port, tick rate and passwords change only when the server starts again."
        : KeepsSettings
            ? "These apply from the next map, and the server keeps them. Its name, join password, player limit, tick rate and lobby apply when its game restarts."
            : "These apply from the next map, until the server restarts: it keeps no settings from here.";

    /// <summary>The hosted server's log is in the chat; a server elsewhere sends only chat and events.</summary>
    public bool ShowServerLogSwitch => !IsRemote;

    /// <summary>The saved name or limits wait for the server's game to restart.</summary>
    public bool RestartNeeded => _remoteSettings?.RestartNeeded == true;

    /// <summary>The server elsewhere keeps something an admin applied from here.</summary>
    public bool HasSavedSettings => _remoteSettings?.HasSaved == true;

    /// <summary>A note above the map list: the server elsewhere has no list of its own, so this one is this PC's.</summary>
    public string CatalogNote { get => _catalogNote; private set { if (Set(ref _catalogNote, value)) Raise(nameof(HasCatalogNote)); } }
    public bool HasCatalogNote => CatalogNote.Length > 0;

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
    private static string Clock(int seconds) => seconds < 0 ? "--:--" : $"{seconds / 60}:{seconds % 60:00}";

    private string MapTitle(string mission) =>
        _catalog?.Find(mission) is { } c ? $"{c.MapName} · {c.ModeName}" : mission;

    public ICommand LaunchCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand StartMatchCommand { get; }
    public ICommand EndMatchCommand { get; }
    public ICommand NextMapCommand { get; }
    public ICommand RestartMapCommand { get; }
    public ICommand BackToLobbyCommand { get; }

    public ControlTab Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value) && value == ControlTab.Players) _ = PollAsync();
        }
    }

    public ICommand SelectTabCommand { get; }

    public ObservableCollection<CatalogMapViewModel> Maps { get; } = new();

    public CatalogStatus CatalogStatus
    {
        get => _catalogStatus;
        private set
        {
            if (!Set(ref _catalogStatus, value)) return;
            Raise(nameof(CatalogReady));
            Raise(nameof(ShowPlaylistScreen));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CatalogReady => CatalogStatus == CatalogStatus.Ready;

    /// <summary>The playlist works without a catalog only while the server runs (it was started
    /// before), or with the fake server.</summary>
    public bool ShowPlaylistScreen => CatalogStatus is not (CatalogStatus.NeedsUpdate or CatalogStatus.NotInstalled) || IsRunning;

    public string CatalogMessage { get => _catalogMessage; private set => Set(ref _catalogMessage, value); }

    /// <summary>Filtered a moment after the last key, not on every one.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            _searchDelay.Stop();
            _searchDelay.Start();
        }
    }

    /// <summary>Ground battles or space battles: the catalog lists one at a time.</summary>
    public BattleKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            UpdateFilterChoices();
            FilterMaps();
        }
    }

    public IReadOnlyList<FilterChoice> EraChoices { get => _eraChoices; private set => Set(ref _eraChoices, value); }
    public IReadOnlyList<FilterChoice> ModeChoices { get => _modeChoices; private set => Set(ref _modeChoices, value); }

    public FilterChoice SelectedEra
    {
        get => _era;
        set
        {
            if (value != null && Set(ref _era, value)) FilterMaps();
        }
    }

    public FilterChoice SelectedMode
    {
        get => _mode;
        set
        {
            if (value != null && Set(ref _mode, value)) FilterMaps();
        }
    }

    /// <summary>The era and mode filters offer only what this kind of battle has; a choice that
    /// isn't there any more goes back to "all".</summary>
    private void UpdateFilterChoices()
    {
        var maps = _catalog?.Maps ?? (IReadOnlyList<MapInfo>)Array.Empty<MapInfo>();
        bool space = Kind == BattleKind.Space;
        EraChoices = new[] { AllEras }.Concat(CatalogFilter.Eras(maps, space).Select(e => new FilterChoice(e.Id, e.Name))).ToList();
        ModeChoices = new[] { AllModes }.Concat(CatalogFilter.Modes(maps, space).Select(m => new FilterChoice(m.Id, m.Name))).ToList();
        _era = EraChoices.FirstOrDefault(c => c.Id == _era.Id) ?? AllEras;
        _mode = ModeChoices.FirstOrDefault(c => c.Id == _mode.Id) ?? AllModes;
        Raise(nameof(SelectedEra));
        Raise(nameof(SelectedMode));
    }

    public bool ShowStock
    {
        get => _showStock;
        set
        {
            if (Set(ref _showStock, value)) FilterMaps();
        }
    }

    public bool ShowMods
    {
        get => _showMods;
        set
        {
            if (Set(ref _showMods, value)) FilterMaps();
        }
    }

    public string CatalogCount => _catalog == null ? "" : $"{Maps.Count} of {_catalog.Maps.Count} maps";

    public ICommand RefreshMapsCommand { get; }
    public ICommand CheckUpdatesCommand { get; }

    public void Refresh()
    {
        if (_catalog == null && CatalogStatus is CatalogStatus.Idle or CatalogStatus.Failed or CatalogStatus.NotInstalled or CatalogStatus.NeedsUpdate)
            _ = LoadCatalogAsync(refresh: false);
        if (Playlist.Count != Profile.Playlist.Count) LoadPlaylist();
        Raise(nameof(ServerSummary));
        Raise(nameof(StatusDetail));
        LiveOptions.Refresh();
    }

    public async Task LoadCatalogAsync(bool refresh)
    {
        if (UseFakeServer)
        {
            SetCatalog(SampleCatalog.Load());
            return;
        }
        if (IsRemote)
        {
            // The server's own list (its game wrote it), so every map offered loads there
            if (await ReadTheirMapsAsync() is { } theirs)
            {
                CatalogNote = "";
                SetCatalog(theirs);
                if (refresh) _main.ShowNotice($"The server has {theirs.Maps.Count} maps.");
                return;
            }
            CatalogNote = "These are your game's maps: the server has no list of its own. A map from a mod it lacks won't load there.";
        }
        if (!_main.Setup.IsReady)
        {
            CatalogStatus = CatalogStatus.NotInstalled;
            CatalogMessage = "Import your game and install OpenSWBF2 on the Play page first.";
            return;
        }
        var store = new MapCatalogStore(_main.Setup.Layout, _main.Setup.State.InstalledVersion);
        bool supported;
        try { supported = await Task.Run(() => store.GameSupportsListing); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { supported = false; }
        if (!supported)
        {
            CatalogStatus = CatalogStatus.NeedsUpdate;
            CatalogMessage = $"OpenSWBF2 {_main.Setup.State.InstalledVersion} can't list its maps for the launcher yet. A newer build can.";
            return;
        }
        if (!refresh && await Task.Run(store.TryLoadCached) is { } cached)
        {
            SetCatalog(cached);
            return;
        }

        CatalogStatus = CatalogStatus.Loading;
        CatalogMessage = "Reading the map list from the game…";
        MapCatalog? built = null;
        var error = await Activity.RunAsync("Reading the map list", async (progress, ct) => built = await store.BuildAsync(progress, ct), cancellable: true);
        if (built != null)
        {
            SetCatalog(built);
            if (refresh) _main.ShowNotice($"Found {built.Maps.Count} maps.");
            return;
        }
        CatalogStatus = CatalogStatus.Failed;
        CatalogMessage = error is OperationCanceledException ? "Cancelled." : error?.Message ?? "The map list is empty.";
    }

    private async Task<MapCatalog?> ReadTheirMapsAsync()
    {
        if (_remoteMods == null) return null;
        try { return await _remoteMods.GetMissionsAsync(CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or ModServiceException or InvalidDataException or TaskCanceledException) { return null; }
    }

    public void SetCatalog(MapCatalog catalog)
    {
        _catalog = catalog;
        CatalogStatus = CatalogStatus.Ready;
        CatalogMessage = "";
        UpdateFilterChoices();
        FilterMaps();
        LoadPlaylist();
    }

    /// <summary>The catalog shows one kind of battle, narrowed by era, mode, source and search;
    /// each map lists only the missions that fit. A search opens the maps it finds when there
    /// are few enough to read.</summary>
    private void FilterMaps()
    {
        Maps.Clear();
        if (_catalog == null) return;
        var query = new CatalogQuery(Kind == BattleKind.Space, SelectedEra.Id, SelectedMode.Id, ShowStock, ShowMods, Search);
        var maps = CatalogFilter.Maps(_catalog.Maps, query);
        bool openAll = Search.Trim().Length > 0 && maps.Count <= 3;
        foreach (var map in maps)
            Maps.Add(new CatalogMapViewModel(this, map, CatalogFilter.Choices(map, query), _expanded.Contains(map.Code) || openAll));
        Raise(nameof(CatalogCount));
    }

    internal void SetExpanded(string code, bool expanded)
    {
        if (expanded) _expanded.Add(code);
        else _expanded.Remove(code);
    }

    public ObservableCollection<PlaylistItemViewModel> Playlist { get; } = new();

    public PlaylistItemViewModel? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }

    /// <summary>The playlist changed since it was sent to the running server.</summary>
    public bool PlaylistDirty
    {
        get => _playlistDirty;
        private set
        {
            if (Set(ref _playlistDirty, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool PlaylistDragHover { get => _playlistDragHover; set => Set(ref _playlistDragHover, value); }

    public bool PlaylistEmpty => Playlist.Count == 0;

    public string PlaylistCount => Playlist.Count == 0 ? "" : Plural(Playlist.Count, "map");

    public ICommand ApplyPlaylistCommand { get; }
    public ICommand ShufflePlaylistCommand { get; }
    public ICommand ClearPlaylistCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand MoveSelectedUpCommand { get; }
    public ICommand MoveSelectedDownCommand { get; }

    private void LoadPlaylist()
    {
        Playlist.Clear();
        foreach (var entry in Profile.Playlist) Playlist.Add(new PlaylistItemViewModel(this, entry, _catalog?.Find(entry.Mission), _catalog != null));
        AfterPlaylistChange(save: false);
        // A server elsewhere keeps its unsent edits through a map list refresh
        if (!IsRemote) PlaylistDirty = false;
        MarkCurrent();
    }

    public void AddMissions(IEnumerable<MissionChoice> choices, int index = -1)
    {
        if (index < 0 || index > Playlist.Count) index = Playlist.Count;
        PlaylistItemViewModel? last = null;
        foreach (var choice in choices)
        {
            var entry = new PlaylistEntry { Mission = choice.Mission, Title = choice.Title };
            last = new PlaylistItemViewModel(this, entry, choice, catalogLoaded: true);
            Playlist.Insert(index++, last);
        }
        if (last == null) return;
        SelectedEntry = last;
        AfterPlaylistChange();
    }

    public void MoveEntry(PlaylistItemViewModel item, int index)
    {
        int from = Playlist.IndexOf(item);
        if (from < 0) return;
        if (index > from) index--;
        index = Math.Clamp(index, 0, Playlist.Count - 1);
        if (index == from) return;
        Playlist.Move(from, index);
        SelectedEntry = item;
        AfterPlaylistChange();
    }

    private void MoveSelected(int by)
    {
        if (SelectedEntry == null) return;
        int from = Playlist.IndexOf(SelectedEntry);
        int to = Math.Clamp(from + by, 0, Playlist.Count - 1);
        if (to == from) return;
        Playlist.Move(from, to);
        AfterPlaylistChange();
    }

    public void Remove(PlaylistItemViewModel item)
    {
        int index = Playlist.IndexOf(item);
        if (index < 0) return;
        Playlist.RemoveAt(index);
        SelectedEntry = Playlist.Count == 0 ? null : Playlist[Math.Min(index, Playlist.Count - 1)];
        AfterPlaylistChange();
    }

    private void Shuffle()
    {
        var order = Playlist.OrderBy(_ => Random.Shared.Next()).ToList();
        Playlist.Clear();
        foreach (var item in order) Playlist.Add(item);
        AfterPlaylistChange();
    }

    private async Task ClearPlaylistAsync()
    {
        if (!await _main.ConfirmAsync("Clear the playlist?", $"All {Plural(Playlist.Count, "map")} come out of it.", "Clear")) return;
        Playlist.Clear();
        SelectedEntry = null;
        AfterPlaylistChange();
    }

    internal void AfterPlaylistChange(bool save = true)
    {
        for (int i = 0; i < Playlist.Count; i++) Playlist[i].Number = i + 1;
        if (save)
        {
            Profile.Playlist = Playlist.Select(p => p.Entry).ToList();
            if (!IsRemote) _main.Setup.Save();
            if (IsRunning) PlaylistDirty = true;
        }
        Raise(nameof(PlaylistEmpty));
        Raise(nameof(PlaylistCount));
        if (Status == ServerStatus.NotRunning) Raise(nameof(StatusDetail));
        CommandManager.InvalidateRequerySuggested();
    }

    private void MarkCurrent()
    {
        string current = _state?.Phase is ServerPhase.Shell or null ? "" : _state.Map;
        string next = _state?.Next ?? "";
        bool foundCurrent = false, foundNext = false;
        foreach (var item in Playlist)
        {
            item.IsCurrent = !foundCurrent && current.Length > 0 && string.Equals(item.Entry.Mission, current, StringComparison.OrdinalIgnoreCase);
            foundCurrent |= item.IsCurrent;
            item.IsNext = !foundNext && foundCurrent && !item.IsCurrent && string.Equals(item.Entry.Mission, next, StringComparison.OrdinalIgnoreCase);
            foundNext |= item.IsNext;
        }
    }

    private async Task ApplyPlaylistAsync()
    {
        if (_admin == null || !CheckPlaylist()) return;
        await Guard(async admin =>
        {
            await admin.SetRotationAsync(Profile.Playlist);
            PlaylistDirty = false;
            string kept = await KeepOnServerAsync(AdminSettings.ForPlaylist(Profile.Playlist), "it");
            string first = Profile.Playlist[0].Mission;
            if (Status == ServerStatus.Lobby && _state != null && !string.Equals(_state.Map, first, StringComparison.OrdinalIgnoreCase))
            {
                // Nothing is being played yet: load the new first map. /changemap keeps the hold,
                // so it waits in the lobby too.
                await admin.NextMapAsync(first);
                _main.ShowNotice($"Playlist sent. The lobby moves to {MapTitle(first)}." + kept);
            }
            else
            {
                _main.ShowNotice((Status is ServerStatus.Playing or ServerStatus.Warmup
                    ? "Playlist sent. The map being played stays; the new order follows it."
                    : "Playlist sent.") + kept);
            }
        }, poll: true);
    }

    /// <summary>Every map is in the map list: a wrong name stops the server's next load.</summary>
    private bool CheckPlaylist()
    {
        var unknown = _catalog == null ? new List<string>() : Playlist.Where(p => p.IsUnknown).Select(p => p.Entry.Mission).ToList();
        if (unknown.Count == 0) return true;
        _main.ShowNotice($"Not in the map list: {string.Join(", ", unknown)}. Remove them or refresh the maps first.", error: true);
        return false;
    }

    internal bool CanPlayNow(PlaylistItemViewModel item) =>
        CanControl && !item.IsUnknown && Status is ServerStatus.Lobby or ServerStatus.Warmup or ServerStatus.Playing or ServerStatus.Results;

    /// <summary>The server goes to this map now (/changemap). An unsent playlist is sent first, so
    /// the rotation carries on from there.</summary>
    internal async Task PlayNowAsync(PlaylistItemViewModel item)
    {
        string mission = item.Entry.Mission;
        if (PlaylistDirty && !CheckPlaylist()) return;
        if (Status is ServerStatus.Playing or ServerStatus.Warmup &&
            !await _main.ConfirmAsync($"Play {MapTitle(mission)} now?", "This match ends now, and the server loads that map.", "Play now"))
            return;
        await Guard(async admin =>
        {
            if (PlaylistDirty)
            {
                await admin.SetRotationAsync(Profile.Playlist);
                PlaylistDirty = false;
                _main.ShowNotice("Playlist sent." + await KeepOnServerAsync(AdminSettings.ForPlaylist(Profile.Playlist), "it"));
            }
            await admin.NextMapAsync(mission);
            AddMessage(ChatKind.Admin, $"Changing to {MapTitle(mission)}.");
            await Task.Delay(300);
        }, poll: true);
    }

    /// <summary>A server elsewhere keeps what was just sent for its next starts (when its service
    /// does). Returns what to add to the notice.</summary>
    private async Task<string> KeepOnServerAsync(AdminSettings settings, string what)
    {
        if (!IsRemote || _remoteMods == null || !_keepsSettings) return "";
        try
        {
            ShowSettings(await _remoteMods.SaveSettingsAsync(settings, CancellationToken.None));
            return $" The server keeps {what} for its next starts.";
        }
        catch (Exception e) when (e is HttpRequestException or ModServiceException or InvalidDataException or TaskCanceledException)
        {
            return $" The server couldn't keep {what} for its next start: {e.Message}";
        }
    }

    private void ShowSettings(AdminSettingsReply? settings)
    {
        _remoteSettings = settings;
        Raise(nameof(RestartNeeded));
        Raise(nameof(HasSavedSettings));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Back to the server's defaults: it forgets what admins saved from here.</summary>
    private async Task ResetSettingsAsync()
    {
        if (_remoteMods == null) return;
        if (!await _main.ConfirmAsync("Back to the defaults?",
                "The server forgets the playlist, options, name, join password and limits saved from here. From its next start it plays the 8 stock Conquest maps with 16 bots a team on Hard; the game running now keeps what it has.",
                "Back to the defaults"))
            return;
        try
        {
            ShowSettings(await _remoteMods.ResetSettingsAsync(CancellationToken.None));
            _main.ShowNotice("The server goes back to its defaults from its next start.");
        }
        catch (Exception e) when (e is HttpRequestException or ModServiceException or InvalidDataException or TaskCanceledException)
        {
            _main.ShowError(e);
        }
    }

    public ObservableCollection<PlayerRowViewModel> Players { get; } = new();
    public string PlayersTab => Players.Count == 0 ? "Players" : $"Players ({Players.Count})";
    public bool NoPlayers => Players.Count == 0 && LauncherWaiting.Count == 0;

    private void SetPlayers(IReadOnlyList<PlayerInfo> players)
    {
        Players.Clear();
        foreach (var player in players.OrderBy(p => p.Team == 0 ? 3 : p.Team).ThenByDescending(p => p.Score))
            Players.Add(new PlayerRowViewModel(this, player));
        Raise(nameof(PlayersTab));
        Raise(nameof(NoPlayers));
    }

    internal async Task KickAsync(PlayerInfo player)
    {
        if (!await _main.ConfirmAsync($"Kick {player.DisplayName}?", "They can join again unless the server is full or passworded.", "Kick")) return;
        await Guard(async admin =>
        {
            await admin.KickAsync(player.Slot);
            AddMessage(ChatKind.Admin, $"Kicked {player.DisplayName}.");
        });
        await PollAsync();
    }

    internal async Task SwapAsync(PlayerInfo player)
    {
        await Guard(async admin =>
        {
            await admin.SwapAsync(player.Slot);
            AddMessage(ChatKind.Admin, $"Moved {player.DisplayName} to the other team.");
        });
        await PollAsync();
    }

    /// <summary>The game has no private messages: write to everyone, addressed to the player.</summary>
    internal void MessagePlayer(PlayerInfo player)
    {
        SayText = $"{player.DisplayName}: ";
        Tab = ControlTab.Chat;
        SayFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SayFocusRequested;

    public ObservableCollection<ChatLine> Messages { get; } = new();
    public ICollectionView MessagesView { get; }

    public bool ShowServerLog
    {
        get => _showServerLog;
        set
        {
            if (Set(ref _showServerLog, value)) MessagesView.Refresh();
        }
    }

    public string SayText
    {
        get => _sayText;
        set
        {
            if (Set(ref _sayText, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand SayCommand { get; }

    private async Task SayAsync()
    {
        string text = SayText.Trim();
        await Guard(async admin =>
        {
            await admin.SayAsync(text);
            AddMessage(ChatKind.Admin, "You: " + text);
            SayText = "";
        });
    }

    public void AddMessage(ChatKind kind, string text)
    {
        Messages.Add(new ChatLine(DateTime.Now, kind, text));
        while (Messages.Count > 600) Messages.RemoveAt(0);
        MessageAdded?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? MessageAdded;

    public ServerOptionsViewModel LiveOptions { get; }
    public ICommand ApplyOptionsCommand { get; }

    internal void OptionsChanged(ServerOptionsViewModel source)
    {
        if (source != LiveOptions) LiveOptions.Refresh();
        else if (!IsRemote) _servers.Options.Refresh();
        if (source != LiveOptions && IsRunning) LiveOptions.IsDirty = true;
        Raise(nameof(ServerSummary));
        Raise(nameof(Title));
    }

    private async Task ApplyOptionsAsync()
    {
        await Guard(async admin =>
        {
            foreach (var (command, value) in Profile.OptionCommands())
                if (Profile.Answers(command)) await admin.SetOptionAsync(command, value);
            LiveOptions.IsDirty = false;
            if (!IsRemote)
            {
                _main.ShowNotice("Options sent. They apply from the next map.");
                return;
            }
            // A server elsewhere also keeps them, with its name and limits for its next start
            var keep = AdminSettings.ForOptions(Profile);
            bool restartSettings = LiveOptions.RestartSettingsChanged && KeepsSettings;
            if (restartSettings)
            {
                var server = AdminSettings.ForServer(Profile, _remoteLobby);
                (keep.Name, keep.JoinPassword, keep.MaxPlayers, keep.TickRate, keep.Lobby) =
                    (server.Name, server.JoinPassword, server.MaxPlayers, server.TickRate, server.Lobby);
            }
            string kept = await KeepOnServerAsync(keep, "them");
            LiveOptions.RestartSettingsChanged = false;
            _main.ShowNotice("Options sent. They apply from the next map." + kept +
                             (restartSettings && RestartNeeded ? " The name, password, limits and lobby apply when its game restarts." : ""));
        });
    }

    private async Task LaunchAsync()
    {
        var unknown = _catalog == null ? new List<string>() : Playlist.Where(p => p.IsUnknown).Select(p => p.Entry.Mission).ToList();
        if (unknown.Count > 0)
        {
            _main.ShowNotice($"Not in the map list: {string.Join(", ", unknown)}. Remove them first; a wrong name stops the server's next load.", error: true);
            return;
        }
        Profile.Normalize();
        _main.Setup.Save();
        Messages.Clear();
        _state = null;
        _stopRequested = false;
        _quietExit = false;
        PlaylistDirty = false;
        LiveOptions.IsDirty = false;

        if (UseFakeServer)
        {
            Status = ServerStatus.Starting;
            var fake = new FakeServerAdmin(_catalog ?? SampleCatalog.Load(), Profile);
            await fake.ConnectAsync(Profile.AdminPassword, CancellationToken.None);
            Attach(fake);
            await PollAsync();
            return;
        }

        try
        {
            var process = _host.Start(Profile);
            _main.Setup.State.RunningServer = new ServerSession(process.Id, process.StartTimeUtc, Profile.Port);
            _main.Setup.Save();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _main.ShowError(e);
            return;
        }
        Status = ServerStatus.Starting;
        AddMessage(ChatKind.Admin, $"Starting {Profile.Name} on port {Profile.Port}.");
        _main.Play.Refresh();

        var client = new RconClient(new IPEndPoint(IPAddress.Loopback, Profile.Port)) { ReplyTimeout = TimeSpan.FromSeconds(1) };
        var error = await Activity.RunAsync("Starting the server", async (progress, ct) =>
        {
            progress.Report(new StepUpdate("Starting the server", "Loading the first map"));
            await ConnectAsync(client, Profile.AdminPassword, ct);
            if (_host.MapsOnCommandLine < Profile.Playlist.Count)
            {
                progress.Report(new StepUpdate("Starting the server", "Sending the rest of the playlist"));
                await client.SetRotationAsync(Profile.Playlist, ct);
            }
        }, cancellable: true);

        if (error != null)
        {
            await client.DisposeAsync();
            _quietExit = true;
            if (_host.IsRunning)
            {
                _stopRequested = true;
                await _host.StopAsync(null, TimeSpan.FromSeconds(3));
            }
            _main.ShowError(error, "Cancelled: the server was stopped.");
            return;
        }
        Attach(client);
        StartSharing(client);
        await PollAsync();
    }

    /// <summary>Shares this PC's mods with joining launchers and runs their lobby (the same service
    /// as the Docker server's), on TCP at the game's port number. Windows asks the first time
    /// whether OpenBattlefront may accept players there.</summary>
    private void StartSharing(IServerAdmin admin)
    {
        if (!Profile.ShareMods || UseFakeServer || _mods != null) return;
        var layout = _main.Setup.Layout;
        var maps = new MapCatalogStore(layout, _main.Setup.State.InstalledVersion);
        var service = new ModHostService(new ModHostOptions
        {
            AddOn = layout.AddOn,
            Hashes = FileHashCache.For(layout),
            ServerName = Profile.Name,
            Version = _main.Setup.State.InstalledVersion ?? "",
            JoinPassword = Profile.JoinPassword,
            AdminPassword = Profile.AdminPassword,
            RateBytesPerSecond = Profile.ModRate * 125_000.0,
            Bind = Loopback ? IPAddress.Loopback : IPAddress.Any,
            Port = Profile.Port,
            Missions = maps.TryReadCached,
        }, new AdminGameProbe(admin));
        try
        {
            service.Start();
            _mods = service;
            AddMessage(ChatKind.Admin, $"Sharing your mods and running the launcher lobby on TCP {Profile.Port}. Players join from the launcher's Multiplayer page.");
        }
        catch (System.Net.Sockets.SocketException e)
        {
            _ = service.DisposeAsync().AsTask();
            _main.ShowNotice($"TCP {Profile.Port} is taken ({e.Message}), so this server doesn't share mods or run the launcher lobby.", error: true);
        }
        UpdateSharing();
    }

    private async Task StopSharingAsync()
    {
        var mods = _mods;
        _mods = null;
        UpdateSharing();
        if (mods != null) await mods.DisposeAsync();
    }

    private string _sharingText = "";
    private IReadOnlyList<LobbyRow> _launcherWaiting = Array.Empty<LobbyRow>();

    /// <summary>"Sharing 2 mods on TCP 3658 · 3 waiting in launchers", or empty.</summary>
    public string SharingText { get => _sharingText; private set => Set(ref _sharingText, value); }
    public bool HasSharingText => SharingText.Length > 0;

    /// <summary>Players waiting in their launchers' lobby, their game still closed.</summary>
    public IReadOnlyList<LobbyRow> LauncherWaiting { get => _launcherWaiting; private set => Set(ref _launcherWaiting, value); }
    public bool HasLauncherWaiting => LauncherWaiting.Count > 0;

    private void UpdateSharing()
    {
        var mods = _mods;
        IReadOnlyList<LobbyPlayer> lobby;
        if (mods != null)
        {
            lobby = mods.Lobby.List();
            bool loading = (string?)mods.Watch.Describe()["start"] == "loading";
            SharingText = $"Sharing {Plural(mods.Library.Folders.Count, "mod")} on TCP {mods.Port}  ·  {lobby.Count} waiting in launchers" +
                (loading ? "  ·  opening their games" : "");
        }
        else if (_match != null)
        {
            // A server elsewhere: its mod service's lobby, read with /status
            lobby = _match.Lobby;
            SharingText = $"Launcher lobby on TCP {_remoteAddress?.Port}  ·  {lobby.Count} waiting in launchers" +
                (_match.IsLoading ? "  ·  opening their games" : "");
        }
        else
        {
            lobby = Array.Empty<LobbyPlayer>();
            SharingText = "";
        }
        LauncherWaiting = lobby.Select(p => new LobbyRow(p.Name, p.IsDownloading ? $"Downloading {p.Progress}%" : "Waiting in the launcher", false, false)).ToList();
        Raise(nameof(HasSharingText));
        Raise(nameof(HasLauncherWaiting));
        Raise(nameof(NoPlayers));
    }

    /// <summary>Start match with the launcher lobby: every waiting player's game opens first, and
    /// the match begins once they're in (or after 60 s); anyone still downloading joins later.
    /// The hosted server's lobby runs here; a server elsewhere runs its own.</summary>
    private async Task StartThroughLobbyAsync()
    {
        var mods = _mods;
        var remote = _remoteMods;
        if (mods == null && (remote == null || _match == null) || _busy) return;
        var lobby = mods != null ? mods.Lobby.List() : _match!.Lobby;
        var downloading = lobby.Where(p => p.IsDownloading).Select(p => p.Name).ToList();
        int waiting = lobby.Count - downloading.Count;
        if (downloading.Count > 0 && !await _main.ConfirmAsync("Start the match?",
                $"Still downloading: {string.Join(", ", downloading)}. They join when their download is done.", "Start match"))
            return;
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (mods != null) await mods.Watch.StartAsync(CancellationToken.None);
            else await remote!.StartMatchAsync(CancellationToken.None);
            AddMessage(ChatKind.Admin, waiting > 0
                ? $"Starting: the games of {Plural(waiting, "player")} waiting in launchers are opening; the match begins once they're in."
                : "Match started.");
        }
        catch (ModHostRefused e)
        {
            _main.ShowNotice(char.ToUpperInvariant(e.Message[0]) + e.Message[1..] + ".", error: true);
        }
        catch (Exception e) when (e is ModServiceException or HttpRequestException or TaskCanceledException)
        {
            _main.ShowError(e);
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
        await PollAsync();
    }

    /// <summary>Retries until the server answers: it loads the shell and its first map before
    /// the admin port does.</summary>
    private async Task ConnectAsync(IServerAdmin admin, string password, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            if (!_host.IsRunning) throw new InvalidOperationException("The server closed while starting. Its log is in Logs\\server.log.");
            try
            {
                await admin.ConnectAsync(password, ct);
                return;
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct);
            }
            catch (RconException e) when (e.Message.StartsWith("Too many") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10_000, ct);
            }
        }
    }

    private void Attach(IServerAdmin admin)
    {
        _admin = admin;
        admin.Pushed += line => Ui(() => OnPushed(line));
        admin.Lost += _ => Ui(() =>
        {
            // A server elsewhere shows it through its polls, and comes back by itself
            if (IsRunning && !IsRemote) AddMessage(ChatKind.Admin, "The server stopped answering.");
        });
        if (Status == ServerStatus.Starting) Status = ServerStatus.Loading;
        _poll.Start();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>A server left running by the last launch: find it again and log in.</summary>
    public async Task ReattachAsync()
    {
        var session = _main.Setup.State.RunningServer;
        if (session == null || IsRunning) return;
        if (!_host.TryAttach(session))
        {
            _main.Setup.State.RunningServer = null;
            _main.Setup.Save();
            return;
        }
        Status = ServerStatus.Loading;
        AddMessage(ChatKind.Admin, $"Found the server left running (process {session.Pid}).");
        var client = new RconClient(new IPEndPoint(IPAddress.Loopback, session.Port)) { ReplyTimeout = TimeSpan.FromSeconds(1) };
        try
        {
            await ConnectAsync(client, Profile.AdminPassword, CancellationToken.None);
            Attach(client);
            StartSharing(client);
            await PollAsync();
        }
        catch (Exception e) when (e is TimeoutException or RconException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            await client.DisposeAsync();
            _main.ShowNotice("The server left running didn't answer: " + e.Message, error: true);
        }
    }

    /// <summary>Connect to server as admin: this screen for a server elsewhere, logged in over
    /// rcon. <paramref name="profile"/> is what it said it runs (ServerReader);
    /// <paramref name="settings"/> what its service keeps for the next start, whose name and
    /// limits are the ones shown.</summary>
    internal async Task AttachRemoteAsync(ServerAddress address, string password, IServerAdmin admin, ModUploadClient? mods,
                                          bool keepsSettings, ServerProfile profile, AdminSettingsReply? settings)
    {
        _remoteAddress = address;
        _remotePassword = password;
        _remoteMods = mods;
        _keepsSettings = keepsSettings && mods != null && settings != null;
        if (settings?.Settings is { } next)
            new AdminSettings { Name = next.Name, JoinPassword = next.JoinPassword, MaxPlayers = next.MaxPlayers, TickRate = next.TickRate }.ApplyTo(profile);
        _remoteLobby = settings?.Settings.Lobby ?? false;
        profile.Port = address.Port;
        _remoteProfile = profile;
        ShowSettings(settings);
        Messages.Clear();
        _state = null;
        _match = null;
        _lastAnswer = DateTime.UtcNow;
        PlaylistDirty = false;
        LiveOptions.IsDirty = false;
        LiveOptions.RestartSettingsChanged = false;
        Tab = ControlTab.Playlist;
        Status = ServerStatus.Loading;
        Attach(admin);
        AddMessage(ChatKind.Admin, $"Connected to {profile.Name} at {address} as admin.");

        _catalog = null;
        Maps.Clear();
        await LoadCatalogAsync(refresh: false);
        if (_catalog == null) LoadPlaylist();
        LiveOptions.Refresh();
        foreach (string name in new[] { nameof(Title), nameof(ServerSummary), nameof(KeepsSettings), nameof(HasModsTab), nameof(OptionsNote), nameof(NoPlayersText) })
            Raise(name);
        await PollAsync();
    }

    /// <summary>Screenshots: a server elsewhere's launcher lobby and Mods tab, without polling it.</summary>
    internal void ShowSampleLobby(ModUploadClient mods, MatchStatus match)
    {
        _poll.Stop();
        _remoteMods = mods;
        _keepsSettings = true;
        _match = match;
        UpdateSharing();
        foreach (string name in new[] { nameof(HasModsTab), nameof(KeepsSettings), nameof(OptionsNote) }) Raise(name);
        LiveOptions.Refresh();
    }

    /// <summary>Join as player: the game joins this server straight away, past the launcher lobby,
    /// while this screen stays on it. A server elsewhere is joined at the address the admin used,
    /// the hosted one on this PC; the join password is the one the game runs with.</summary>
    private async Task JoinGameAsync()
    {
        if (!CanJoinGame) return;
        _joining = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            ServerAddress server = IsRemote ? _remoteAddress! : new ServerAddress("127.0.0.1", Profile.Port);
            string? password = IsRemote ? await ReadJoinPasswordAsync() : Profile.JoinPassword;
            if (await _main.Multiplayer.JoinNowAsync(server, password))
                AddMessage(ChatKind.Admin, "Your game is opening on the server.");
        }
        finally
        {
            _joining = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The join password a server elsewhere runs with: its game says (/password with no
    /// value answers with it), else what its service keeps for it.</summary>
    private async Task<string?> ReadJoinPasswordAsync()
    {
        if (_admin is { IsLoggedIn: true } admin)
        {
            try { return await admin.ReadTextAsync("password"); }
            catch (Exception e) when (e is TimeoutException or RconException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
        return _remoteSettings?.Settings.JoinPassword;
    }

    /// <summary>Leaves a server elsewhere as it is, and logs out.</summary>
    public async Task DisconnectAsync()
    {
        if (!IsRemote) return;
        _poll.Stop();
        var admin = _admin;
        _admin = null;
        _state = null;
        _match = null;
        _remoteMods = null;
        _keepsSettings = false;
        ShowSettings(null);
        Status = ServerStatus.NotRunning;
        SetPlayers(Array.Empty<PlayerInfo>());
        UpdateSharing();
        if (admin != null) await admin.DisposeAsync();
        _remote!.OnDisconnected();
    }

    private async Task StopAsync()
    {
        if (!await _main.ConfirmAsync("Stop the server?", "Everyone on it is disconnected.", "Stop server")) return;
        await StopNowAsync();
    }

    /// <summary>Stops without asking (closing the launcher already asked).</summary>
    public async Task StopNowAsync()
    {
        _stopRequested = true;
        Status = ServerStatus.Stopping;
        if (UseFakeServer || !_host.IsRunning)
        {
            OnServerExited();
            return;
        }
        var admin = _admin;
        await _host.StopAsync(admin == null ? null : () => admin.ShutdownAsync(), TimeSpan.FromSeconds(5));
    }

    /// <summary>Closing the launcher while it hosts leaves the server up; the next launch attaches again.</summary>
    public async Task DetachAsync()
    {
        _poll.Stop();
        await StopSharingAsync();
        if (_admin != null) await _admin.DisposeAsync();
        _admin = null;
    }

    private async void OnServerExited()
    {
        if (Status == ServerStatus.NotRunning) return;
        _poll.Stop();
        var admin = _admin;
        _admin = null;
        bool expected = _stopRequested;
        _state = null;
        Status = ServerStatus.NotRunning;
        await StopSharingAsync();
        if (admin != null) await admin.DisposeAsync();
        _host.Forget();
        _main.Setup.State.RunningServer = null;
        _main.Setup.Save();
        SetPlayers(Array.Empty<PlayerInfo>());
        MarkCurrent();
        PlaylistDirty = false;
        AddMessage(ChatKind.Admin, expected ? "Server stopped." : "The server closed.");
        // A failed start reports its own error instead.
        if (!_quietExit)
        {
            if (expected) _main.ShowNotice("Server stopped.");
            else _main.ShowNotice($"The server closed. Its log is in {_host.LogFile}.", error: true);
        }
        _main.Play.Refresh();
    }

    private async Task PollAsync()
    {
        var admin = _admin;
        if (admin == null || _polling) return;
        _polling = true;
        try
        {
            // A server elsewhere that restarted its game forgot the login, and a login whose answer
            // came too late left this one out: take it again
            if (!admin.IsLoggedIn) await admin.ConnectAsync(IsRemote ? _remotePassword : Profile.AdminPassword, CancellationToken.None);
            bool wasDown = Status == ServerStatus.NotAnswering;
            ApplyState(await admin.StateAsync());
            _lastAnswer = DateTime.UtcNow;
            if (wasDown) AddMessage(ChatKind.Admin, "The server answers again.");
            if (_pollCount++ % 2 == 0 || Tab == ControlTab.Players) SetPlayers(await admin.PlayersAsync());
            if (IsRemote) await ReadRemoteStatusAsync();
            UpdateSharing();
            _unsupportedPolls = 0;
        }
        catch (NotSupportedException e)
        {
            // An old server says so every time; one stray answer taken for a reply only once
            if (++_unsupportedPolls < 3) return;
            _poll.Stop();
            _main.ShowNotice(e.Message, error: true);
        }
        catch (Exception e) when (e is TimeoutException or RconException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // The next tick tries again; a stopped hosted server shows up through its process,
            // a server elsewhere as not answering.
            if (IsRemote && _admin == admin && Status != ServerStatus.NotAnswering && DateTime.UtcNow - _lastAnswer > NotAnsweringAfter)
            {
                Status = ServerStatus.NotAnswering;
                AddMessage(ChatKind.Admin, "The server stopped answering.");
            }
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>A server elsewhere: its launcher lobby, and (now and then) what it keeps for its next start.</summary>
    private async Task ReadRemoteStatusAsync()
    {
        var mods = _remoteMods;
        if (mods == null) return;
        try
        {
            _match = await mods.StatusAsync(CancellationToken.None);
            if (_keepsSettings && _pollCount % 10 == 1) ShowSettings(await mods.GetSettingsAsync(CancellationToken.None) ?? _remoteSettings);
        }
        catch (Exception e) when (e is HttpRequestException or ModServiceException or InvalidDataException or TaskCanceledException)
        {
            _match = null;
        }
    }

    private void ApplyState(ServerState state)
    {
        _state = state;
        if (Status != ServerStatus.Stopping)
        {
            Status = state.Phase switch
            {
                ServerPhase.Lobby => ServerStatus.Lobby,
                ServerPhase.Warmup => ServerStatus.Warmup,
                ServerPhase.Playing => ServerStatus.Playing,
                ServerPhase.Ended => ServerStatus.Results,
                _ => ServerStatus.Loading,
            };
        }
        Raise(nameof(StatusDetail));
        MarkCurrent();
    }

    private static readonly string[] EventPrefixes = { "game started: ", "lobby: ", "match started: ", "score: " };

    /// <summary>Chat is "name: text"; everything else (game over, players joining) is an event,
    /// and worth a fresh look at the state.</summary>
    private void OnPushed(string line)
    {
        bool isEvent = EventPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)) || !line.Contains(": ");
        AddMessage(isEvent ? ChatKind.Event : ChatKind.Chat, line);
        if (isEvent) _ = PollAsync();
    }

    private void OnServerLine(string line)
    {
        AddMessage(ChatKind.Log, line);
        if (_admin is { IsLoggedIn: true }) return;
        // Until the admin port answers, the log's marker lines tell the state.
        switch (ServerLog.Parse(line).Event)
        {
            case ServerLogEvent.MissionStart when Status == ServerStatus.Starting:
                Status = ServerStatus.Loading;
                break;
        }
    }

    private void Run(Func<IServerAdmin, Task> action, string done) => _ = Guard(async admin =>
    {
        await action(admin);
        AddMessage(ChatKind.Admin, done);
        await Task.Delay(300);
    }, poll: true);

    private async Task BackToLobbyAsync()
    {
        if (Status is ServerStatus.Playing or ServerStatus.Warmup &&
            !await _main.ConfirmAsync("Back to the lobby?", "This match ends now, and the next map waits in the lobby until you start it.", "Back to lobby"))
            return;
        await Guard(async admin =>
        {
            string result = await admin.LobbyAsync(now: true);
            AddMessage(ChatKind.Admin, result.Length > 0 ? char.ToUpper(result[0]) + result[1..] + "." : "Back to the lobby.");
        }, poll: true);
    }

    /// <summary>Runs an admin command, one at a time, and reports what went wrong.</summary>
    private async Task Guard(Func<IServerAdmin, Task> action, bool poll = false)
    {
        var admin = _admin;
        if (admin == null || _busy) return;
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await action(admin);
        }
        catch (Exception e) when (e is TimeoutException or RconException or NotSupportedException or System.Net.Sockets.SocketException or ObjectDisposedException
                                      or ModServiceException or HttpRequestException or InvalidDataException)
        {
            _main.ShowError(e);
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
        if (poll) await PollAsync();
    }

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
