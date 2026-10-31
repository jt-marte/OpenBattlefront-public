using System.Windows.Input;
using OpenBattlefront.Core;

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
/// server), by its address and admin password. Its mods next to this PC's; upload what it lacks,
/// remove what was uploaded, and restart its game to load the changes.</summary>
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

    public RemoteAdminViewModel(MainViewModel main)
    {
        _main = main;
        if (main.Setup.State.RemoteAdmin is { } saved)
        {
            _address = saved.Address;
            _password = saved.AdminPassword;
        }
        ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !Activity.IsBusy && !string.IsNullOrWhiteSpace(Address) && Password.Length > 0);
        DisconnectCommand = new RelayCommand(Disconnect);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => IsConnected && !Activity.IsBusy);
        RestartNowCommand = new RelayCommand(() => _ = RestartAsync(whenEmpty: false), () => IsConnected && !Activity.IsBusy);
        RestartWhenEmptyCommand = new RelayCommand(() => _ = RestartAsync(whenEmpty: true), () => IsConnected && !Activity.IsBusy && !RestartWaits);
        CancelRestartCommand = new RelayCommand(() => _ = CancelRestartAsync(), () => IsConnected && !Activity.IsBusy && RestartWaits);
    }

    public ActivityViewModel Activity => _main.Activity;

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
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

    private async Task ConnectAsync()
    {
        if (!ServerAddress.TryParse(Address, out var server))
        {
            _main.ShowNotice("That isn't a server address. Type it as address:port, such as 203.0.113.5:3658.", error: true);
            return;
        }
        var client = new ModUploadClient(_http, server, Password);
        ServerModsManifest? manifest = null;
        var error = await Activity.RunAsync($"Connecting to {server}", async (_, ct) => manifest = await client.GetManifestAsync(ct), cancellable: true);
        if (error != null)
        {
            _main.ShowError(error);
            return;
        }
        _client = client;
        _main.Setup.State.RemoteAdmin = new RemoteAdminProfile { Address = server.ToString(), AdminPassword = Password };
        _main.Setup.Save();
        IsConnected = true;
        await ShowAsync(manifest!);
    }

    private void Disconnect()
    {
        _client = null;
        IsConnected = false;
        ServerMods = Array.Empty<RemoteModItem>();
        LocalMods = Array.Empty<LocalModItem>();
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

    /// <summary>For screenshots: a server with the included content and an upload waiting.</summary>
    internal void ShowSample()
    {
        _client = new ModUploadClient(_http, new ServerAddress("203.0.113.5", 3658), "sample");
        IsConnected = true;
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
