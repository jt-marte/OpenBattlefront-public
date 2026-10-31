using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenBattlefront.Core;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.Tests;

public class RconTests
{
    // The 63-character line from OpenSWBF2 tests\net\roundtrip_netwire.cpp ("rcon line of 63
    // characters passes auth and decodes"): 'J', 0, key 'k', length 63 at offsets 5-8, then the
    // text, after the 5-byte packet header.
    [Fact]
    public void A_63_character_line_matches_the_games_vector()
    {
        string text = new(Enumerable.Range(0, 63).Select(i => (char)('a' + i % 26)).ToArray());
        byte[] packet = RconPacket.EncodeLine((byte)'k', text);

        var expected = new List<byte> { 28, 0, 0, 0, 1, (byte)'J', 0, (byte)'k', 63 };
        expected.AddRange(Encoding.ASCII.GetBytes(text));
        Assert.Equal(expected, packet);
        Assert.Throws<ArgumentException>(() => RconPacket.EncodeLine((byte)'k', text + "x"));
    }

    [Fact]
    public void Hello_and_heartbeat_are_eight_bytes()
    {
        Assert.Equal(new byte[] { 28, 0, 0, 0, 1, (byte)'s', 0, 0 }, RconPacket.EncodeHello());
        Assert.Equal(new byte[] { 28, 0, 0, 0, 1, 0x29, 0, (byte)'z' }, RconPacket.EncodeHeartbeat((byte)'z'));
    }

    [Fact]
    public void Replies_are_key_then_lines_then_zero_in_whole_words()
    {
        byte[] datagram = RconPacket.EncodeReply((byte)'4', new[] { "hello" });
        // header, then '4' 1 5 h e l l o 0 = 9 bytes, padded to 12
        Assert.Equal(new byte[] { 29, 0, 0, 0, 1, (byte)'4', 1, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 0, 0, 0 }, datagram);

        // A payload that fills its last word still gets the zero word EndWrite adds.
        byte[] heartbeat = RconPacket.EncodeReply((byte)'4', Array.Empty<string>());
        Assert.Equal(new byte[] { 29, 0, 0, 0, 1, (byte)'4', 0, 0, 0 }, heartbeat);

        Assert.True(RconPacket.TryReadFragment(datagram, out var fragment));
        Assert.Equal((29, 0, 1, false), (fragment.Type, fragment.Index, fragment.Count, fragment.Compressed));
        var reply = RconPacket.DecodeReply(fragment.Payload)!;
        Assert.Equal((byte)'4', reply.Key);
        Assert.Equal(new[] { "hello" }, reply.Lines);

        Assert.Null(RconPacket.DecodeReply(new byte[] { (byte)'4', 1, 9, (byte)'x' }));
        Assert.Null(RconPacket.DecodeReply(new byte[] { (byte)'4', 1 }));
    }

    [Fact]
    public void Long_replies_are_put_back_together()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"m {i} map{i:00}c_con 0 0 -").ToList();
        byte[] whole = RconPacket.EncodeReply(7, lines);
        var parts = FakeRconServer.Split(whole, 64);
        Assert.True(parts.Count > 2);

        var assembler = new RconAssembler();
        byte[]? payload = null;
        foreach (byte[] part in parts)
        {
            Assert.Null(payload);
            Assert.True(RconPacket.TryReadFragment(part, out var fragment));
            payload = assembler.Add(fragment);
        }
        Assert.Equal(lines, RconPacket.DecodeReply(payload)!.Lines);

        // A lost middle datagram drops the group rather than gluing the wrong parts.
        assembler = new RconAssembler();
        payload = null;
        foreach (byte[] part in parts.Where((_, i) => i != 1))
        {
            RconPacket.TryReadFragment(part, out var fragment);
            payload ??= assembler.Add(fragment);
        }
        Assert.Null(payload);
    }

    [Fact]
    public void Reads_state_players_and_rotation()
    {
        var state = RconReplies.ParseState(new[]
        {
            "#state", "state=lobby hold=1 players=3/2/6 time=-1 tickets=150/150 scores=0/0 map=geo1c_con next=kas2g_ctf",
        });
        Assert.Equal(new ServerState(ServerPhase.Lobby, true, 3, 2, 6, -1, 150, 150, 0, 0, "geo1c_con", "kas2g_ctf"), state);
        Assert.Equal(ServerPhase.Shell, RconReplies.ParseState(new[] { "#state", "state=shell hold=0 players=0/0/0 time=-1 tickets=0/0 scores=0/0 map= next=" }).Phase);

        var players = RconReplies.ParsePlayers(new[]
        {
            "#playerinfo", "p 1 1 1 25 5 2 31 Jay Tee", "p 4 0 0 0 0 0 120 New Player [OBF]", "more 9",
        });
        Assert.Equal(9, players.More);
        Assert.Equal(new PlayerInfo(1, 1, true, 25, 5, 2, 31, "Jay Tee"), players.Rows[0]);
        Assert.Equal("New Player [OBF]", players.Rows[1].Name);
        Assert.Null(RconReplies.ParsePlayers(new[] { "#playerinfo", "end 0" }).More);

        var rotation = RconReplies.ParseRotation(new[]
        {
            "#maplist", "m 1 geo1c_con 150 150 cur", "m 2 kas2g_ctf 0 0 next", "m 3 hot1g_hunt 0 0 -", "end 3 next=2",
        });
        Assert.Equal(3, rotation.Total);
        Assert.True(rotation.Rows[0].IsCurrent);
        Assert.True(rotation.Rows[1].IsNext);
        Assert.Equal(new RotationEntry(3, "hot1g_hunt", 0, 0, false, false), rotation.Rows[2]);

        var e = Assert.Throws<NotSupportedException>(() => RconReplies.ParseState(new[] { "command not found" }));
        Assert.Contains("Update OpenSWBF2", e.Message);
    }

    [Theory]
    [InlineData("game started: geo1c_con", true)]
    [InlineData("game over", true)]
    [InlineData("added player Jay", true)]
    [InlineData("lobby: geo1c_con", true)]
    [InlineData("match started: geo1c_con", true)]
    [InlineData("Jay: gg everyone", true)]
    [InlineData("logged in", false)]
    [InlineData("done", false)]
    [InlineData("ended", false)]
    [InlineData("booted player 3 (Jay)", false)]
    [InlineData("Server name : x", false)]
    public void Tells_pushed_messages_from_replies(string line, bool push)
    {
        Assert.Equal(push, RconReplies.LooksLikePush(new[] { line }));
    }

    [Fact]
    public async Task Logs_in_and_follows_the_admin_key()
    {
        using var server = new FakeRconServer();
        server.Handle = command => command == "/state"
            ? new[] { "#state", "state=playing hold=0 players=1/0/1 time=600 tickets=100/90 scores=3/1 map=geo1c_con next=kas2g_ctf" }
            : null;
        await using var client = new RconClient(server.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(1) };
        var pushed = new List<string>();
        client.Pushed += line => { lock (pushed) pushed.Add(line); };

        await client.ConnectAsync("secret", CancellationToken.None);
        Assert.True(client.IsLoggedIn);

        server.PushBeforeReply.Add("Jay: hi admin");
        var state = await client.StateAsync();
        Assert.Equal(ServerPhase.Playing, state.Phase);
        Assert.Equal(600, state.TimeLeft);
        // The key changes with each reply; the next command must use the new one.
        state = await client.StateAsync();
        Assert.Equal("kas2g_ctf", state.Next);
        lock (pushed) Assert.Equal(new[] { "Jay: hi admin" }, pushed);
        Assert.All(server.Lines, l => Assert.StartsWith("/admin ", l));
        Assert.Equal("/admin /login secret", server.Lines[0]);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        using var server = new FakeRconServer();
        await using var client = new RconClient(server.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(1) };
        var e = await Assert.ThrowsAsync<RconException>(() => client.ConnectAsync("nope", CancellationToken.None));
        Assert.Contains("password", e.Message);
        Assert.False(client.IsLoggedIn);
    }

    [Fact]
    public async Task Follows_pages_and_sends_the_playlist()
    {
        using var server = new FakeRconServer { SplitAt = 96 };
        server.Handle = command => command switch
        {
            "/playerinfo" => new[] { "#playerinfo" }.Concat(Enumerable.Range(1, 12).Select(i => $"p {i} 1 1 0 0 0 30 Player {i}")).Append("more 13"),
            "/playerinfo 13" => new[] { "#playerinfo", "p 13 2 1 0 0 0 30 Last One", "end 13" },
            "/maplist" => new[] { "#maplist", "m 1 geo1c_con 0 0 cur", "more 2" },
            "/maplist 2" => new[] { "#maplist", "m 2 kas2g_ctf 5 5 next", "end 2 next=2" },
            "/clearmaps" => new[] { "#clearmaps", "maps cleared" },
            _ when command.StartsWith("/appendmap bad") => new[] { "#appendmap", "map 'bad' not added" },
            _ when command.StartsWith("/appendmap ") => new[] { "#appendmap", "map appended 2" },
            _ => null,
        };
        await using var client = new RconClient(server.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(1) };
        await client.ConnectAsync("secret", CancellationToken.None);

        var players = await client.PlayersAsync();
        Assert.Equal(13, players.Count);
        Assert.Equal("Last One", players[^1].Name);

        var rotation = await client.RotationAsync();
        Assert.Equal(new[] { "geo1c_con", "kas2g_ctf" }, rotation.Select(r => r.Mission));

        server.Lines.Clear();
        await client.SetRotationAsync(new[]
        {
            new PlaylistEntry { Mission = "kas2g_ctf", Tickets1 = 5 },
            new PlaylistEntry { Mission = "hot1g_hunt" },
        });
        Assert.Equal(new[] { "/admin /clearmaps", "/admin /appendmap kas2g_ctf 5 5", "/admin /appendmap hot1g_hunt" }, server.Lines);

        await Assert.ThrowsAsync<RconException>(() => client.SetRotationAsync(new[] { new PlaylistEntry { Mission = "bad" } }));
    }

    [Fact]
    public async Task Takes_an_admin_slot_again_when_it_was_lost()
    {
        using var server = new FakeRconServer();
        server.Handle = command => command switch
        {
            "/state" => new[] { "#state", "state=lobby hold=1 players=0/0/0 time=-1 tickets=0/0 scores=0/0 map=geo1c_con next=" },
            "/endgame" => new[] { "ended" },
            _ => null,
        };
        await using var client = new RconClient(server.EndPoint) { ReplyTimeout = TimeSpan.FromMilliseconds(300) };
        await client.ConnectAsync("secret", CancellationToken.None);

        // A query is sent again once the slot is back.
        server.HasSlot = false;
        server.LoggedIn = false;
        Assert.Equal(ServerPhase.Lobby, (await client.StateAsync()).Phase);
        Assert.True(client.IsLoggedIn);

        // A command that changes the game is not: the caller hears it timed out, and the next one works.
        server.HasSlot = false;
        server.LoggedIn = false;
        server.Lines.Clear();
        await Assert.ThrowsAsync<TimeoutException>(() => client.EndMatchAsync());
        Assert.Equal(new[] { "/admin /login secret" }, server.Lines);
        await client.EndMatchAsync();
        Assert.Equal("/admin /endgame", server.Lines[^1]);
    }

    [Fact]
    public async Task Logs_in_again_when_the_server_forgot_the_session()
    {
        using var server = new FakeRconServer();
        server.Handle = command => command == "/endgame" ? new[] { "ended" } : null;
        await using var client = new RconClient(server.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(1) };
        await client.ConnectAsync("secret", CancellationToken.None);

        server.LoggedIn = false;
        await client.EndMatchAsync();
        Assert.Equal(new[] { "/admin /login secret", "/admin /endgame", "/admin /login secret", "/admin /endgame" }, server.Lines);
    }
}

/// <summary>A server's admin side on loopback: hello, heartbeat, login and canned replies. The
/// key changes after every line, as the game rotates it, and the last two keys are accepted.</summary>
internal sealed class FakeRconServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private byte _key = (byte)'4';
    private byte _previousKey = (byte)'z';

    public FakeRconServer() => _ = Task.Run(LoopAsync);

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
    public string Password { get; set; } = "secret";
    public bool LoggedIn { get; set; }

    /// <summary>A heartbeat takes an admin slot; without one, lines get no answer at all.</summary>
    public bool HasSlot { get; set; }
    public int SplitAt { get; set; } = 1000;
    public List<string> Lines { get; } = new();
    public List<string> PushBeforeReply { get; } = new();
    public Func<string, IEnumerable<string>?> Handle { get; set; } = _ => null;

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await _udp.ReceiveAsync(_stop.Token); }
            catch (Exception) { return; }
            byte[] p = received.Buffer;
            if (p.Length < 7 || p[0] != RconPacket.ConsoleType) continue;
            async Task Reply(IEnumerable<string> lines)
            {
                foreach (byte[] part in Split(RconPacket.EncodeReply(_key, lines), SplitAt))
                    await _udp.SendAsync(part, received.RemoteEndPoint);
            }
            bool keyOk = p.Length >= 8 && (p[7] == _key || p[7] == _previousKey);
            switch ((char)p[5])
            {
                case 's':
                    await Reply(new[] { "hello" });
                    break;
                case ')':
                    if (!keyOk) break;
                    HasSlot = true;
                    await Reply(Array.Empty<string>());
                    break;
                case 'J':
                    if (!keyOk || !HasSlot) break;
                    string line = Encoding.Latin1.GetString(p, 9, p[8]);
                    Lines.Add(line);
                    string command = line.StartsWith("/admin ") ? line[7..] : "";
                    List<string> reply;
                    if (command.StartsWith("/login "))
                    {
                        LoggedIn = command[7..] == Password;
                        reply = new() { LoggedIn ? "logged in" : "login failed" };
                    }
                    else if (!LoggedIn) reply = new() { "not logged in" };
                    else
                    {
                        foreach (string push in PushBeforeReply) await Reply(new[] { push });
                        PushBeforeReply.Clear();
                        reply = Handle(command)?.ToList() ?? new() { "done" };
                    }
                    _previousKey = _key;
                    _key = (byte)(_key == 'z' ? 'a' : _key + 1);
                    await Reply(reply);
                    break;
            }
        }
    }

    /// <summary>Splits a reply datagram's payload at word boundaries over several datagrams.</summary>
    public static List<byte[]> Split(byte[] datagram, int maxPayload)
    {
        byte[] payload = datagram[RconPacket.HeaderSize..];
        int size = Math.Max(4, maxPayload / 4 * 4);
        var chunks = payload.Chunk(size).ToList();
        return chunks.Select((chunk, i) => new byte[] { datagram[0], datagram[1], datagram[2], (byte)i, (byte)chunks.Count }.Concat(chunk).ToArray()).ToList();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
