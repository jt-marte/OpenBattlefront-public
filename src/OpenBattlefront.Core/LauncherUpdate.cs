using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

/// <summary>The launcher's own updates. Each release also puts OpenBattlefront.exe (the exe alone,
/// none of the included content) and its .sha256 on the public jt-marte/OpenBattlefront-public
/// repo, which anyone can read; the full zip stays on the private repo. Updating swaps the running
/// exe for the new one: Windows lets a running exe be renamed, not overwritten.</summary>
public static partial class LauncherUpdate
{
    public const string Repo = "jt-marte/OpenBattlefront-public";
    public const string ExeName = "OpenBattlefront.exe";

    /// <summary>Where the newest release is read: GitHub's, or for testing another releases/latest
    /// reply named by OBF_UPDATE_URL.</summary>
    public static string LatestUrl =>
        Environment.GetEnvironmentVariable("OBF_UPDATE_URL") is { Length: > 0 } test ? test : $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>The newest launcher release, as ReleaseClient downloads it (checked against its
    /// SHA-256), or null when there is none or it lacks the exe or its .sha256.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(LatestUrl, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("GitHub is rate-limiting update checks. Try again in a few minutes.");
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = TagVersion().Match(tag);
        if (!version.Success || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        string? page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

        string? exeUrl = null, shaUrl = null;
        long size = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = url;
                size = asset.GetProperty("size").GetInt64();
            }
            else if (name.Equals(ExeName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = url;
            }
        }
        // Never an exe that can't be checked
        if (exeUrl == null || shaUrl == null) return null;
        return new ReleaseInfo(version.Groups[1].Value, tag, ExeName, exeUrl, size, shaUrl, page);
    }

    public static bool IsNewer(ReleaseInfo release, Version running) =>
        Version.TryParse(release.Version, out var candidate) && candidate > new Version(running.Major, running.Minor, Math.Max(running.Build, 0));

    /// <summary>Whether the folder the launcher runs from can take the new exe (not, say, a
    /// read-only or Program Files folder).</summary>
    public static bool CanReplace(string exe)
    {
        string? folder = Path.GetDirectoryName(exe);
        if (folder == null) return false;
        string probe = Path.Combine(folder, $".update-test-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Puts <paramref name="downloaded"/> in place of the running <paramref name="exe"/>,
    /// which moves aside to exe.old (deleted at the next start). On failure the old exe is put back.</summary>
    public static void Swap(string exe, string downloaded)
    {
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    /// <summary>Deletes what an update left behind: exe.old, once the old launcher has exited.</summary>
    public static void CleanUp(string exe)
    {
        foreach (string leftover in new[] { exe + ".old", exe + ".download" })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(@"^v?(\d+\.\d+\.\d+)$")]
    private static partial Regex TagVersion();
}
