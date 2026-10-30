using System.Diagnostics;
using System.IO.Compression;

namespace OpenBattlefront.Core;

/// <summary>Unpacks the archives mods come in. Zip is built in; 7z, rar and tar go through
/// Windows' own tar.exe (libarchive), so nothing extra ships with the launcher.</summary>
public static class Archives
{
    private static readonly string[] TarFormats = { ".7z", ".rar", ".tar", ".tgz", ".tar.gz", ".tar.xz", ".tar.bz2" };

    public static bool IsArchive(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        TarFormats.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }

        string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
            throw new InvalidDataException($"Windows can't unpack {Path.GetFileName(archive)} here. Extract it with 7-Zip and drop the folder instead.");
        var info = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // bsdtar refuses absolute paths and ".." entries unless told otherwise (-P), so an
        // archive can only write inside destination.
        foreach (string arg in new[] { "-xf", archive, "-C", destination }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start tar.exe.");
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Windows couldn't unpack {Path.GetFileName(archive)} ({stderr.Result.Trim()}). " +
                                           "Extract it with 7-Zip and drop the folder instead.");
    }
}
