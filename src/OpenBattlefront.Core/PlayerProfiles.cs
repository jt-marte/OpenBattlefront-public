namespace OpenBattlefront.Core;

/// <summary>The game's profiles (Game\SaveGames\&lt;name&gt;.profile) and the name the launcher starts the
/// game with (-name): the game signs into that profile, or makes it, without its profile screen.</summary>
public static class PlayerProfiles
{
    /// <summary>The game's profile screen makes names of 3 to 15 characters; older profiles can be
    /// shorter, and still sign in.</summary>
    public const int MinLength = 3;
    public const int MaxLength = 15;

    /// <summary>The profiles in the game copy, newest first.</summary>
    public static IReadOnlyList<string> List(InstallLayout layout)
    {
        if (!Directory.Exists(layout.SaveGames)) return Array.Empty<string>();
        return Directory.GetFiles(layout.SaveGames, "*.profile")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(IsValid)
            .ToList();
    }

    /// <summary>A name the launcher can sign in with: up to 15 plain characters, no spaces at the ends,
    /// nothing that would break the command line, and not a broken profile such as "[NULL]".</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxLength && name.Trim() == name &&
        !name.StartsWith('[') && name.All(c => c >= ' ' && c < 127 && c != '"' && c != '\\');

    /// <summary>A name the game would make a new profile with (3 characters or more).</summary>
    public static bool CanCreate(string? name) => IsValid(name) && name!.Length >= MinLength;

    /// <summary>A name the player can choose: one of their profiles, or one the game can make.</summary>
    public static bool CanUse(InstallLayout layout, string? name) =>
        CanCreate(name) || (IsValid(name) && List(layout).Contains(name!, StringComparer.OrdinalIgnoreCase));

    /// <summary>A usable name from free text (a Windows user name, say), or null.</summary>
    public static string? Clean(string? text)
    {
        if (text == null) return null;
        string kept = new string(text.Where(c => c >= ' ' && c < 127 && c != '"' && c != '\\' && c != '[' && c != ']').ToArray()).Trim();
        if (kept.Length > MaxLength) kept = kept[..MaxLength].TrimEnd();
        return CanCreate(kept) ? kept : null;
    }

    /// <summary>The name for a player who hasn't chosen one: their newest profile, else their Windows
    /// name, else "Player".</summary>
    public static string DefaultName(InstallLayout layout, string? userName) =>
        List(layout).FirstOrDefault() ?? Clean(userName) ?? "Player";
}
