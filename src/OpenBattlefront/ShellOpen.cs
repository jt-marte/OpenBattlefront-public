using System.Diagnostics;

namespace OpenBattlefront;

/// <summary>Opens folders in Explorer and links in the default browser.</summary>
public static class ShellOpen
{
    public static void Folder(string path, bool create = false)
    {
        if (create) Directory.CreateDirectory(path);
        if (Directory.Exists(path)) Start(path);
        else App.Launcher?.ShowNotice("That folder isn't there yet.");
    }

    public static void Url(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Start(url);
    }

    private static void Start(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) { App.Launcher?.ShowError(e); }
    }
}
