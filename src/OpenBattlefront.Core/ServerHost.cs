using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBattlefront.Core;

/// <summary>A server the launcher started, saved so the next launch can attach to it again.</summary>
public sealed record ServerSession(int Pid, DateTime StartedUtc, int Port);

/// <summary>The dedicated server's process, real or (in tests) fake.</summary>
public interface IServerProcess : IDisposable
{
    int Id { get; }
    DateTime StartTimeUtc { get; }
    bool HasExited { get; }

    /// <summary>A line of the server's stdout or stderr (none for a server attached to later).</summary>
    event Action<string>? Output;
    event Action? Exited;

    Task<bool> WaitForExitAsync(TimeSpan timeout);

    /// <summary>Posts WM_CLOSE to the server's message window. False when it has none.</summary>
    bool CloseWindow();
    void Kill();
}

/// <summary>
/// Hosts a dedicated server from the launcher's game copy: writes its settings folder, starts
/// BattlefrontII.exe -dedicated with the playlist and a lobby, keeps the process (the game's
/// own exe, so it is tracked by process, not by path) and its log, and stops it cleanly.
/// </summary>
public sealed class ServerHost
{
    public delegate IServerProcess Starter(ProcessStartInfo info);

    public const string WindowClass = "Star Wars Battlefront II Dedicated";
    public const string LogCommands = "all Warning, LogNet Inform, LogCore Inform";
    public const int LogLimit = 2000;

    /// <summary>The game copies its command line into a 4096-character buffer; keep the maps
    /// well inside it. The rest are sent over rcon once the server is up.</summary>
    public const int MapArgumentBudget = 2800;

    private readonly InstallLayout _layout;
    private readonly Starter _start;
    private readonly LinkedList<string> _log = new();

    public ServerHost(InstallLayout layout, Starter? start = null)
    {
        _layout = layout;
        _start = start ?? SystemServerProcess.Start;
    }

    public IServerProcess? Process { get; private set; }
    public bool IsRunning => Process is { HasExited: false };
    public string LogFile => Path.Combine(_layout.Logs, "server.log");

    /// <summary>The maps the last start put on the command line; the playlist's others still
    /// need sending.</summary>
    public int MapsOnCommandLine { get; private set; }

    public event Action<string>? LineReceived;
    public event Action? Exited;

    public IReadOnlyList<string> Log
    {
        get { lock (_log) return _log.ToList(); }
    }

    public static string SettingsFile(InstallLayout layout) => Path.Combine(layout.ServerSettings, "ServerSettings.cfg");
    public static string RotationFile(InstallLayout layout) => Path.Combine(layout.ServerSettings, "ServerRotation.cfg");

    /// <summary>ServerSettings.cfg gets the game options. ServerRotation.cfg is written empty: the
    /// maps go on the command line, where the tickets stick (an /addmap read at start drops them).</summary>
    public void WriteConfig(ServerProfile profile)
    {
        Directory.CreateDirectory(_layout.ServerSettings);
        File.WriteAllLines(SettingsFile(_layout), profile.SettingsLines(), Encoding.ASCII);
        File.WriteAllText(RotationFile(_layout), "", Encoding.ASCII);
    }

    /// <param name="loopback">Only this PC can join (-netloopback): for tests, so Windows doesn't
    /// ask about the firewall.</param>
    public static string BuildArguments(ServerProfile profile, InstallLayout layout, out int mapsOnCommandLine, bool loopback = false)
    {
        if (profile.Playlist.Count == 0) throw new InvalidOperationException("Add a map to the playlist first.");
        var maps = new StringBuilder();
        mapsOnCommandLine = 0;
        foreach (var entry in profile.Playlist)
        {
            string argument = entry.ToArgument();
            if (mapsOnCommandLine > 0 && maps.Length + argument.Length + 1 > MapArgumentBudget) break;
            if (maps.Length > 0) maps.Append(' ');
            maps.Append(argument);
            mapsOnCommandLine++;
        }

        int tickRate = ServerProfile.TickRates.Contains(profile.TickRate) ? profile.TickRate : 60;
        var parts = new List<string>
        {
            "-dedicated", maps.ToString(), "-lobby",
            "/configdir", Quote(layout.ServerSettings),
            "/gamename", Quote(profile.Name),
        };
        if (profile.JoinPassword.Length > 0) parts.AddRange(new[] { "/password", Quote(profile.JoinPassword) });
        parts.AddRange(new[]
        {
            "/adminpw", profile.AdminPassword,
            "/playerlimit", profile.MaxPlayers.ToString(),
            // Room for 64 soldiers a team, 128 in all (OpenSWBF2 0.2.6+, its players too); each
            // mode's bots a team (ServerSettings.cfg) caps how many spawn
            "/bots", ServerProfile.MaxBots.ToString(),
            "/tps", tickRate.ToString(),
            "-port", profile.Port.ToString(),
            "-logcmds", Quote(LogCommands),
            "-log", Quote(Path.Combine(layout.Logs, "server.log")),
        });
        if (loopback) parts.Add("-netloopback");
        return string.Join(' ', parts);
    }

    /// <summary>The --loopback development switch: the server and its mod service answer only this PC.</summary>
    public bool Loopback { get; set; }

    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";

    public IServerProcess Start(ServerProfile profile)
    {
        if (IsRunning) throw new InvalidOperationException("The server is already running.");
        if (!File.Exists(_layout.GameExe)) throw new FileNotFoundException("The game is not installed yet.", _layout.GameExe);
        profile.Normalize();
        string arguments = BuildArguments(profile, _layout, out int maps, Loopback);
        WriteConfig(profile);
        Directory.CreateDirectory(_layout.Logs);
        lock (_log) _log.Clear();

        var info = new ProcessStartInfo(_layout.GameExe, arguments)
        {
            WorkingDirectory = _layout.Game,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = _start(info);
        MapsOnCommandLine = maps;
        Track(process);
        return process;
    }

    /// <summary>Attaches to a server left running by an earlier launch, if that process is still
    /// the same game server (same id and start time, our exe).</summary>
    public bool TryAttach(ServerSession session)
    {
        if (IsRunning) return true;
        var process = SystemServerProcess.TryAttach(session, _layout.GameExe);
        if (process == null) return false;
        lock (_log) _log.Clear();
        MapsOnCommandLine = 0;
        Track(process);
        return true;
    }

    private void Track(IServerProcess process)
    {
        Process = process;
        process.Output += line =>
        {
            lock (_log)
            {
                _log.AddLast(line);
                while (_log.Count > LogLimit) _log.RemoveFirst();
            }
            LineReceived?.Invoke(line);
        };
        process.Exited += () => Exited?.Invoke();
    }

    /// <summary>WM_CLOSE to the server's window first: the dedicated server's own clean stop, done
    /// in a frame. Then /shutdown over rcon (when given), then kill it. Each step waits
    /// <paramref name="grace"/>. (/shutdown alone left a server in its lobby running, 2026-10-30.)</summary>
    public async Task StopAsync(Func<Task>? shutdown, TimeSpan grace)
    {
        var process = Process;
        if (process == null || process.HasExited) return;
        if (process.CloseWindow() && await process.WaitForExitAsync(grace)) return;
        if (shutdown != null)
        {
            try
            {
                await shutdown();
                if (await process.WaitForExitAsync(grace)) return;
            }
            catch (Exception e) when (e is TimeoutException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                // Fall through to killing it.
            }
        }
        process.Kill();
        await process.WaitForExitAsync(grace);
    }

    public void Forget()
    {
        Process?.Dispose();
        Process = null;
    }
}

/// <summary>A real BattlefrontII.exe process.</summary>
internal sealed class SystemServerProcess : IServerProcess
{
    private readonly Process _process;

    private SystemServerProcess(Process process) => _process = process;

    public static IServerProcess Start(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var wrapper = new SystemServerProcess(process);
        process.OutputDataReceived += (_, e) => { if (e.Data != null) wrapper.Output?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) wrapper.Output?.Invoke(e.Data); };
        process.Exited += (_, _) => wrapper.Exited?.Invoke();
        if (!process.Start()) throw new InvalidOperationException("The server did not start.");
        if (info.RedirectStandardOutput) process.BeginOutputReadLine();
        if (info.RedirectStandardError) process.BeginErrorReadLine();
        return wrapper;
    }

    public static IServerProcess? TryAttach(ServerSession session, string exe)
    {
        Process process;
        try { process = System.Diagnostics.Process.GetProcessById(session.Pid); }
        catch (ArgumentException) { return null; }
        try
        {
            bool same = !process.HasExited
                && Math.Abs((process.StartTime.ToUniversalTime() - session.StartedUtc).TotalSeconds) < 2
                && string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
            if (!same)
            {
                process.Dispose();
                return null;
            }
            var wrapper = new SystemServerProcess(process);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => wrapper.Exited?.Invoke();
            return wrapper;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return null;
        }
    }

    public int Id => _process.Id;
    public DateTime StartTimeUtc => _process.StartTime.ToUniversalTime();

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public event Action<string>? Output;
    public event Action? Exited;

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasExited;
        }
    }

    /// <summary>A headless server's window is message-only (HWND_MESSAGE), which EnumWindows
    /// doesn't list; one with a renderer has a top-level window.</summary>
    public bool CloseWindow()
    {
        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(HWND_MESSAGE, hwnd, ServerHost.WindowClass, null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == (uint)_process.Id) return PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        bool posted = false;
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != (uint)_process.Id) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != ServerHost.WindowClass) return true;
            posted = PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return false;
        }, IntPtr.Zero);
        return posted;
    }

    public void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => _process.Dispose();

    private const uint WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>What the server's OPENSWBF2 log lines say happened.</summary>
public enum ServerLogEvent { None, Started, Lobby, MatchStart, MatchOver, MissionStart }

public static class ServerLog
{
    /// <summary>Reads a marker line: "OPENSWBF2 SERVER lobby map=geo1c_con" → (Lobby, geo1c_con).</summary>
    public static (ServerLogEvent Event, string? Map) Parse(string line)
    {
        int at = line.IndexOf("OPENSWBF2 ", StringComparison.Ordinal);
        if (at < 0) return (ServerLogEvent.None, null);
        string text = line[(at + "OPENSWBF2 ".Length)..];
        string? map = null;
        int m = text.IndexOf(" map=", StringComparison.Ordinal);
        if (m >= 0)
        {
            string rest = text[(m + 5)..];
            int end = rest.IndexOf(' ');
            map = end < 0 ? rest : rest[..end];
        }
        if (text.StartsWith("SERVER dedicated", StringComparison.Ordinal)) return (ServerLogEvent.Started, null);
        if (text.StartsWith("SERVER lobby", StringComparison.Ordinal)) return (ServerLogEvent.Lobby, map);
        if (text.StartsWith("SERVER match start", StringComparison.Ordinal)) return (ServerLogEvent.MatchStart, map);
        if (text.StartsWith("SERVER match over", StringComparison.Ordinal)) return (ServerLogEvent.MatchOver, map);
        if (text.StartsWith("NET mission start", StringComparison.Ordinal)) return (ServerLogEvent.MissionStart, map);
        return (ServerLogEvent.None, null);
    }
}
