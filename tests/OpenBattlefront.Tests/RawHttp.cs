using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenBattlefront.Tests;

/// <summary>HTTP/1.1 over a plain socket, so a test sends a path exactly as written (HttpClient
/// would tidy "/a/../b" and percent escapes before they reach the server).</summary>
internal sealed class RawHttp : IDisposable
{
    public sealed record Answer(int Status, Dictionary<string, string> Headers, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
        public string? Error => Body.Length > 0 && Json.TryGetProperty("error", out var e) ? e.GetString() : null;
    }

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly List<byte> _pending = new();

    public RawHttp(int port)
    {
        _client = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 15000 };
        _stream = _client.GetStream();
    }

    public static Answer Send(int port, string method, string target, string? password = null, object? json = null,
        IDictionary<string, string>? headers = null)
    {
        using var http = new RawHttp(port);
        return http.Request(method, target, password, json, headers);
    }

    public Answer Request(string method, string target, string? password = null, object? json = null, IDictionary<string, string>? headers = null)
    {
        byte[] body = json == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(json);
        var head = new StringBuilder($"{method} {target} HTTP/1.1\r\nHost: test\r\n");
        if (password != null) head.Append($"Authorization: Bearer {password}\r\n");
        if (json != null) head.Append($"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        _stream.Write(Encoding.Latin1.GetBytes(head.ToString()));
        _stream.Write(body);
        return Read(method == "HEAD");
    }

    private Answer Read(bool head)
    {
        string text = ReadHead();
        string[] lines = text.Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        int length = head ? 0 : int.Parse(headers.GetValueOrDefault("Content-Length", "0"));
        return new Answer(status, headers, Take(length));
    }

    private string ReadHead()
    {
        while (true)
        {
            int end = IndexOfBlank();
            if (end >= 0)
            {
                string head = Encoding.Latin1.GetString(_pending.GetRange(0, end).ToArray());
                _pending.RemoveRange(0, end + 4);
                return head;
            }
            Fill();
        }
    }

    private int IndexOfBlank()
    {
        for (int i = 0; i + 3 < _pending.Count; i++)
            if (_pending[i] == '\r' && _pending[i + 1] == '\n' && _pending[i + 2] == '\r' && _pending[i + 3] == '\n') return i;
        return -1;
    }

    private byte[] Take(int length)
    {
        while (_pending.Count < length) Fill();
        byte[] body = _pending.GetRange(0, length).ToArray();
        _pending.RemoveRange(0, length);
        return body;
    }

    private void Fill()
    {
        var buffer = new byte[65536];
        int read = _stream.Read(buffer);
        if (read == 0) throw new IOException("The server closed the connection.");
        _pending.AddRange(buffer.AsSpan(0, read).ToArray());
    }

    public void Dispose() => _client.Dispose();
}
