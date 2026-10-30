using System.Windows;
using System.Windows.Threading;
using OpenBattlefront.Core;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront;

public partial class App : Application
{
    public static MainViewModel? Launcher { get; private set; }

    /// <summary>
    /// Arguments for development: --state &lt;launcher.json&gt; uses another state file (and so another
    /// install), --screenshot &lt;dir&gt; saves each page as a PNG and exits, --drop &lt;path&gt; drops a
    /// folder on the Play page first.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        string stateFile = Arg(e.Args, "--state") ?? LauncherState.DefaultFile;
        var setup = new GameSetup(LauncherState.Load(stateFile), stateFile, MainViewModel.LauncherVersion);
        Launcher = new MainViewModel(setup);
        var window = new MainWindow { DataContext = Launcher };
        MainWindow = window;
        window.Show();

        if (Arg(e.Args, "--screenshot") is { } shots)
        {
            await Screenshots.CaptureAsync(window, Launcher, shots, Arg(e.Args, "--drop"));
            Shutdown();
            return;
        }
        await Launcher.StartAsync();
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string logs = Launcher?.Setup.Layout.Logs ?? Path.GetTempPath();
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "launcher.log"), $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        }
        catch (IOException) { }
        Launcher?.ShowError(e.Exception);
        e.Handled = true;
    }
}
