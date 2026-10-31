using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenBattlefront.Core;

/// <summary>A mod service refused or failed a request; the message is ready to show.</summary>
public sealed class ModServiceException : Exception
{
    public ModServiceException(HttpStatusCode? status, string message, Exception? inner = null) : base(message, inner) => Status = status;

    public HttpStatusCode? Status { get; }
}

public enum ModServiceStatus
{
    Ok,

    /// <summary>The server has a join password and none, or a wrong one, was given.</summary>
    NeedsPassword,

    /// <summary>Nothing serves mods there: an older or launcher-hosted server, or the TCP port isn't open.</summary>
    Unavailable,
}

public sealed record ModServiceReply(ModServiceStatus Status, ServerModsManifest? Manifest = null);

/// <summary>Speaks to a server's mod service (the Docker server's modserver.py): HTTP on the game's
/// port number over TCP, the join or admin password as a Bearer token.</summary>
public static class ModService
{
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);

    public static HttpClient CreateHttp(Version launcherVersion, HttpMessageHandler? handler = null)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBattlefront", launcherVersion.ToString(3)));
        return http;
    }

    public static HttpRequestMessage Request(HttpMethod method, ServerAddress server, string path, string? password)
    {
        var request = new HttpRequestMessage(method, new Uri(server.ModsBase, path));
        if (!string.IsNullOrEmpty(password)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", password);
        return request;
    }

    /// <summary>A mod file's URL path, each part escaped: mods/BF1/data/_lvl_pc/bf1.lvl.</summary>
    public static string FilePath(string prefix, string folder, string path) =>
        prefix + "/" + Uri.EscapeDataString(folder) + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>The server's mod list. Unavailable when no mod service answers within 5 seconds
    /// (refused, timed out, or another kind of web server).</summary>
    public static async Task<ModServiceReply> GetManifestAsync(HttpClient http, ServerAddress server, string? password, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ListTimeout);
        try
        {
            using var request = Request(HttpMethod.Get, server, "mods.json", password);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new ModServiceReply(ModServiceStatus.NeedsPassword);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw await ErrorAsync(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
                return new ModServiceReply(ModServiceStatus.Unavailable);
            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new ModServiceReply(ModServiceStatus.Ok, ServerModsManifest.Parse(json));
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            return new ModServiceReply(ModServiceStatus.Unavailable);
        }
    }

    /// <summary>The service's {"error": "..."} reply as an exception to show.</summary>
    public static async Task<ModServiceException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("error", out var value)) error = value.GetString();
        }
        catch (JsonException)
        {
        }
        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Wrong password.",
            HttpStatusCode.TooManyRequests when error?.StartsWith("too many", StringComparison.OrdinalIgnoreCase) == true =>
                "Too many wrong passwords. Wait 30 seconds, then try again.",
            _ when !string.IsNullOrWhiteSpace(error) => char.ToUpperInvariant(error[0]) + error[1..] + (error.EndsWith('.') ? "" : "."),
            _ => $"The server's mod service answered {(int)response.StatusCode} ({response.ReasonPhrase}).",
        };
        return new ModServiceException(response.StatusCode, message);
    }
}

/// <summary>Gets a server's mods for a join: downloads what the plan lists (resuming a cut
/// download), checks every file's SHA-256, then puts each mod in AddOn\. A new mod moves in whole;
/// an update replaces only its differing files, the old ones going to the Recycle Bin.</summary>
public sealed class ModSyncClient
{
    public const string Title = "Getting the server's mods";
    private const long SpareBytes = 512L << 20;
    private const int Attempts = 3;
    private readonly HttpClient _http;
    private readonly InstallLayout _layout;
    private readonly Action<string> _remove;

    /// <param name="remove">How a file an update replaces goes away; the Recycle Bin by default.</param>
    public ModSyncClient(HttpClient http, InstallLayout layout, Action<string>? remove = null)
    {
        _http = http;
        _layout = layout;
        _remove = remove ?? RecycleBin.Delete;
    }

    /// <summary>Where a server's files wait until they are checked and put in place.</summary>
    public string DownloadDir(ServerAddress server) => Path.Combine(_layout.Downloads, "server-mods", server.Key);

    public async Task InstallAsync(ServerAddress server, string? password, ModSyncPlan plan, IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        var needed = plan.Needed;
        Directory.CreateDirectory(_layout.Root);
        long need = plan.Bytes + SpareBytes;
        long free = Importer.FreeBytes(_layout.Root);
        if (need > free)
            throw new IOException($"Not enough disk space: the server's mods need {Sizes.Format(plan.Bytes)} and the drive has {Sizes.Format(free)} free.");

        var transfer = new Transfer(plan.Bytes, progress);
        foreach (var item in needed)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Action == ModSyncAction.Enable)
            {
                Enable(item.Name);
                continue;
            }
            string cache = Path.Combine(DownloadDir(server), item.Name);
            foreach (var file in item.Missing)
                await DownloadFileAsync(server, password, item.Name, file, Inside(cache, file.Path), transfer, ct).ConfigureAwait(false);
            progress?.Report(new StepUpdate(Title, $"Putting {item.Name} in place"));
            if (item.Action == ModSyncAction.New) PlaceNew(item, cache);
            else PlaceUpdate(item, cache);
        }
        // Downloads\server-mods\<server>, then server-mods itself, once nothing waits there
        for (string? dir = DownloadDir(server); dir != null && dir.StartsWith(_layout.Downloads + "\\", StringComparison.OrdinalIgnoreCase); dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
            Directory.Delete(dir);
        }
    }

    private void Enable(string name)
    {
        var mods = new ModManager(_layout);
        mods.SetEnabled(new ModInfo(name, Path.Combine(_layout.AddOnDisabled, name), false), true);
    }

    /// <summary>Every file of a new mod is downloaded and checked: the folder moves in whole.</summary>
    private void PlaceNew(ModSyncItem item, string cache)
    {
        string target = Path.Combine(_layout.AddOn, item.Name);
        if (Directory.Exists(target)) throw new IOException($"A mod named {item.Name} appeared while downloading. Try joining again.");
        var listed = new HashSet<string>(item.Folder.Files.Select(f => Inside(cache, f.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).ToList())
            if (!listed.Contains(file)) File.Delete(file);
        Directory.CreateDirectory(_layout.AddOn);
        Directory.Move(cache, target);
    }

    /// <summary>The player has another version: switch it on if it was off, then swap in the
    /// differing files.</summary>
    private void PlaceUpdate(ModSyncItem item, string cache)
    {
        string dir = Path.Combine(_layout.AddOn, item.Name);
        if (!Directory.Exists(dir)) Enable(item.Name);
        foreach (var file in item.Missing)
        {
            string from = Inside(cache, file.Path);
            string to = Inside(dir, file.Path);
            if (File.Exists(to)) _remove(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
        }
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
    }

    private async Task DownloadFileAsync(ServerAddress server, string? password, string folder, ManifestFile file, string target, Transfer transfer, CancellationToken ct)
    {
        if (File.Exists(target) && new FileInfo(target).Length == file.Size && IsSha(target, file))
        {
            transfer.Skip(file.Size);
            return;
        }
        string partial = target + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bool damaged = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await FetchAsync(server, password, folder, file, partial, transfer, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < Attempts && !ct.IsCancellationRequested && e is HttpRequestException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (IsSha(partial, file))
            {
                File.Move(partial, target, overwrite: true);
                transfer.Finish(file.Size);
                return;
            }
            File.Delete(partial);
            transfer.Restart();
            if (damaged) throw new InvalidDataException($"{folder}/{file.Path} arrived damaged twice. Try joining again later.");
            damaged = true;
        }
    }

    private async Task FetchAsync(ServerAddress server, string? password, string folder, ManifestFile file, string partial, Transfer transfer, CancellationToken ct)
    {
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > file.Size)
        {
            File.Delete(partial);
            have = 0;
        }
        transfer.Begin($"{folder}/{file.Path}", have);
        if (have == file.Size && have > 0) return;

        using var request = ModService.Request(HttpMethod.Get, server, ModService.FilePath("mods", folder, file.Path), password);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{file.Sha256.ToLowerInvariant()}\""));
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            throw new IOException("The server couldn't resume that download.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ModServiceException(response.StatusCode, $"The server no longer has {folder}/{file.Path}. Try joining again.");
        if (!response.IsSuccessStatusCode) throw await ModService.ErrorAsync(response, ct).ConfigureAwait(false);

        bool append = false;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != have)
            {
                File.Delete(partial);
                throw new IOException("The server resumed the download at the wrong place.");
            }
            append = true;
        }
        if (!append) transfer.Begin($"{folder}/{file.Path}", 0);

        var buffer = new byte[1 << 16];
        long written = append ? have : 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            written += read;
            if (written > file.Size) throw new InvalidDataException($"The server sent more of {folder}/{file.Path} than its mod list says.");
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            transfer.Advance(read);
        }
    }

    private static bool IsSha(string path, ManifestFile file) =>
        string.Equals(Hashing.Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>A mod file's place under <paramref name="root"/>, refusing any path that leaves it.</summary>
    public static string Inside(string root, string path)
    {
        string full = Path.GetFullPath(Path.Combine(root, path.Replace('/', '\\')));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe path in the server's mod list: {path}");
        return full;
    }

    /// <summary>Progress over every file: bytes done, the current file and the speed.</summary>
    private sealed class Transfer
    {
        private readonly long _total;
        private readonly IProgress<StepUpdate>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _done, _current, _received, _lastReport = -1000;
        private string _name = "";

        public Transfer(long total, IProgress<StepUpdate>? progress)
        {
            _total = total;
            _progress = progress;
        }

        public void Begin(string name, long already)
        {
            _name = name;
            _current = already;
            Report(force: true);
        }

        public void Advance(int count)
        {
            _current += count;
            _received += count;
            Report();
        }

        public void Finish(long size)
        {
            _done += size;
            _current = 0;
        }

        public void Skip(long size) => Finish(size);

        public void Restart() => _current = 0;

        private void Report(bool force = false)
        {
            if (_progress == null || (!force && _clock.ElapsedMilliseconds - _lastReport < 100)) return;
            _lastReport = _clock.ElapsedMilliseconds;
            long bytes = _done + _current;
            double perSecond = _received / Math.Max(_clock.Elapsed.TotalSeconds, 0.001);
            _progress.Report(new StepUpdate(Title, $"{_name}  ·  {Sizes.Format(bytes)} of {Sizes.Format(_total)}  ·  {Sizes.Format((long)perSecond)}/s",
                _total > 0 ? Math.Clamp((double)bytes / _total, 0, 1) : 0));
        }
    }
}
