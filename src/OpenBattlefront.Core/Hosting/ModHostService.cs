using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBattlefront.Core.Hosting;

public sealed class ModHostOptions
{
    /// <summary>The hosted server's Game\AddOn.</summary>
    public required string AddOn { get; init; }
    public required FileHashCache Hashes { get; init; }
    public string ServerName { get; init; } = "";
    public string Version { get; init; } = "";
    public string JoinPassword { get; init; } = "";
    public string AdminPassword { get; init; } = "";

    /// <summary>All downloads together, in bytes a second; 0 for no cap.</summary>
    public double RateBytesPerSecond { get; init; }

    /// <summary>Any address for players on other PCs; loopback for tests.</summary>
    public IPAddress Bind { get; init; } = IPAddress.Any;

    /// <summary>TCP, the game's port number; 0 picks one (tests).</summary>
    public int Port { get; init; }

    /// <summary>The hosted game's map list (the game's -listmissions, as MapCatalogStore keeps
    /// it) for GET /missions.json, or null when there's none.</summary>
    public Func<string?>? Missions { get; init; }
}

/// <summary>
/// The Docker server's mod service (modserver.py), run by the launcher while it hosts a server:
/// the hosted server's mods for joining launchers (GET /mods.json, ranged GET /mods/…) and the
/// launchers' lobby with "load all, then start" (/status, /lobby/…, /admin/match). Same protocol,
/// same rules and answers, so joining launchers can't tell them apart. No uploads: the admin is at
/// this PC. A small HTTP/1.1 server on a TcpListener, so Windows needs no URL reservation.
/// </summary>
public sealed class ModHostService : IAsyncDisposable
{
    public const int MaxConnections = 96;
    public const int MaxPerAddress = 16;
    private const int MaxHead = 16 * 1024;
    private const int MaxUrl = 1024;
    private const int MaxBody = 64 * 1024;
    private const int Chunk = 64 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ModHostOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, int> _connections = new();
    private readonly object _countLock = new();
    private readonly Gate _gate;
    private readonly Bucket _bucket;
    private int _total;
    private Task? _accepting, _watching;

    public ModHostService(ModHostOptions options, IGameProbe? probe, Func<DateTime>? clock = null)
    {
        _options = options;
        var now = clock ?? (() => DateTime.UtcNow);
        Library = new SharedModLibrary(options.AddOn, options.Hashes);
        Lobby = new LauncherLobby(now);
        Watch = new LobbyWatch(probe, Lobby, now);
        _gate = new Gate(options.JoinPassword, options.AdminPassword, now);
        _bucket = new Bucket(options.RateBytesPerSecond);
        _listener = new TcpListener(options.Bind, options.Port);
    }

    public SharedModLibrary Library { get; }
    public LauncherLobby Lobby { get; }
    public LobbyWatch Watch { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Listens (a SocketException when the port is taken), hashes the mods in the
    /// background and starts watching the game.</summary>
    public void Start(bool watch = true)
    {
        _listener.Start(64);
        _ = Task.Run(Library.Scan);
        _accepting = Task.Run(AcceptLoopAsync);
        if (watch) _watching = Task.Run(() => Watch.RunAsync(_stop.Token));
    }

    /// <summary>Stops listening at once, then gives the loops a moment to finish (a game that
    /// stopped answering mid-look doesn't hold this up).</summary>
    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { _stop.Cancel(); }
        catch (AggregateException) { }
        foreach (var task in new[] { _accepting, _watching })
        {
            if (task == null) continue;
            try { await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            string address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            bool accepted;
            lock (_countLock)
            {
                int mine = _connections.GetValueOrDefault(address);
                accepted = _total < MaxConnections && mine < MaxPerAddress;
                if (accepted)
                {
                    _total++;
                    _connections[address] = mine + 1;
                }
            }
            if (!accepted)
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, address).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
                finally
                {
                    client.Dispose();
                    lock (_countLock)
                    {
                        _total--;
                        if (_connections.TryGetValue(address, out int n) && n <= 1) _connections.TryRemove(address, out _);
                        else _connections[address] = n - 1;
                    }
                }
            });
        }
    }

    // One connection: requests one after another while the client keeps it open

    private sealed record Request(string Method, string Target, Dictionary<string, string> Headers, byte[] Body);

    private sealed class Reply
    {
        public int Status = 200;
        public byte[] Body = Array.Empty<byte>();
        public string ContentType = "application/json";
        public List<(string, string)> Headers = new();
        public SharedFile? File;
        public long Start, Length;
        public bool Close;
    }

    private async Task ServeAsync(TcpClient client, string address)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();
        var buffer = new byte[MaxHead];
        int filled = 0;
        while (!_stop.IsCancellationRequested)
        {
            Request request;
            Reply reply;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                idle.CancelAfter(IdleTimeout);
                var (head, rest, ok) = await ReadHeadAsync(stream, buffer, filled, idle.Token).ConfigureAwait(false);
                if (!ok) return;
                filled = rest;
                if (head == null)
                {
                    await WriteAsync(stream, Json(431, new Dictionary<string, object?> { ["error"] = "request too large" }, close: true), false, idle.Token).ConfigureAwait(false);
                    return;
                }
                var parsed = Parse(head);
                if (parsed == null)
                {
                    await WriteAsync(stream, Json(400, Error("bad request"), close: true), false, idle.Token).ConfigureAwait(false);
                    return;
                }
                var (method, target, version, headers) = parsed.Value;
                byte[] body = Array.Empty<byte>();
                if (headers.ContainsKey("Transfer-Encoding"))
                {
                    await WriteAsync(stream, Json(411, Error("Content-Length is needed"), close: true), method == "HEAD", idle.Token).ConfigureAwait(false);
                    return;
                }
                if (headers.TryGetValue("Content-Length", out string? lengthText))
                {
                    if (!long.TryParse(lengthText, out long length) || length < 0)
                    {
                        await WriteAsync(stream, Json(400, Error("bad Content-Length"), close: true), false, idle.Token).ConfigureAwait(false);
                        return;
                    }
                    if (length > MaxBody)
                    {
                        await WriteAsync(stream, Json(413, Error("that list is too long"), close: true), false, idle.Token).ConfigureAwait(false);
                        return;
                    }
                    body = new byte[length];
                    int have = Math.Min(filled, (int)length);
                    Array.Copy(buffer, body, have);
                    Array.Copy(buffer, have, buffer, 0, filled - have);
                    filled -= have;
                    while (have < length)
                    {
                        int read = await stream.ReadAsync(body.AsMemory(have), idle.Token).ConfigureAwait(false);
                        if (read == 0) return;
                        have += read;
                    }
                }
                request = new Request(method, target, headers, body);
                reply = await RouteAsync(request, address, idle.Token).ConfigureAwait(false);
                reply.Close |= version == "HTTP/1.0" || headers.TryGetValue("Connection", out string? connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase);
            }
            await WriteAsync(stream, reply, request.Method == "HEAD", _stop.Token).ConfigureAwait(false);
            if (reply.Close) return;
        }
    }

    /// <summary>The request head up to the blank line; head null when it outgrows the buffer, ok
    /// false when the client went away or stayed idle.</summary>
    private static async Task<(string? Head, int Left, bool Ok)> ReadHeadAsync(NetworkStream stream, byte[] buffer, int filled, CancellationToken ct)
    {
        while (true)
        {
            int end = IndexOf(buffer, filled, "\r\n\r\n"u8);
            if (end >= 0)
            {
                string head = Encoding.Latin1.GetString(buffer, 0, end);
                int consumed = end + 4;
                Array.Copy(buffer, consumed, buffer, 0, filled - consumed);
                return (head, filled - consumed, true);
            }
            if (filled == buffer.Length) return (null, 0, true);
            int read;
            try { read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or IOException) { return (null, 0, false); }
            if (read == 0) return (null, 0, false);
            filled += read;
        }
    }

    private static int IndexOf(byte[] buffer, int length, ReadOnlySpan<byte> pattern) => buffer.AsSpan(0, length).IndexOf(pattern);

    private static (string Method, string Target, string Version, Dictionary<string, string> Headers)? Parse(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.")) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        // As Python's http.server: leading slashes fold into one (it guards against open redirects)
        string target = first[1].StartsWith("//") ? "/" + first[1].TrimStart('/') : first[1];
        return (first[0], target, first[2], headers);
    }

    // Routes: the same paths, checks and answers as modserver.py's Handler.route

    private async Task<Reply> RouteAsync(Request request, string address, CancellationToken ct)
    {
        try
        {
            var parts = SplitUrl(request.Target) ?? throw new ModHostRefused(400, "bad path");
            string method = request.Method;
            bool get = method is "GET" or "HEAD";
            if (parts.SequenceEqual(new[] { "mods.json" }) && get)
            {
                string role = Who(request, address);
                return Json(200, Manifest(role == "admin"));
            }
            if (parts.Length >= 3 && parts[0] == "mods" && get)
            {
                Who(request, address);
                return FileReply(parts[1], string.Join('/', parts.Skip(2)), request);
            }
            if (parts.Length >= 2 && parts[0] == "upload" && method is "PUT" or "POST" or "DELETE")
            {
                Who(request, address, admin: true);
                throw new ModHostRefused(403, "uploads are switched off on this server: it is hosted from a launcher");
            }
            if (parts.SequenceEqual(new[] { "admin", "restart" }) && method is "POST" or "DELETE")
            {
                Who(request, address, admin: true);
                throw new ModHostRefused(403, "a server hosted from a launcher restarts from that launcher's Server manager");
            }
            if (parts.SequenceEqual(new[] { "status" }) && get)
            {
                Who(request, address);
                return Json(200, Status(Query(request.Target).GetValueOrDefault("token")));
            }
            if (parts.Length == 2 && parts[0] == "lobby" && method == "POST")
            {
                Who(request, address);
                return Json(200, LobbyAction(parts[1], request, address));
            }
            if (parts.SequenceEqual(new[] { "admin", "match" }) && method is "POST" or "DELETE")
            {
                Who(request, address, admin: true);
                return Json(200, await MatchAsync(request, ct).ConfigureAwait(false));
            }
            if (parts.SequenceEqual(new[] { "missions.json" }) && get)
            {
                Who(request, address);
                string json = _options.Missions?.Invoke() ?? throw new ModHostRefused(404, "the server has no map list yet");
                return new Reply { Body = Encoding.UTF8.GetBytes(json), Headers = { ("Cache-Control", "no-store") } };
            }
            throw new ModHostRefused(404, "not found");
        }
        catch (ModHostRefused refused)
        {
            var reply = Json(refused.Status, Error(refused.Message));
            if (refused.Status == 401) reply.Headers.Add(("WWW-Authenticate", "Bearer realm=\"mods\""));
            return reply;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Json(500, Error("the server couldn't do that"));
        }
    }

    private string Who(Request request, string address, bool admin = false)
    {
        string? role = _gate.Role(request.Headers.GetValueOrDefault("Authorization"), address);
        if (role == "locked") throw new ModHostRefused(429, "too many wrong passwords; wait 30 seconds");
        if (role == null) throw new ModHostRefused(401, "password");
        if (admin && role != "admin") throw new ModHostRefused(403, "that needs the admin password");
        return role;
    }

    private Dictionary<string, object?> Manifest(bool admin)
    {
        var reply = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["server"] = _options.ServerName,
            ["openswbf2"] = _options.Version,
            ["uploads"] = false,
            ["restart"] = "none",
            ["folders"] = Library.Scan().Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["size"] = f.Size,
                ["source"] = "server",
                ["files"] = f.Files.Select(file => new Dictionary<string, object?> { ["path"] = file.Path, ["size"] = file.Size, ["sha256"] = file.Sha256 }).ToList(),
            }).ToList(),
        };
        if (admin) reply["pending"] = Array.Empty<object>();
        return reply;
    }

    private Dictionary<string, object?> Status(string? token)
    {
        bool mine = token != null && Lobby.Touch(token);
        var reply = new Dictionary<string, object?> { ["schema"] = 1, ["server"] = _options.ServerName };
        foreach (var (key, value) in Watch.Describe()) reply[key] = value;
        reply["lobby"] = Lobby.List().Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["state"] = p.State, ["progress"] = p.Progress }).ToList();
        reply["inLobby"] = mine;
        return reply;
    }

    private Dictionary<string, object?> LobbyAction(string action, Request request, string address)
    {
        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            body = doc.RootElement.Clone();
        }
        catch (JsonException) { throw new ModHostRefused(400, "the body isn't JSON"); }
        if (body.ValueKind != JsonValueKind.Object) throw new ModHostRefused(400, "the body is an object");
        JsonElement Field(string name) => body.TryGetProperty(name, out var value) ? value : default;
        Dictionary<string, object?> reply;
        switch (action)
        {
            case "join":
                reply = new Dictionary<string, object?> { ["token"] = Lobby.Join(Field("name"), address) };
                break;
            case "update":
                Lobby.Update(Field("token"), Field("state"), Field("progress"));
                reply = new Dictionary<string, object?> { ["ok"] = true };
                break;
            case "leave":
                Lobby.Leave(Field("token"));
                reply = new Dictionary<string, object?> { ["ok"] = true };
                break;
            default:
                throw new ModHostRefused(404, "not found");
        }
        Watch.Wake();
        return reply;
    }

    private async Task<Dictionary<string, object?>> MatchAsync(Request request, CancellationToken ct)
    {
        if (request.Method == "DELETE") return Watch.Cancel();
        return Query(request.Target).GetValueOrDefault("do") switch
        {
            "start" => await Watch.StartAsync(ct).ConfigureAwait(false),
            "lobby" => await Watch.BackToLobbyAsync(ct).ConfigureAwait(false),
            _ => throw new ModHostRefused(400, "do is start or lobby"),
        };
    }

    private Reply FileReply(string folder, string path, Request request)
    {
        var file = Library.Lookup(folder, path) ?? throw new ModHostRefused(404, "not found");
        long size;
        try { size = new FileInfo(file.FullPath).Length; }
        catch (IOException) { throw new ModHostRefused(404, "not found"); }
        if (!File.Exists(file.FullPath)) throw new ModHostRefused(404, "not found");
        if (size != file.Size) throw new ModHostRefused(503, "that file just changed; read mods.json again");

        var reply = new Reply { ContentType = "application/octet-stream", File = file, Start = 0, Length = size };
        string? range = request.Headers.GetValueOrDefault("Range");
        string? ifRange = request.Headers.GetValueOrDefault("If-Range");
        if (range != null && (ifRange == null || ifRange.Trim('"') == file.Sha256))
        {
            var wanted = ParseRange(range, size);
            if (wanted == Unsatisfiable)
                return new Reply { Status = 416, ContentType = "", Headers = { ("Content-Range", $"bytes */{size}") } };
            if (wanted is { } r)
            {
                reply.Status = 206;
                reply.Start = r.Start;
                reply.Length = r.End - r.Start + 1;
                reply.Headers.Add(("Content-Range", $"bytes {r.Start}-{r.End}/{size}"));
            }
        }
        reply.Headers.Add(("Accept-Ranges", "bytes"));
        reply.Headers.Add(("ETag", $"\"{file.Sha256}\""));
        return reply;
    }

    private static readonly (long Start, long End) Unsatisfiable = (-1, -1);

    /// <summary>A single byte range, Unsatisfiable, or null to send the whole file (as modserver.py).</summary>
    public static (long Start, long End)? ParseRange(string header, long size)
    {
        int eq = header.IndexOf('=');
        if (eq < 0) return null;
        string unit = header[..eq].Trim(), spec = header[(eq + 1)..].Trim();
        if (!unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || spec.Contains(',')) return null;
        int dash = spec.IndexOf('-');
        if (dash < 0) return null;
        string first = spec[..dash], last = spec[(dash + 1)..];
        static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
        if (first == "")
        {
            if (!Digits(last) || !long.TryParse(last, out long count)) return null;
            if (count == 0) return Unsatisfiable;
            return (Math.Max(0, size - count), size - 1);
        }
        if (!Digits(first) || (last != "" && !Digits(last))) return null;
        if (!long.TryParse(first, out long start)) return null;
        long end = last == "" ? size - 1 : (long.TryParse(last, out long l) ? Math.Min(l, size - 1) : size - 1);
        if (start >= size) return Unsatisfiable;
        if (end < start) return null;
        return (start, end);
    }

    /// <summary>The decoded segments of a request path, or null when one could leave its folder
    /// (modserver.py's split_url): each segment is percent-decoded as strict UTF-8 on its own.</summary>
    public static string[]? SplitUrl(string target)
    {
        if (target.Length > MaxUrl) return null;
        string path = target.Split('?', 2)[0];
        if (!path.StartsWith('/')) return null;
        var parts = new List<string>();
        foreach (string segment in path[1..].Split('/'))
        {
            string? name = Unescape(segment);
            if (name == null || !SafeSegment(name)) return null;
            parts.Add(name);
        }
        return parts.ToArray();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static string? Unescape(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            // As Python's unquote: a % not followed by two hex digits stays as it is
            if (text[i] == '%' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
            }
        }
        try { return StrictUtf8.GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>A path segment every launcher can write (modserver.py's safe_segment).</summary>
    public static bool SafeSegment(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && !name.EndsWith('.') && !name.EndsWith(' ') &&
        !name.Any(c => c < 0x20 || "<>:\"/\\|?*".Contains(c)) && ServerModsManifest.IsSafeModPath(name);

    private static Dictionary<string, string> Query(string target)
    {
        var values = new Dictionary<string, string>();
        int question = target.IndexOf('?');
        if (question < 0) return values;
        foreach (string pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            values.TryAdd(key, Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
        }
        return values;
    }

    // Replies

    private static Dictionary<string, object?> Error(string text) => new() { ["error"] = text };

    private static Reply Json(int status, Dictionary<string, object?> body, bool close = false) => new()
    {
        Status = status,
        Body = JsonSerializer.SerializeToUtf8Bytes(body),
        Headers = { ("Cache-Control", "no-store") },
        Close = close,
    };

    private async Task WriteAsync(NetworkStream stream, Reply reply, bool head, CancellationToken ct)
    {
        long length = reply.File != null ? reply.Length : reply.Body.Length;
        var text = new StringBuilder();
        text.Append($"HTTP/1.1 {reply.Status} {Reason(reply.Status)}\r\n");
        text.Append("Server: OpenBattlefront-mods/1\r\n");
        if (reply.ContentType.Length > 0) text.Append($"Content-Type: {reply.ContentType}\r\n");
        text.Append($"Content-Length: {length}\r\n");
        foreach (var (name, value) in reply.Headers) text.Append($"{name}: {value}\r\n");
        if (reply.Close) text.Append("Connection: close\r\n");
        text.Append("\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(text.ToString()), ct).ConfigureAwait(false);
        if (head) return;
        if (reply.File == null)
        {
            await stream.WriteAsync(reply.Body, ct).ConfigureAwait(false);
            return;
        }
        await using var file = new FileStream(reply.File.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Seek(reply.Start, SeekOrigin.Begin);
        var buffer = new byte[Chunk];
        long left = reply.Length;
        while (left > 0)
        {
            int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(Chunk, left)), ct).ConfigureAwait(false);
            if (read == 0) break;
            await _bucket.TakeAsync(read, ct).ConfigureAwait(false);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(IdleTimeout);
            await stream.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
            left -= read;
        }
        if (left > 0) throw new IOException("the file ended early");
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Payload Too Large",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        503 => "Service Unavailable",
        _ => "Error",
    };

    /// <summary>Who a request is: the admin password, the join password (or none on a server without
    /// one), or nobody; five wrong passwords in ten seconds lock an address out for thirty.</summary>
    private sealed class Gate
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);
        private const int Fails = 5;
        private readonly byte[]? _join, _admin;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, Queue<DateTime>> _fails = new();
        private readonly Dictionary<string, DateTime> _locked = new();
        private readonly object _lock = new();

        public Gate(string join, string admin, Func<DateTime> clock)
        {
            _join = join.Length > 0 ? Digest(join) : null;
            _admin = admin.Length > 0 ? Digest(admin) : null;
            _clock = clock;
        }

        // Comparing digests takes the same time whatever the length of the guess
        private static byte[] Digest(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

        public string? Role(string? header, string address)
        {
            var now = _clock();
            lock (_lock)
            {
                if (_locked.TryGetValue(address, out var until) && until > now) return "locked";
            }
            string value = header ?? "";
            int space = value.IndexOf(' ');
            string scheme = space < 0 ? value : value[..space];
            string given = space < 0 ? "" : value[(space + 1)..].Trim();
            if (!scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || given.Length == 0) return _join == null ? "player" : null;
            byte[] digest = Digest(given);
            bool isAdmin = _admin != null && CryptographicOperations.FixedTimeEquals(digest, _admin);
            bool isJoin = _join != null && CryptographicOperations.FixedTimeEquals(digest, _join);
            if (isAdmin) return "admin";
            if (isJoin) return "player";
            lock (_lock)
            {
                if (!_fails.TryGetValue(address, out var fails)) _fails[address] = fails = new Queue<DateTime>();
                fails.Enqueue(now);
                while (fails.Count > 0 && now - fails.Peek() > Window) fails.Dequeue();
                if (fails.Count >= Fails)
                {
                    fails.Clear();
                    _locked[address] = now + Lockout;
                }
            }
            return null;
        }
    }

    /// <summary>One download speed limit shared by everyone, in bytes a second (0: none).</summary>
    private sealed class Bucket
    {
        private readonly double _rate, _burst;
        private readonly object _lock = new();
        private double _tokens;
        private DateTime _last = DateTime.UtcNow;

        public Bucket(double rate)
        {
            _rate = rate;
            _burst = Math.Max(rate / 4, Chunk);
            _tokens = _burst;
        }

        public Task TakeAsync(int count, CancellationToken ct)
        {
            if (_rate <= 0) return Task.CompletedTask;
            double wait;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                _tokens = Math.Min(_burst, _tokens + (now - _last).TotalSeconds * _rate);
                _last = now;
                _tokens -= count;
                wait = _tokens < 0 ? -_tokens / _rate : 0;
            }
            return wait > 0 ? Task.Delay(TimeSpan.FromSeconds(wait), ct) : Task.CompletedTask;
        }
    }
}
