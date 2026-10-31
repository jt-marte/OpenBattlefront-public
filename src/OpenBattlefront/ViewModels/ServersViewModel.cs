using System.Windows;
using System.Windows.Input;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

public enum ServerScreen { Landing, Options, Control, Remote }

/// <summary>The Server manager page: host a server (options, then the playlist and controls), or
/// connect to one running elsewhere as its admin (its mods, for now).</summary>
public sealed class ServersViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ServerScreen _screen;

    public ServersViewModel(MainViewModel main)
    {
        _main = main;
        Options = new ServerOptionsViewModel(main, live: false);
        Control = new ServerControlViewModel(main, this);
        Remote = new RemoteAdminViewModel(main);
        HostCommand = new RelayCommand(() => Screen = Control.IsRunning ? ServerScreen.Control : ServerScreen.Options);
        ConnectCommand = new RelayCommand(() => Screen = ServerScreen.Remote);
        BackCommand = new RelayCommand(GoBack);
        ContinueCommand = new RelayCommand(Continue);
    }

    public ServerOptionsViewModel Options { get; }
    public ServerControlViewModel Control { get; }
    public RemoteAdminViewModel Remote { get; }

    public ICommand HostCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ContinueCommand { get; }

    public ServerScreen Screen
    {
        get => _screen;
        set
        {
            if (!Set(ref _screen, value)) return;
            if (value == ServerScreen.Control) Control.Refresh();
            if (value == ServerScreen.Options) Options.Refresh();
        }
    }

    /// <summary>Called when the page opens: a running server goes straight to its controls.</summary>
    public void Refresh()
    {
        if (Control.IsRunning) Screen = ServerScreen.Control;
        Options.Refresh();
    }

    private void GoBack()
    {
        Screen = Screen == ServerScreen.Control && !Control.IsRunning ? ServerScreen.Options : ServerScreen.Landing;
    }

    private void Continue()
    {
        _main.Setup.State.Server.Normalize();
        _main.Setup.Save();
        Screen = ServerScreen.Control;
    }
}

/// <summary>The host settings, on the options screen and (game options only, applied from the
/// next map) on the running server's Options tab.</summary>
public sealed class ServerOptionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _showAdminPassword;
    private bool _isDirty;
    private bool _serverLocked;

    public ServerOptionsViewModel(MainViewModel main, bool live)
    {
        _main = main;
        IsLive = live;
        ToggleAdminPasswordCommand = new RelayCommand(() => ShowAdminPassword = !ShowAdminPassword);
        CopyAdminPasswordCommand = new RelayCommand(() =>
        {
            try
            {
                Clipboard.SetText(Profile.AdminPassword);
                _main.ShowNotice("Admin password copied.");
            }
            catch (System.Runtime.InteropServices.COMException) { }
        });
        NewAdminPasswordCommand = new RelayCommand(() =>
        {
            Profile.AdminPassword = ServerProfile.NewAdminPassword();
            Changed(nameof(AdminPasswordText));
        }, () => !ServerLocked);
    }

    private ServerProfile Profile => _main.Setup.State.Server;

    /// <summary>The running server's Options tab: changes wait for "Apply options".</summary>
    public bool IsLive { get; }

    /// <summary>Settings fixed while the server runs (name, port, tick rate…).</summary>
    public bool ServerLocked
    {
        get => _serverLocked;
        set
        {
            if (Set(ref _serverLocked, value)) Raise(nameof(ServerEditable));
        }
    }

    public bool ServerEditable => !ServerLocked;

    /// <summary>Game options changed since they were last sent to the running server.</summary>
    public bool IsDirty { get => _isDirty; set => Set(ref _isDirty, value); }

    public ICommand ToggleAdminPasswordCommand { get; }
    public ICommand CopyAdminPasswordCommand { get; }
    public ICommand NewAdminPasswordCommand { get; }

    public void Refresh()
    {
        foreach (var property in GetType().GetProperties()) Raise(property.Name);
    }

    private void Changed(string name, bool gameOption = false)
    {
        Raise(name);
        _main.Setup.Save();
        if (gameOption && IsLive) IsDirty = true;
        if (gameOption) _main.Servers?.Control.OptionsChanged(this);
    }

    // Server

    public string Name
    {
        get => Profile.Name;
        set
        {
            Profile.Name = value;
            Profile.Normalize();
            Changed(nameof(Name));
        }
    }

    public string JoinPassword
    {
        get => Profile.JoinPassword;
        set
        {
            Profile.JoinPassword = value;
            Profile.Normalize();
            Changed(nameof(JoinPassword));
        }
    }

    public int Port
    {
        get => Profile.Port;
        set
        {
            Profile.Port = Math.Clamp(value, 1024, 65534);
            Changed(nameof(Port));
        }
    }

    public int MaxPlayers
    {
        get => Profile.MaxPlayers;
        set
        {
            Profile.MaxPlayers = Math.Clamp(value, 2, 64);
            Changed(nameof(MaxPlayers));
        }
    }

    /// <summary>"30", "60" or "120" for the segmented choice.</summary>
    public string Tick
    {
        get => Profile.TickRate.ToString();
        set
        {
            if (!int.TryParse(value, out int rate) || !ServerProfile.TickRates.Contains(rate)) return;
            Profile.TickRate = rate;
            Changed(nameof(Tick));
        }
    }

    public bool ShowAdminPassword
    {
        get => _showAdminPassword;
        set
        {
            if (Set(ref _showAdminPassword, value)) Raise(nameof(AdminPasswordText));
        }
    }

    public string AdminPasswordText => ShowAdminPassword ? Profile.AdminPassword : new string('•', Profile.AdminPassword.Length);

    // Battle

    public int BotsPerTeam
    {
        get => Profile.BotsPerTeam;
        set
        {
            Profile.BotsPerTeam = Math.Clamp(value, 0, 32);
            Changed(nameof(BotsPerTeam), gameOption: true);
        }
    }

    /// <summary>"2" Medium, "3" Hard, "4" Overhaul.</summary>
    public string Difficulty
    {
        get => Profile.Difficulty.ToString();
        set
        {
            if (!int.TryParse(value, out int level)) return;
            Profile.Difficulty = Math.Clamp(level, 2, 4);
            Changed(nameof(Difficulty), gameOption: true);
        }
    }

    public bool TeamDamage
    {
        get => Profile.TeamDamage;
        set
        {
            Profile.TeamDamage = value;
            Changed(nameof(TeamDamage), gameOption: true);
        }
    }

    public bool ShowNames
    {
        get => Profile.ShowNames;
        set
        {
            Profile.ShowNames = value;
            Changed(nameof(ShowNames), gameOption: true);
        }
    }

    public bool AutoAssignTeams
    {
        get => Profile.AutoAssignTeams;
        set
        {
            Profile.AutoAssignTeams = value;
            Changed(nameof(AutoAssignTeams), gameOption: true);
        }
    }

    public bool Awards
    {
        get => Profile.Awards;
        set
        {
            Profile.Awards = value;
            Changed(nameof(Awards), gameOption: true);
        }
    }

    // Heroes

    public bool Heroes
    {
        get => Profile.Heroes;
        set
        {
            Profile.Heroes = value;
            Changed(nameof(Heroes), gameOption: true);
        }
    }

    public bool AiHeroes
    {
        get => Profile.AiHeroes;
        set
        {
            Profile.AiHeroes = value;
            Changed(nameof(AiHeroes), gameOption: true);
        }
    }

    /// <summary>"points" or "timer", as Instant Action offers them.</summary>
    public string HeroUnlock
    {
        get => Profile.HeroUnlock == ServerProfile.HeroUnlockTimer ? "timer" : "points";
        set
        {
            int unlock = value == "timer" ? ServerProfile.HeroUnlockTimer : ServerProfile.HeroUnlockPoints;
            if (unlock == Profile.HeroUnlock) return;
            Profile.HeroUnlock = unlock;
            Profile.HeroUnlockValue = unlock == ServerProfile.HeroUnlockTimer ? 60 : 10;
            Changed(nameof(HeroUnlock), gameOption: true);
            Raise(nameof(HeroUnlockValue));
            Raise(nameof(HeroUnlockUnit));
        }
    }

    public int HeroUnlockValue
    {
        get => Profile.HeroUnlockValue;
        set
        {
            Profile.HeroUnlockValue = Profile.HeroUnlock == ServerProfile.HeroUnlockTimer ? Math.Clamp(value, 0, 120) : Math.Clamp(value, 1, 50);
            Changed(nameof(HeroUnlockValue), gameOption: true);
        }
    }

    public string HeroUnlockUnit => Profile.HeroUnlock == ServerProfile.HeroUnlockTimer ? "seconds (0-120)" : "points (1-50)";

    /// <summary>"best" or "random".</summary>
    public string HeroPlayer
    {
        get => Profile.HeroPlayer == ServerProfile.HeroPlayerRandom ? "random" : "best";
        set
        {
            Profile.HeroPlayer = value == "random" ? ServerProfile.HeroPlayerRandom : ServerProfile.HeroPlayerBest;
            Changed(nameof(HeroPlayer), gameOption: true);
        }
    }

    // Per mode

    public int ConquestReinforcements
    {
        get => Profile.ConquestReinforcements;
        set
        {
            Profile.ConquestReinforcements = Math.Clamp(value, 10, 500);
            Changed(nameof(ConquestReinforcements), gameOption: true);
        }
    }

    public int ConquestTimeLimit
    {
        get => Profile.ConquestTimeLimit;
        set
        {
            Profile.ConquestTimeLimit = Math.Clamp(value, 0, 60);
            Changed(nameof(ConquestTimeLimit), gameOption: true);
        }
    }

    public int CtfCaptures
    {
        get => Profile.CtfCaptures;
        set
        {
            Profile.CtfCaptures = Math.Clamp(value, 1, 15);
            Changed(nameof(CtfCaptures), gameOption: true);
        }
    }

    public int CtfTimeLimit
    {
        get => Profile.CtfTimeLimit;
        set
        {
            Profile.CtfTimeLimit = Math.Clamp(value, 0, 60);
            Changed(nameof(CtfTimeLimit), gameOption: true);
        }
    }

    public int HuntTimeLimit
    {
        get => Profile.HuntTimeLimit;
        set
        {
            Profile.HuntTimeLimit = Math.Clamp(value, 0, 60);
            Changed(nameof(HuntTimeLimit), gameOption: true);
        }
    }

    public int HuntScore
    {
        get => Profile.HuntScore;
        set
        {
            Profile.HuntScore = Math.Clamp(value, 50, 150);
            Changed(nameof(HuntScore), gameOption: true);
        }
    }

    public int AssaultScore
    {
        get => Profile.AssaultScore;
        set
        {
            Profile.AssaultScore = Math.Clamp(value, 150, 300);
            Changed(nameof(AssaultScore), gameOption: true);
        }
    }
}
