using System.Text;

namespace OpenBattlefront.Core.Rcon;

/// <summary>One datagram of a reply group: the 5-byte packet header and its payload.</summary>
public sealed record RconFragment(byte Type, int Group, int Index, int Count, bool Compressed, byte[] Payload);

/// <summary>A reply group: the admin key the server wants next, and its text lines (none for a
/// heartbeat).</summary>
public sealed record RconReply(byte Key, IReadOnlyList<string> Lines);

/// <summary>
/// The retail remote-admin wire format (NetAdmin.cpp ReceiveConsole, NetWire.cpp IsRconAuthorized).
/// Every datagram starts with the packet header: type, group (16-bit LE), index, count (top bit
/// set when compressed). A request (type 28, a new group number each) then carries plain bytes:
/// the command ('s' hello, ')' heartbeat, 'J' line), a zero, the admin key, and then nothing for
/// a hello, a zero for a heartbeat, or a line's length and text of up to 63 characters. A reply
/// (type 29) carries a bit stream packed in little-endian 32-bit words: the key, then
/// [1, length, text] per line, then 0.
/// </summary>
public static class RconPacket
{
    public const byte ConsoleType = 28;
    public const byte ReplyType = 29;
    public const int HeaderSize = 5;
    public const int LineMax = 63;
    public const int ReplyLineMax = 127;
    public const string AdminPrefix = "/admin ";

    public const byte Hello = (byte)'s';
    public const byte Heartbeat = (byte)')';
    public const byte Line = (byte)'J';

    /// <summary>Latin-1: the game narrows each character to one byte.</summary>
    private static readonly Encoding Text = Encoding.Latin1;

    public static byte[] EncodeHello(int group) => new byte[] { ConsoleType, (byte)group, (byte)(group >> 8), 0, 1, Hello, 0, 0 };

    public static byte[] EncodeHeartbeat(int group, byte key) => new byte[] { ConsoleType, (byte)group, (byte)(group >> 8), 0, 1, Heartbeat, 0, key, 0 };

    /// <summary>A command line such as "/admin /state". Longer than 63 characters is refused,
    /// since the server cuts it.</summary>
    public static byte[] EncodeLine(int group, byte key, string line)
    {
        if (line.Length > LineMax) throw new ArgumentException($"An admin line is at most {LineMax} characters: {line}", nameof(line));
        var packet = new byte[HeaderSize + 4 + line.Length];
        packet[0] = ConsoleType;
        packet[1] = (byte)group;
        packet[2] = (byte)(group >> 8);
        packet[4] = 1;
        packet[5] = Line;
        packet[6] = 0;
        packet[7] = key;
        packet[8] = (byte)line.Length;
        for (int i = 0; i < line.Length; i++) packet[9 + i] = line[i] <= 0xFF ? (byte)line[i] : (byte)'?';
        return packet;
    }

    public static bool TryReadFragment(ReadOnlySpan<byte> datagram, out RconFragment fragment)
    {
        fragment = null!;
        if (datagram.Length < HeaderSize) return false;
        fragment = new RconFragment(
            datagram[0],
            datagram[1] | datagram[2] << 8,
            datagram[3],
            datagram[4] & 0x7F,
            (datagram[4] & 0x80) != 0,
            datagram[HeaderSize..].ToArray());
        return true;
    }

    /// <summary>Reads a reply group's payload, or null when it is cut short.</summary>
    public static RconReply? DecodeReply(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        byte key = payload[0];
        var lines = new List<string>();
        int at = 1;
        while (true)
        {
            if (at >= payload.Length) return null;
            byte more = payload[at++];
            if (more == 0) break;
            if (at >= payload.Length) return null;
            int length = payload[at++];
            if (at + length > payload.Length) return null;
            lines.Add(Text.GetString(payload.Slice(at, length)));
            at += length;
        }
        return new RconReply(key, lines);
    }

    /// <summary>A reply datagram as the server writes it (for tests and the fake server).</summary>
    public static byte[] EncodeReply(byte key, IEnumerable<string> lines, int group = 0)
    {
        var bits = new List<byte> { key };
        foreach (string line in lines)
        {
            byte[] text = Text.GetBytes(line.Length > ReplyLineMax ? line[..ReplyLineMax] : line);
            bits.Add(1);
            bits.Add((byte)text.Length);
            bits.AddRange(text);
        }
        bits.Add(0);
        // EndWrite always writes the word in progress, so a full last word gets a zero word after it.
        int pad = 4 - bits.Count % 4;
        bits.AddRange(new byte[pad]);
        var packet = new List<byte> { ReplyType, (byte)group, (byte)(group >> 8), 0, 1 };
        packet.AddRange(bits);
        return packet.ToArray();
    }
}

/// <summary>Puts a reply group back together. Large replies come in several datagrams with the
/// same group number and count, split at word boundaries, in order.</summary>
public sealed class RconAssembler
{
    private readonly List<byte[]> _parts = new();
    private int _count;
    private int _group = -1;

    /// <summary>The whole payload once the last datagram of a group is in, otherwise null.</summary>
    public byte[]? Add(RconFragment fragment)
    {
        if (fragment.Compressed) return null;
        if (fragment.Count <= 1) return fragment.Payload;
        if (fragment.Index == 0 || fragment.Group != _group || fragment.Count != _count)
        {
            _parts.Clear();
            _group = fragment.Group;
            _count = fragment.Count;
            if (fragment.Index != 0) return null;
        }
        if (fragment.Index != _parts.Count) return null;
        _parts.Add(fragment.Payload);
        if (_parts.Count < _count) return null;
        var whole = _parts.SelectMany(p => p).ToArray();
        _parts.Clear();
        _group = -1;
        return whole;
    }
}
