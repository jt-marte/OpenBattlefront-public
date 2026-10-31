using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenBattlefront.Core;

/// <param name="Path">Relative to the mod folder, with forward slashes.</param>
public sealed record LocalModFile(string Path, long Size, string Sha256, string FullPath);

/// <summary>A mod folder of this PC, ready to upload to a server: every file with its SHA-256.
/// Programs, and names the server can't take, stay behind (LeftOut names them).</summary>
public sealed record ModUpload(string Folder, IReadOnlyList<LocalModFile> Files, IReadOnlyList<string> LeftOut)
{
    public const long MaxFile = 2L << 30;
    public const long MaxTotal = 8L << 30;

    public long Size => Files.Sum(f => f.Size);

    public static ModUpload Prepare(string dir, FileHashCache hashes)
    {
        string folder = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('\\'));
        if (ScriptMods.IsPatchAddOn(folder))
            throw new InvalidDataException("The unofficial 1.3 patch is never uploaded: OpenSWBF2 has its support built in.");
        if (!ServerModsManifest.IsSafeFolderName(folder))
            throw new InvalidDataException($"A server takes mod folder names of letters, digits, _ . and - only, not {folder}.");
        if (!File.Exists(Path.Combine(dir, ModManager.AddMe)))
            throw new InvalidDataException($"{folder} has no {ModManager.AddMe}, so it isn't a mod folder the game loads.");

        var files = new List<LocalModFile>();
        var leftOut = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.GetRelativePath(dir, full).Replace('\\', '/');
            if (GameContent.IsProgram(path) || !ServerModsManifest.IsSafeModPath(path) || !seen.Add(path))
            {
                leftOut.Add(path);
                continue;
            }
            long size = new FileInfo(full).Length;
            if (size > MaxFile) throw new InvalidDataException($"{folder}/{path} is over 2 GB, more than a server takes.");
            files.Add(new LocalModFile(path, size, hashes.Sha256(full), full));
        }
        hashes.Save();
        var upload = new ModUpload(folder, files, leftOut);
        if (upload.Size > MaxTotal) throw new InvalidDataException($"{folder} is over 8 GB, more than a server takes.");
        return upload;
    }
}

public enum ServerModState
{
    /// <summary>The server has the same files.</summary>
    Same,

    /// <summary>The server has another version.</summary>
    Different,

    NotOnServer,
}

public static class ModCompare
{
    /// <summary>How a local mod stands against the server's copy of the same name.</summary>
    public static ServerModState Compare(ModUpload local, ServerModsManifest server)
    {
        var theirs = server.Folders.FirstOrDefault(f => string.Equals(f.Name, local.Folder, StringComparison.OrdinalIgnoreCase));
        if (theirs == null) return ServerModState.NotOnServer;
        var files = theirs.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        bool same = files.Count == local.Files.Count &&
            local.Files.All(f => files.TryGetValue(f.Path, out var sha) && string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase));
        return same ? ServerModState.Same : ServerModState.Different;
    }
}

/// <summary>An admin's side of a server's mod service: its mod list with what waits for a restart,
/// uploads (resumed file by file), removing uploaded mods, and restarting the game to load them.</summary>
public sealed class ModUploadClient
{
    private readonly HttpClient _http;
    private readonly string _password;

    public ModUploadClient(HttpClient http, ServerAddress server, string adminPassword)
    {
        _http = http;
        Server = server;
        _password = adminPassword;
    }

    public ServerAddress Server { get; }

    public async Task<ServerModsManifest> GetManifestAsync(CancellationToken ct)
    {
        var reply = await ModService.GetManifestAsync(_http, Server, _password, ct).ConfigureAwait(false);
        return reply.Status switch
        {
            ModServiceStatus.Ok => reply.Manifest!,
            ModServiceStatus.NeedsPassword => throw new ModServiceException(HttpStatusCode.Unauthorized, "Wrong admin password."),
            _ => throw new ModServiceException(null,
                $"No mod service answers at {Server} (TCP {Server.Port}). The Docker server needs MOD_SHARING on and TCP {Server.Port} mapped and forwarded."),
        };
    }

    public async Task UploadAsync(ModUpload upload, IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        string title = $"Uploading {upload.Folder}";
        var listing = new { files = upload.Files.Select(f => new { path = f.Path, size = f.Size, sha256 = f.Sha256 }) };
        progress?.Report(new StepUpdate(title, "Checking what the server already has"));
        using var begin = await SendAsync(HttpMethod.Post, $"upload/{Uri.EscapeDataString(upload.Folder)}/begin", Json(listing), ct).ConfigureAwait(false);
        var have = new HashSet<string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
            if (doc.RootElement.TryGetProperty("have", out var list))
                foreach (var item in list.EnumerateArray())
                    have.Add(item.GetString() ?? "");

        var send = upload.Files.Where(f => !have.Contains(f.Path)).ToList();
        long total = send.Sum(f => f.Size), done = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in send)
        {
            ct.ThrowIfCancellationRequested();
            long before = done;
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var counting = new CountingStream(stream, read =>
            {
                done = before + read;
                double perSecond = done / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                progress?.Report(new StepUpdate(title, $"{file.Path}  ·  {Sizes.Format(done)} of {Sizes.Format(total)}  ·  {Sizes.Format((long)perSecond)}/s",
                    total > 0 ? (double)done / total : 1));
            });
            var content = new StreamContent(counting, 1 << 16);
            content.Headers.ContentLength = file.Size;
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var put = ModService.Request(HttpMethod.Put, Server, ModService.FilePath("upload", upload.Folder, file.Path), _password);
            put.Headers.Add("X-Sha256", file.Sha256);
            put.Content = content;
            using var response = await _http.SendAsync(put, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await ModService.ErrorAsync(response, ct).ConfigureAwait(false);
            done = before + file.Size;
        }

        progress?.Report(new StepUpdate(title, "Checking the upload on the server"));
        using var _ = await SendAsync(HttpMethod.Post, $"upload/{Uri.EscapeDataString(upload.Folder)}/commit", Json(listing), ct).ConfigureAwait(false);
    }

    /// <summary>Removes a mod an admin uploaded; it unloads when the game restarts.</summary>
    public async Task RemoveAsync(string folder, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"upload/{Uri.EscapeDataString(folder)}", null, ct).ConfigureAwait(false);
    }

    /// <summary>Restarts the game now (players are dropped) or once nobody is on. Returns the
    /// service's restart state: "none" or "when-empty".</summary>
    public Task<string> RestartAsync(bool whenEmpty, CancellationToken ct) =>
        RestartStateAsync(HttpMethod.Post, whenEmpty ? "admin/restart?when=empty" : "admin/restart?when=now", ct);

    public Task<string> CancelRestartAsync(CancellationToken ct) => RestartStateAsync(HttpMethod.Delete, "admin/restart", ct);

    private async Task<string> RestartStateAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, null, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("restart", out var state) ? state.GetString() ?? "none" : "none";
    }

    /// <summary>JSON with a Content-Length, which the service needs (no chunked bodies).</summary>
    private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = ModService.Request(method, Server, path, _password);
        request.Content = content;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ModServiceException(null, $"Couldn't reach the mod service at {Server}: {e.Message}", e);
        }
        if (response.IsSuccessStatusCode) return response;
        using (response)
            throw await ModService.ErrorAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Reports how much of a file the upload has read.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _read;
        private long _position;

        public CountingStream(Stream inner, Action<long> read)
        {
            _inner = inner;
            _read = read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await _inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int read)
        {
            _position += read;
            _read(_position);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
