using Microsoft.Win32;

namespace OpenBattlefront.Core;

/// <summary>Read-only access to the registry, so the game search can be tested without one. Key
/// paths start with HKCU\ or HKLM\.</summary>
public interface IRegistryView
{
    string? GetString(string key, string value);
    IReadOnlyList<string> SubKeyNames(string key);
}

public sealed class WindowsRegistry : IRegistryView
{
    public string? GetString(string key, string value)
    {
        try
        {
            using var k = Open(key);
            return k?.GetValue(value) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> SubKeyNames(string key)
    {
        try
        {
            using var k = Open(key);
            return k?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static RegistryKey? Open(string key)
    {
        int slash = key.IndexOf('\\');
        if (slash < 0) return null;
        RegistryKey? hive = key[..slash].ToUpperInvariant() switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            _ => null,
        };
        return hive?.OpenSubKey(key[(slash + 1)..]);
    }
}
