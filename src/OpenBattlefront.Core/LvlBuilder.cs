using System.Diagnostics;

namespace OpenBattlefront.Core;

/// <summary>Builds the four core lvls into Game\data\_lvl_pc from the player's stock copies, with
/// the build's own LuaCompiler and scripts (LuaCompiler lvl).</summary>
public static class LvlBuilder
{
    public static IReadOnlyList<string> Arguments(InstallLayout layout, BuildManifest manifest) => new[]
    {
        "lvl",
        "--index", manifest.Index.Replace('/', '\\'),
        "--gamedata", layout.Stock,
        "--out-dir", layout.LvlDir,
    };

    public static async Task BuildAsync(InstallLayout layout, string buildDir, BuildManifest manifest, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path.Combine(buildDir, manifest.LvlBuilder))
        {
            WorkingDirectory = buildDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in Arguments(layout, manifest)) info.ArgumentList.Add(arg);

        Directory.CreateDirectory(layout.LvlDir);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start LuaCompiler.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);

        Directory.CreateDirectory(layout.Logs);
        await File.WriteAllTextAsync(Path.Combine(layout.Logs, "lvl-build.log"),
            $"{DateTime.Now:u} OpenSWBF2 {manifest.Version}, exit {process.ExitCode}{Environment.NewLine}{output}", ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Building the core game files failed: " + output.Trim());
        if (!layout.HasCoreLvls)
            throw new InvalidOperationException("Building the core game files did not produce all four lvls.");
    }
}
