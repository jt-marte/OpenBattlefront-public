using System.Diagnostics;

namespace OpenBattlefront.Core;

public static class GameLauncher
{
    public static string BuildArguments(LauncherState state)
    {
        var parts = new List<string>();
        if (state.Renderer == "vulkan") parts.Add("-vulkan");
        else if (state.Renderer == "d3d9") parts.Add("-d3d9");
        if (state.Borderless) parts.Add("-borderless");
        if (state.SkipIntro) parts.Add("-nointro");
        if (PlayerProfiles.IsValid(state.PlayerName)) parts.Add($"-name \"{state.PlayerName}\"");
        if (!string.IsNullOrWhiteSpace(state.ExtraArgs)) parts.Add(state.ExtraArgs.Trim());
        return string.Join(' ', parts);
    }

    /// <summary>Starts the game from Game\, its working directory, where it finds data\, AddOn\ and SaveGames\.</summary>
    public static Process Start(InstallLayout layout, LauncherState state)
    {
        if (!File.Exists(layout.GameExe)) throw new FileNotFoundException("The game is not installed yet.", layout.GameExe);
        if (!layout.HasCoreLvls) throw new InvalidOperationException("The core game files are missing. Use Settings > Rebuild core files.");
        var info = new ProcessStartInfo(layout.GameExe, BuildArguments(state))
        {
            WorkingDirectory = layout.Game,
            UseShellExecute = false,
        };
        return Process.Start(info) ?? throw new InvalidOperationException("The game did not start.");
    }

    /// <param name="except">A hosted dedicated server: the same exe, but not the game.</param>
    public static bool IsRunning(InstallLayout layout, int? except = null)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(layout.GameExe)))
        {
            using (process)
            {
                try
                {
                    if (process.Id == except) continue;
                    if (string.Equals(process.MainModule?.FileName, layout.GameExe, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Another user's process, or one that just exited.
                }
            }
        }
        return false;
    }
}

public sealed record Prerequisite(string Name, bool Present, string Url);

public static class Prerequisites
{
    /// <summary>System DLLs the x64 game imports that Windows does not ship.</summary>
    public static IReadOnlyList<Prerequisite> Check()
    {
        string system = Environment.SystemDirectory;
        bool Has(params string[] dlls) => dlls.All(d => File.Exists(Path.Combine(system, d)));
        return new[]
        {
            new Prerequisite("Microsoft Visual C++ Redistributable (x64)", Has("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"),
                "https://aka.ms/vs/17/release/vc_redist.x64.exe"),
            new Prerequisite("DirectX End-User Runtime (d3dx9_43.dll)", Has("d3dx9_43.dll"),
                "https://www.microsoft.com/en-us/download/details.aspx?id=35"),
        };
    }
}
