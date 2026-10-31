using System.Text;
using System.Windows.Input;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

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

/// <summary>Play > Join a server: an address (or a recent server), then the server's mods (asked
/// once, downloaded, checked, installed), then the game opens straight into the join.</summary>
public sealed class JoinServerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly PlayViewModel _play;
    private bool _isOpen;
    private string _address = "";
    private IReadOnlyList<RecentServerItem> _recent = Array.Empty<RecentServerItem>();

    public JoinServerViewModel(MainViewModel main, PlayViewModel play)
    {
        _main = main;
        _play = play;
        OpenCommand = new RelayCommand(Open, () => _play.CanStartGame);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        JoinCommand = new RelayCommand(() => _ = JoinAsync(), () => _play.CanStartGame && !string.IsNullOrWhiteSpace(Address));
        PickCommand = new RelayCommand(p =>
        {
            if (p is RecentServerItem item) Address = item.Server.Address;
        });
        ForgetCommand = new RelayCommand(p =>
        {
            if (p is not RecentServerItem item) return;
            State.RecentServers.Remove(item.Server);
            _main.Setup.Save();
            LoadRecent();
        });
    }

    private LauncherState State => _main.Setup.State;

    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand PickCommand { get; }
    public ICommand ForgetCommand { get; }

    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public IReadOnlyList<RecentServerItem> Recent { get => _recent; private set => Set(ref _recent, value); }
    public bool HasRecent => Recent.Count > 0;

    public void Open()
    {
        LoadRecent();
        if (string.IsNullOrWhiteSpace(Address) && Recent.Count > 0) Address = Recent[0].Server.Address;
        IsOpen = true;
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
        IsOpen = false;
        string? password = State.RecentServers.FirstOrDefault(s => string.Equals(s.Address, server.ToString(), StringComparison.OrdinalIgnoreCase))?.JoinPassword;
        if (string.IsNullOrEmpty(password)) password = null;
        using var http = ModService.CreateHttp(MainViewModel.LauncherVersion);
        string title = $"Joining {server}";

        // The server's mod list, asking for the join password when it has one
        ModServiceReply reply = new(ModServiceStatus.Unavailable);
        while (true)
        {
            string? tried = password;
            var error = await _main.Activity.RunAsync(title, async (progress, ct) =>
            {
                progress.Report(new StepUpdate(title, "Asking the server for its mods"));
                reply = await ModService.GetManifestAsync(http, server, tried, ct);
            }, cancellable: true);
            if (error != null)
            {
                _main.ShowError(error, "Not joined.");
                return;
            }
            if (reply.Status != ModServiceStatus.NeedsPassword) break;
            string? answer = await _main.AskAsync(password == null ? "This server has a password" : "Wrong password",
                "Type the server's join password. The launcher keeps it with the server in your recent list.", "", "Join");
            if (string.IsNullOrWhiteSpace(answer)) return;
            password = answer.Trim();
        }

        if (reply.Status == ModServiceStatus.Ok && !await SyncAsync(server, password, reply.Manifest!, http)) return;
        if (reply.Status == ModServiceStatus.Unavailable)
            _main.ShowNotice($"{server} doesn't share its mods, so you join with the ones you have.");

        State.RememberServer(server, reply.Manifest?.Server, password);
        _main.Setup.Save();
        _play.Launch(new JoinTarget(server, password));
    }

    /// <summary>Asks once for what the server needs, then gets it. False when the player said no
    /// or it failed.</summary>
    private async Task<bool> SyncAsync(ServerAddress server, string? password, ServerModsManifest manifest, HttpClient http)
    {
        string name = string.IsNullOrWhiteSpace(manifest.Server) ? server.ToString() : manifest.Server;
        string? mine = State.InstalledVersion;
        if (!string.IsNullOrEmpty(manifest.Openswbf2) && mine != null && manifest.Openswbf2 != mine &&
            !await _main.ConfirmAsync("Another OpenSWBF2 version",
                $"{name} runs OpenSWBF2 {manifest.Openswbf2} and you have {mine}. Players need the same version to play together: " +
                "update on the Play page first, or ask the server's admin to.", "Join anyway"))
            return false;

        ModSyncPlan? plan = null;
        var hashes = FileHashCache.For(_main.Setup.Layout);
        var error = await _main.Activity.RunAsync($"Joining {server}", async (progress, ct) =>
        {
            progress.Report(new StepUpdate($"Joining {server}", "Comparing the server's mods with yours"));
            plan = await Task.Run(() => ModSyncPlan.Create(manifest, _main.Setup.Layout, hashes), ct);
        });
        if (error != null)
        {
            _main.ShowError(error);
            return false;
        }
        if (plan!.IsEmpty) return true;

        if (!await _main.ConfirmAsync("Get the server's mods?", Describe(name, plan), plan.Bytes > 0 ? $"Download {Sizes.Format(plan.Bytes)} and join" : "Switch on and join"))
            return false;

        var client = new ModSyncClient(http, _main.Setup.Layout);
        _play.WorkingHint = "The mods go on your Mods page, where you can switch them off.";
        error = await _main.Activity.RunAsync(ModSyncClient.Title, (progress, ct) => client.InstallAsync(server, password, plan, progress, ct), cancellable: true);
        _play.WorkingHint = null;
        _main.Mods.Refresh();
        if (error != null)
        {
            _main.ShowError(error, "Stopped. Join again to pick up the download where it stopped.");
            return false;
        }
        return true;
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
}
