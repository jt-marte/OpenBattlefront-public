using System.Diagnostics;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ServerHostTests
{
    private static (InstallLayout Layout, ServerProfile Profile) Setup(TempDir temp)
    {
        var layout = new InstallLayout(temp.Sub("Root"));
        temp.File("Root\\Game\\BattlefrontII.exe", "exe");
        var profile = new ServerProfile { Playlist = { new PlaylistEntry { Mission = "geo1c_con", Tickets1 = 150 } } };
        return (layout, profile);
    }

    [Fact]
    public void Starts_from_the_game_folder_with_its_settings_written()
    {
        using var temp = new TempDir();
        var (layout, profile) = Setup(temp);
        profile.Heroes = false;
        FakeServerProcess? process = null;
        ProcessStartInfo? seen = null;
        var host = new ServerHost(layout, info =>
        {
            seen = info;
            return process = new FakeServerProcess();
        });
        var lines = new List<string>();
        host.LineReceived += lines.Add;

        host.Start(profile);

        Assert.Equal(layout.GameExe, seen!.FileName);
        Assert.Equal(layout.Game, seen.WorkingDirectory);
        Assert.True(seen.RedirectStandardOutput);
        Assert.StartsWith("-dedicated geo1c_con 150 150 -lobby ", seen.Arguments);
        Assert.Equal(1, host.MapsOnCommandLine);
        Assert.Contains("/heroes 0", File.ReadAllLines(ServerHost.SettingsFile(layout)));
        Assert.Equal("", File.ReadAllText(ServerHost.RotationFile(layout)));
        Assert.True(host.IsRunning);
        Assert.Throws<InvalidOperationException>(() => host.Start(profile));

        process!.Say("OPENSWBF2 SERVER dedicated name=\"x\" port=3658");
        process.Say("OPENSWBF2 SERVER lobby map=geo1c_con");
        Assert.Equal(2, lines.Count);
        Assert.Equal(lines, host.Log);
    }

    [Fact]
    public async Task Stop_closes_the_window_first()
    {
        using var temp = new TempDir();
        var (layout, profile) = Setup(temp);
        var process = new FakeServerProcess { ExitOnClose = true };
        var host = new ServerHost(layout, _ => process);
        host.Start(profile);

        bool shutdownSent = false;
        await host.StopAsync(() =>
        {
            shutdownSent = true;
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(200));

        Assert.False(shutdownSent);
        Assert.Equal(new[] { "close" }, process.Steps);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task Stop_falls_back_to_shutdown_then_kill()
    {
        using var temp = new TempDir();
        var (layout, profile) = Setup(temp);

        // No window to close: /shutdown over rcon.
        var windowless = new FakeServerProcess { HasWindow = false };
        var host = new ServerHost(layout, _ => windowless);
        host.Start(profile);
        await host.StopAsync(() =>
        {
            windowless.Exit();
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(200));
        Assert.Equal(new[] { "close", "exit" }, windowless.Steps);

        // Neither works: kill it.
        var stubborn = new FakeServerProcess();
        host = new ServerHost(layout, _ => stubborn);
        host.Start(profile);
        await host.StopAsync(() => throw new TimeoutException(), TimeSpan.FromMilliseconds(50));
        Assert.Equal(new[] { "close", "kill" }, stubborn.Steps);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Exit_is_reported()
    {
        using var temp = new TempDir();
        var (layout, profile) = Setup(temp);
        var process = new FakeServerProcess();
        var host = new ServerHost(layout, _ => process);
        bool exited = false;
        host.Exited += () => exited = true;
        host.Start(profile);
        process.Exit();
        Assert.True(exited);
        Assert.False(host.IsRunning);
    }

    [Theory]
    [InlineData("OPENSWBF2 SERVER dedicated name=\"OpenSWBF2 Server\" port=3658", ServerLogEvent.Started, null)]
    [InlineData("[LogNet] OPENSWBF2 SERVER lobby map=geo1c_con", ServerLogEvent.Lobby, "geo1c_con")]
    [InlineData("OPENSWBF2 SERVER match start map=kas2g_ctf", ServerLogEvent.MatchStart, "kas2g_ctf")]
    [InlineData("OPENSWBF2 SERVER match over map=kas2g_ctf winner=1", ServerLogEvent.MatchOver, "kas2g_ctf")]
    [InlineData("OPENSWBF2 NET mission start tps=60 map=hot1g_hunt", ServerLogEvent.MissionStart, "hot1g_hunt")]
    [InlineData("some other line", ServerLogEvent.None, null)]
    public void Reads_the_servers_marker_lines(string line, ServerLogEvent expected, string? map)
    {
        Assert.Equal((expected, map), ServerLog.Parse(line));
    }
}

internal sealed class FakeServerProcess : IServerProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ExitOnClose { get; init; }
    public bool HasWindow { get; init; } = true;
    public List<string> Steps { get; } = new();

    public int Id => 4242;
    public DateTime StartTimeUtc { get; } = DateTime.UtcNow;
    public bool HasExited => _exit.Task.IsCompleted;

    public event Action<string>? Output;
    public event Action? Exited;

    public void Say(string line) => Output?.Invoke(line);

    public void Exit()
    {
        if (!_exit.TrySetResult()) return;
        Steps.Add("exit");
        Exited?.Invoke();
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout) => await Task.WhenAny(_exit.Task, Task.Delay(timeout)) == _exit.Task;

    public bool CloseWindow()
    {
        Steps.Add("close");
        if (!HasWindow) return false;
        if (ExitOnClose)
        {
            _exit.TrySetResult();
            Exited?.Invoke();
        }
        return true;
    }

    public void Kill()
    {
        Steps.Add("kill");
        _exit.TrySetResult();
        Exited?.Invoke();
    }

    public void Dispose() { }
}
