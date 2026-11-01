using System.Net;
using System.Net.Sockets;
using System.Windows.Input;
using OpenBattlefront.Core;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.ViewModels;

/// <summary>A mod the server has.</summary>
public sealed class RemoteModItem
{
    public RemoteModItem(string name, long size, bool uploaded, string? pending, ICommand remove)
    {
        Name = name;
        SizeText = Sizes.Format(size);
        IsUploaded = uploaded;
        Pending = pending;
        RemoveCommand = remove;
    }

    public string Name { get; }
    public string SizeText { get; }
    public bool IsUploaded { get; }

    /// <summary>"Loads after a restart", "Unloads after a restart", or null.</summary>
    public string? Pending { get; }
    public bool HasPending => Pending != null;
    public string SourceText => IsUploaded ? "Uploaded" : "Server's own files";

    /// <summary>Only uploaded mods can go; the server's own are changed on its host.</summary>
    public bool CanRemove => IsUploaded && Pending != RemoteAdminViewModel.Unloads;
    public ICommand RemoveCommand { get; }
}

/// <summary>A mod of this PC, against the server's copy.</summary>
public sealed class LocalModItem
{
    public LocalModItem(string name, ModUpload? upload, string state, bool canUpload, ICommand uploadCommand)
    {
        Name = name;
        Upload = upload;
        StateText = state;
        CanUpload = canUpload;
        UploadCommand = uploadCommand;
    }

    public string Name { get; }
    public ModUpload? Upload { get; }
    public string SizeText => Upload == null ? "" : Sizes.Format(Upload.Size);
    public string StateText { get; }
    public bool CanUpload { get; }
    public ICommand UploadCommand { get; }
}

/// <summary>Server manager > Connect to server as admin: a server running elsewhere (the Docker
/// server, say), by its address and admin password. Once in, <see cref="Control"/> is the same
/// playlist and control screen as hosting has, over rcon; this keeps its Mods tab: the server's
/// mods next to this PC's, uploads, removing uploaded ones, and restarting its game.</summary>
public sealed class RemoteAdminViewModel : ObservableObject
{
    public const string Loads = "Loads after a restart";
    public const string Unloads = "Unloads after a restart";

    private readonly MainViewModel _main;
    private readonly HttpClient _http = ModService.CreateHttp(MainViewModel.LauncherVersion);
    private ModUploadClient? _client;
    private string _address = "";
    private string _password = "";
    private bool _isConnected;
    private string _serverName = "";
    private string _serverDetail = "";
    private bool _restartWaits;
    private int _pendingCount;
    private bool _uploadsOn = true;
    private IReadOnlyList<RemoteModItem> _serverMods = Array.Empty<RemoteModItem>();
    private IReadOnlyList<LocalModItem> _localMods = Array.Empty<LocalModItem>();

    public RemoteAdminViewModel(MainViewModel main, ServersViewModel servers)
    {
        _main = main;
        if (main.Setup.State.RemoteAdmin is { } saved)
        {
            _address = saved.Address;
            _password = saved.AdminPassword;
        }
        Control = new ServerControlViewModel(main, servers, this);
        ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !Activity.IsBusy && !string.IsNullOrWhiteSpace(Address) && Password.Length > 0);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => HasMods && !Activity.IsBusy);
        RestartNowCommand = new RelayCommand(() => _ = RestartAsync(whenEmpty: false), () => HasMods && !Activity.IsBusy);
        RestartWhenEmptyCommand = new RelayCommand(() => _ = RestartAsync(whenEmpty: true), () => HasMods && !Activity.IsBusy && !RestartWaits);
        CancelRestartCommand = new RelayCommand(() => _ = CancelRestartAsync(), () => HasMods && !Activity.IsBusy && RestartWaits);
    }

    /// <summary>The playlist and control screen for the server, once connected.</summary>
    public ServerControlViewModel Control { get; }

    /// <summary>The server has a mod service: its mods, uploads and restarts.</summary>
    public bool HasMods => _client != null;

    public ActivityViewModel Activity => _main.Activity;

    public ICommand ConnectCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RestartNowCommand { get; }
    public ICommand RestartWhenEmptyCommand { get; }
    public ICommand CancelRestartCommand { get; }

    public string Address { get => _address; set => Set(ref _address, value); }
    public string Password { get => _password; set => Set(ref _password, value); }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value)) Raise(nameof(IsDisconnected));
        }
    }

    public bool IsDisconnected => !IsConnected;
    public string ServerName { get => _serverName; private set => Set(ref _serverName, value); }
    public string ServerDetail { get => _serverDetail; private set => Set(ref _serverDetail, value); }
    public IReadOnlyList<RemoteModItem> ServerMods { get => _serverMods; private set => Set(ref _serverMods, value); }
    public IReadOnlyList<LocalModItem> LocalMods { get => _localMods; private set => Set(ref _localMods, value); }

    public bool RestartWaits
    {
        get => _restartWaits;
        private set
        {
            if (Set(ref _restartWaits, value)) Raise(nameof(RestartText));
        }
    }

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (Set(ref _pendingCount, value)) Raise(nameof(RestartText));
        }
    }

    public string RestartText =>
        RestartWaits ? "The game restarts once nobody is on." :
        PendingCount == 1 ? "1 change loads when the game restarts." :
        PendingCount > 1 ? $"{PendingCount} changes load when the game restarts." :
        "Mods load when the game starts.";

    /// <summary>Its mod service first (a wrong password stops there; no service means rcon only),
    /// then the admin login over rcon on the game port, then what it runs: rotation, options,
    /// name and limits, and what its service keeps for the next start.</summary>
    private async Task ConnectAsync()
    {
        if (!ServerAddress.TryParse(Address, out var server))
        {
            _main.ShowNotice("That isn't a server address. Type it as address:port, such as 203.0.113.5:3658.", error: true);
            return;
        }
        string password = Password;
        var client = new ModUploadClient(_http, server, password);
        ServerModsManifest? manifest = null;
        RconClient? rcon = null;
        ServerProfile? profile = null;
        AdminSettingsReply? settings = null;
        string title = $"Connecting to {server}";
        var error = await Activity.RunAsync(title, async (progress, ct) =>
        {
            progress.Report(new StepUpdate(title, "Asking its mod service"));
            try { manifest = await client.GetManifestAsync(ct); }
            catch (ModServiceException e) when (e.Status == null) { manifest = null; }
            progress.Report(new StepUpdate(title, "Logging in as admin"));
            rcon = new RconClient(await ResolveAsync(server, ct));
            try { await rcon.ConnectAsync(password, ct); }
            catch (TimeoutException e)
            {
                throw new TimeoutException($"{server} didn't answer as admin on UDP {server.Port}. Is the server up, with UDP {server.Port} forwarded to it?", e);
            }
            progress.Report(new StepUpdate(title, "Reading its playlist and options"));
            profile = await ServerReader.ReadAsync(rcon, ct);
            if (manifest?.Settings == true) settings = await client.GetSettingsAsync(ct);
        }, cancellable: true);
        if (error != null)
        {
            if (rcon != null) await rcon.DisposeAsync();
            _main.ShowError(error);
            return;
        }
        _client = manifest != null ? client : null;
        _main.Setup.State.RemoteAdmin = new RemoteAdminProfile { Address = server.ToString(), AdminPassword = password };
        _main.Setup.Save();
        IsConnected = true;
        Raise(nameof(HasMods));
        ServerName = profile!.Name;
        await Control.AttachRemoteAsync(server, password, rcon!, _client, manifest?.Settings == true, profile, settings);
        if (manifest != null) await ShowAsync(manifest);
    }

    /// <summary>The game speaks IPv4 only.</summary>
    private static async Task<IPEndPoint> ResolveAsync(ServerAddress server, CancellationToken ct)
    {
        if (IPAddress.TryParse(server.Host, out var ip)) return new IPEndPoint(ip, server.Port);
        var found = await Dns.GetHostAddressesAsync(server.Host, ct);
        var v4 = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(v4, server.Port);
    }

    /// <summary>The control screen logged out: back to the address and password.</summary>
    internal void OnDisconnected()
    {
        _client = null;
        IsConnected = false;
        Raise(nameof(HasMods));
        ServerMods = Array.Empty<RemoteModItem>();
        LocalMods = Array.Empty<LocalModItem>();
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task RefreshAsync()
    {
        if (_client == null) return;
        ServerModsManifest? manifest = null;
        var error = await Activity.RunAsync($"Reading {_client.Server}'s mods", async (_, ct) => manifest = await _client.GetManifestAsync(ct), cancellable: true);
        if (error != null) _main.ShowError(error);
        else await ShowAsync(manifest!);
    }

    /// <summary>The server's mods, and this PC's against them (hashing them, once, takes a moment).</summary>
    private async Task ShowAsync(ServerModsManifest manifest)
    {
        var layout = _main.Setup.Layout;
        var hashes = FileHashCache.For(layout);
        var dirs = new ModManager(layout).List().Where(m => m.Enabled && !m.IsPatch).ToList();
        var uploads = new Dictionary<string, (ModUpload? Upload, string? Problem)>();
        var error = await Activity.RunAsync("Comparing your mods with the server's", async (progress, ct) =>
        {
            foreach (var mod in dirs)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new StepUpdate("Comparing your mods with the server's", mod.Name));
                await Task.Run(() =>
                {
                    try { uploads[mod.Name] = (ModUpload.Prepare(mod.Path, hashes), null); }
                    catch (Exception e) when (e is InvalidDataException or IOException) { uploads[mod.Name] = (null, e.Message); }
                }, ct);
            }
        }, cancellable: true);
        if (error != null)
        {
            _main.ShowError(error);
            return;
        }
        Show(manifest, uploads);
    }

    internal void Show(ServerModsManifest manifest, IReadOnlyDictionary<string, (ModUpload? Upload, string? Problem)> local)
    {
        var pending = manifest.Pending ?? new List<PendingModChange>();
        string? PendingFor(string name) => pending.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Action switch
        {
            "add" => Loads,
            "remove" => Unloads,
            _ => null,
        };

        var serverMods = manifest.Folders
            .Select(f => new RemoteModItem(f.Name, f.Size, f.IsUploaded, PendingFor(f.Name), RemoveCommandFor(f.Name)))
            .Concat(pending.Where(p => p.Action == "add" && !manifest.Folders.Any(f => string.Equals(f.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new RemoteModItem(p.Name, p.Size, true, Loads, RemoveCommandFor(p.Name))))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var localMods = new List<LocalModItem>();
        foreach (var (name, (upload, problem)) in local.OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (upload == null)
            {
                localMods.Add(new LocalModItem(name, null, problem ?? "Can't be uploaded", false, NoCommand));
                continue;
            }
            var theirs = manifest.Folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            string? waiting = PendingFor(name);
            (string state, bool can) = waiting == Loads ? ("Uploaded; " + Loads.ToLowerInvariant(), false) : ModCompare.Compare(upload, manifest) switch
            {
                ServerModState.Same => ("Same as the server's", false),
                ServerModState.Different when theirs?.IsUploaded == false => ("Different on the server, which has its own copy", false),
                ServerModState.Different => ("Different on the server", true),
                _ => ("Not on the server", true),
            };
            if (can && !manifest.Uploads) (state, can) = (state + "; the server takes no uploads", false);
            if (can && upload.LeftOut.Count > 0) state += $"; {upload.LeftOut.Count} program file(s) stay behind";
            localMods.Add(new LocalModItem(name, upload, state, can, UploadCommandFor(upload)));
        }

        ServerName = string.IsNullOrWhiteSpace(manifest.Server) ? _client?.Server.ToString() ?? "" : manifest.Server;
        ServerDetail = $"{_client?.Server}  ·  OpenSWBF2 {manifest.Openswbf2}  ·  {manifest.Folders.Count} mods";
        _uploadsOn = manifest.Uploads;
        ServerMods = serverMods;
        LocalMods = localMods;
        PendingCount = pending.Count;
        RestartWaits = manifest.Restart == "when-empty";
        CommandManager.InvalidateRequerySuggested();
    }

    private static readonly ICommand NoCommand = new RelayCommand(() => { }, () => false);

    private ICommand UploadCommandFor(ModUpload upload) => new RelayCommand(() => _ = UploadAsync(upload), () => !Activity.IsBusy && _uploadsOn);

    private ICommand RemoveCommandFor(string name) => new RelayCommand(() => _ = RemoveAsync(name), () => !Activity.IsBusy);

    private async Task UploadAsync(ModUpload upload)
    {
        if (_client == null) return;
        string programs = upload.LeftOut.Count == 0 ? "" : $" {upload.LeftOut.Count} program file(s) in it stay behind; servers never take programs.";
        if (!await _main.ConfirmAsync($"Upload {upload.Folder}?",
                $"{Sizes.Format(upload.Size)} to {ServerName}. It loads when the game there restarts, and players who join get it.{programs}", "Upload"))
            return;
        var error = await Activity.RunAsync($"Uploading {upload.Folder}", (progress, ct) => _client.UploadAsync(upload, progress, ct), cancellable: true);
        if (error != null)
        {
            _main.ShowError(error, "Upload stopped. Upload again to send the rest.");
            return;
        }
        _main.ShowNotice($"{upload.Folder} is on the server. Restart its game to load it.");
        await RefreshAsync();
    }

    private async Task RemoveAsync(string name)
    {
        if (_client == null) return;
        if (!await _main.ConfirmAsync($"Remove {name} from the server?",
                "It unloads when the game there restarts. Players keep their own copies.", "Remove"))
            return;
        var error = await Activity.RunAsync($"Removing {name}", (_, ct) => _client.RemoveAsync(name, ct));
        if (error != null) _main.ShowError(error);
        else await RefreshAsync();
    }

    private async Task RestartAsync(bool whenEmpty)
    {
        if (_client == null) return;
        if (!whenEmpty && !await _main.ConfirmAsync("Restart the server's game now?",
                "Everyone on it is dropped and can rejoin in about a minute. Mod changes waiting for a restart load.", "Restart now"))
            return;
        string state = "none";
        var error = await Activity.RunAsync("Restarting the server's game", async (_, ct) => state = await _client.RestartAsync(whenEmpty, ct));
        if (error != null)
        {
            _main.ShowError(error);
            return;
        }
        RestartWaits = state == "when-empty";
        if (whenEmpty) _main.ShowNotice("The server's game restarts once nobody is on.");
        else
        {
            PendingCount = 0;
            _main.ShowNotice("The server's game is restarting. Refresh in a minute to see its mods.");
        }
    }

    private async Task CancelRestartAsync()
    {
        if (_client == null) return;
        string state = "none";
        var error = await Activity.RunAsync("Restarting the server's game", async (_, ct) => state = await _client.CancelRestartAsync(ct));
        if (error != null) _main.ShowError(error);
        else RestartWaits = state == "when-empty";
    }

    /// <summary>For screenshots: the control screen on a pretend server, with launchers waiting,
    /// the included content and an upload waiting. Nothing goes over the network.</summary>
    internal async Task ShowSampleAsync()
    {
        var server = new ServerAddress("203.0.113.5", 3658);
        var catalog = SampleCatalog.Load();
        var profile = new ServerProfile { Name = "Friday Night Battles", MaxPlayers = 32, Difficulty = 4 };
        profile.Playlist = new[] { "BS1g_con", "geo1c_con", "kas2g_ctf", "hot1g_hunt", "spa3c_ass" }
            .Where(catalog.Contains).Select(m => new PlaylistEntry { Mission = m }).ToList();
        var admin = new FakeServerAdmin(catalog, profile);
        await admin.ConnectAsync("sample", CancellationToken.None);
        IsConnected = true;
        Control.UseFakeServer = true;
        await Control.AttachRemoteAsync(server, "sample", admin, null, keepsSettings: false, profile, null);
        _client = new ModUploadClient(_http, server, "sample");
        Raise(nameof(HasMods));
        Control.ShowSampleLobby(_client, new MatchStatus
        {
            Schema = 1, Phase = "lobby", Map = "BS1g_con", Players = 1,
            Lobby = { new LobbyPlayer("Pat", "waiting", 100), new LobbyPlayer("Cody", "waiting", 100), new LobbyPlayer("Echo", "downloading", 45) },
        });
        var manifest = new ServerModsManifest
        {
            Schema = 1, Server = "Friday Night Battles", Openswbf2 = "0.2.2", Uploads = true,
            Folders =
            {
                new ServerModFolder { Name = "BF1", Size = 644_000_000, Source = "server", Files = { new ManifestFile { Path = "addme.script", Size = 644_000_000, Sha256 = new string('a', 64) } } },
                new ServerModFolder { Name = "DLC_XBOX", Size = 221_000_000, Source = "server" },
                new ServerModFolder { Name = "KOTOR", Size = 96_000_000, Source = "uploaded" },
            },
            Pending = new List<PendingModChange> { new() { Name = "Endor", Action = "add", Size = 48_500_000 } },
        };
        ModUpload Sample(string name, long size) => new(name, new[] { new LocalModFile("addme.script", size, new string('a', 64), "") }, Array.Empty<string>());
        Show(manifest, new Dictionary<string, (ModUpload?, string?)>
        {
            ["BF1"] = (Sample("BF1", 644_000_000), null),
            ["Endor"] = (Sample("Endor", 48_500_000), null),
            ["Geonosis_Plus"] = (Sample("Geonosis_Plus", 132_000_000), null),
        });
    }
}
