using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenBattlefront.Core;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront;

public partial class App : Application
{
    private static Mutex? _single;

    public static MainViewModel? Launcher { get; private set; }

    /// <summary>
    /// Arguments for development: --state &lt;launcher.json&gt; uses another state file (and so another
    /// install), --screenshot &lt;dir&gt; saves each page as a PNG and exits, --drop &lt;path&gt; drops a
    /// folder on the Play page first, --fake-server runs the Server manager against a simulated
    /// server and the sample map list.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // One launcher at a time: starting it again brings the open one forward.
        _single = new Mutex(true, "OpenBattlefront.Launcher", out bool first);
        if (!first && Arg(e.Args, "--screenshot") == null)
        {
            ShowRunningLauncher();
            Shutdown();
            return;
        }

        string stateFile = Arg(e.Args, "--state") ?? LauncherState.DefaultFile;
        var setup = new GameSetup(LauncherState.Load(stateFile), stateFile, MainViewModel.LauncherVersion);
        Launcher = new MainViewModel(setup);
        if (e.Args.Any(a => a.Equals("--fake-server", StringComparison.OrdinalIgnoreCase))) Launcher.Servers.Control.UseFakeServer = true;
        var window = new MainWindow { DataContext = Launcher };
        MainWindow = window;
        window.Show();

        if (Arg(e.Args, "--screenshot") is { } shots)
        {
            setup.SavingEnabled = false;
            await Screenshots.CaptureAsync(window, Launcher, shots, Arg(e.Args, "--drop"));
            Shutdown();
            return;
        }
        await Launcher.StartAsync();
    }

    private static void ShowRunningLauncher()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id == self.Id || other.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

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
