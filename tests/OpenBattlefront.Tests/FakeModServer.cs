using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

/// <summary>A mod service in memory that answers like modserver.py: mods.json, files with ranges,
/// uploads. It can cut a download short or send damaged bytes, and records every request.</summary>
internal sealed class FakeModServer : HttpMessageHandler
{
    public sealed record Seen(string Method, string Path, string? Auth, long? RangeFrom, string? IfRange, string? Sha256Header, long? ContentLength, byte[]? Body);

    public Dictionary<string, Dictionary<string, byte[]>> Mods { get; } = new(StringComparer.Ordinal);
    public string? JoinPassword { get; set; }
    public string AdminPassword { get; set; } = "Admin12345";
    public List<Seen> Requests { get; } = new();

    /// <summary>The next download of this path stops with an error after this many bytes.</summary>
    public (string Path, int Bytes)? CutOnce { get; set; }

    /// <summary>Downloads of these paths send other bytes, this many times.</summary>
    public Dictionary<string, int> Damage { get; } = new();

    /// <summary>The server sends whole files even when asked for a range.</summary>
    public bool IgnoreRanges { get; set; }

    /// <summary>Files already staged for an upload (begin replies with them).</summary>
    public HashSet<string> Staged { get; } = new();
    public Dictionary<string, byte[]> Uploaded { get; } = new();
    public string RestartState { get; set; } = "none";

    public void Add(string folder, string path, string content) => Add(folder, path, Encoding.UTF8.GetBytes(content));

    public void Add(string folder, string path, byte[] content)
    {
        if (!Mods.TryGetValue(folder, out var files)) Mods[folder] = files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        files[path] = content;
    }

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public string ManifestJson(bool admin = false) => JsonSerializer.Serialize(new
    {
        schema = 1,
        server = "Fake server",
        openswbf2 = "0.2.2",
        uploads = true,
        restart = RestartState,
        folders = Mods.Select(m => new
        {
            name = m.Key,
            size = m.Value.Values.Sum(d => (long)d.Length),
            source = "server",
            files = m.Value.Select(f => new { path = f.Key, size = (long)f.Value.Length, sha256 = Sha(f.Value) }),
        }),
        pending = admin ? Array.Empty<object>() : null,
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        byte[]? body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
        string? auth = request.Headers.Authorization?.Parameter;
        Requests.Add(new Seen(request.Method.Method, request.RequestUri.PathAndQuery, auth, request.Headers.Range?.Ranges.First().From,
            request.Headers.IfRange?.EntityTag?.Tag, request.Headers.TryGetValues("X-Sha256", out var sha) ? sha.First() : null,
            request.Content?.Headers.ContentLength, body));

        bool admin = auth == AdminPassword;
        bool player = admin || (JoinPassword == null ? auth == null : auth == JoinPassword);
        if (!player) return Json(HttpStatusCode.Unauthorized, new { error = "password" });

        if (path == "mods.json")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson(admin), Encoding.UTF8, "application/json") };
        if (path.StartsWith("mods/"))
            return File(path["mods/".Length..], request);
        if (!admin) return Json(HttpStatusCode.Forbidden, new { error = "that needs the admin password" });
        if (path.StartsWith("upload/") && path.EndsWith("/begin")) return Json(HttpStatusCode.OK, new { have = Staged.ToArray() });
        if (path.StartsWith("upload/") && path.EndsWith("/commit")) return Json(HttpStatusCode.OK, new { pending = true });
        if (path.StartsWith("upload/") && request.Method == HttpMethod.Put)
        {
            Uploaded[path["upload/".Length..]] = body!;
            return Json(HttpStatusCode.OK, new { ok = true });
        }
        if (path.StartsWith("upload/") && request.Method == HttpMethod.Delete) return Json(HttpStatusCode.OK, new { pending = true });
        if (path == "admin/restart")
        {
            RestartState = request.Method == HttpMethod.Delete || request.RequestUri.Query.Contains("now") ? "none" : "when-empty";
            return Json(HttpStatusCode.OK, new { restart = RestartState });
        }
        return Json(HttpStatusCode.NotFound, new { error = "not found" });
    }

    private HttpResponseMessage File(string folderAndPath, HttpRequestMessage request)
    {
        int slash = folderAndPath.IndexOf('/');
        string folder = folderAndPath[..slash], path = folderAndPath[(slash + 1)..];
        if (!Mods.TryGetValue(folder, out var files) || !files.TryGetValue(path, out var data))
            return Json(HttpStatusCode.NotFound, new { error = "not found" });
        if (Damage.TryGetValue(path, out int times) && times > 0)
        {
            Damage[path] = times - 1;
            data = data.Select(b => (byte)~b).ToArray();
        }

        long start = 0;
        var status = HttpStatusCode.OK;
        if (!IgnoreRanges && request.Headers.Range?.Ranges.FirstOrDefault()?.From is long from &&
            (request.Headers.IfRange == null || request.Headers.IfRange.EntityTag?.Tag == $"\"{Sha(files[path])}\""))
        {
            if (from >= data.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            start = from;
            status = HttpStatusCode.PartialContent;
        }
        byte[] part = data[(int)start..];
        Stream stream = new MemoryStream(part);
        if (CutOnce is { } cut && cut.Path == path)
        {
            CutOnce = null;
            stream = new CutStream(part, cut.Bytes);
        }
        var content = new StreamContent(stream);
        content.Headers.ContentLength = part.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (status == HttpStatusCode.PartialContent) content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>Gives the first bytes, then fails like a dropped connection.</summary>
    private sealed class CutStream : MemoryStream
    {
        private readonly int _cut;

        public CutStream(byte[] data, int cut) : base(data) => _cut = cut;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= _cut) throw new IOException("The connection dropped.");
            return base.Read(buffer, offset, (int)Math.Min(count, _cut - Position));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= _cut) throw new IOException("The connection dropped.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _cut - Position)], ct);
        }
    }
}
