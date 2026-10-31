using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

/// <summary>A server to join: host[:port], as the game's Direct Connect takes it (IPv4 or a host
/// name). Its mod downloads are HTTP over TCP on the same port number (the Docker server's
/// modserver.py).</summary>
public sealed partial record ServerAddress(string Host, int Port)
{
    public const int DefaultPort = 3658;

    /// <summary>The game reads at most 63 characters of a Join IP address.</summary>
    public const int MaxLength = 63;

    public static bool TryParse(string? text, out ServerAddress address)
    {
        address = new ServerAddress("", DefaultPort);
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;
        string host = text;
        int port = DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.None, null, out port) || port is < 1 or > 65535) return false;
        }
        if (!HostName().IsMatch(host)) return false;
        address = new ServerAddress(host.ToLowerInvariant(), port);
        return true;
    }

    public static ServerAddress Parse(string text) =>
        TryParse(text, out var address) ? address : throw new FormatException("That isn't a server address. Type it as address:port, such as 203.0.113.5:3658 or myserver.example.com.");

    /// <summary>What the player types and the recent list shows: the port only when it isn't 3658.</summary>
    public override string ToString() => Port == DefaultPort ? Host : $"{Host}:{Port}";

    /// <summary>The game's -connect value.</summary>
    public string ConnectArg => $"{Host}:{Port}";

    public Uri ModsBase => new UriBuilder(Uri.UriSchemeHttp, Host, Port, "/").Uri;

    /// <summary>A name for folders: 203.0.113.5_3658.</summary>
    public string Key => $"{Host}_{Port}";

    [GeneratedRegex(@"^(?=.{1,63}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();
}

/// <summary>The join password as the game and the Docker server take it.</summary>
public static class JoinPasswords
{
    public const int MaxLength = 15;

    public static bool IsValid(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length <= MaxLength && password.All(c => c > ' ' && c < 127 && c != '"');
}
