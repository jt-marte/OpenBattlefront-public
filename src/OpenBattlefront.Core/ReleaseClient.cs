using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

public sealed record ReleaseInfo(string Version, string Tag, string AssetName, string DownloadUrl, long Size, string? Sha256Url, string? PageUrl);

/// <summary>Reads the latest OpenSWBF2 release from GitHub and downloads its x64 package.</summary>
public sealed partial class ReleaseClient
{
    private readonly HttpClient _http;
    private readonly string _repo;

    public ReleaseClient(string repo, HttpClient http)
    {
        _repo = repo;
        _http = http;
    }

    public static HttpClient CreateHttp(Version launcherVersion)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBattlefront", launcherVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The newest release with an x64 package, or null when the repo has none yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{_repo}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("GitHub is rate-limiting update checks. Try again in a few minutes.");
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string? page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        ReleaseInfo? found = null;
        var shaUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                shaUrls[name[..^".sha256".Length]] = url;
                continue;
            }
            var match = PackageName().Match(name);
            if (match.Success && found == null)
                found = new ReleaseInfo(match.Groups[1].Value, tag, name, url, asset.GetProperty("size").GetInt64(), null, page);
        }
        if (found == null) return null;
        return shaUrls.TryGetValue(found.AssetName, out var sha) ? found with { Sha256Url = sha } : found;
    }

    public static bool IsNewer(string candidate, string? installed) =>
        installed == null || (System.Version.TryParse(candidate, out var a) && System.Version.TryParse(installed, out var b) && a > b);

    public async Task DownloadAsync(ReleaseInfo release, string destination, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".partial";
        using (var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            var buffer = new byte[1 << 16];
            var clock = Stopwatch.StartNew();
            long done = 0, last = -1000;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (clock.ElapsedMilliseconds - last >= 100)
                {
                    last = clock.ElapsedMilliseconds;
                    progress?.Report(new TransferProgress(done, total, 0, 1, release.AssetName, done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
                }
            }
        }

        if (release.Sha256Url != null)
        {
            string expected = (await _http.GetStringAsync(release.Sha256Url, ct).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.Equals(Hashing.Sha256(partial), expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("The download is damaged (SHA-256 does not match). Try again.");
            }
        }
        File.Move(partial, destination, overwrite: true);
    }

    [GeneratedRegex(@"^OpenSWBF2-(\d+\.\d+\.\d+)-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageName();
}
