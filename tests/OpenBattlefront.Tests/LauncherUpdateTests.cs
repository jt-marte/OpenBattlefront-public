using System.Net;
using System.Text;
using OpenBattlefront.Core;
using Xunit;

namespace OpenBattlefront.Tests;

public class LauncherUpdateTests
{
    private static string Release(string tag, params string[] assets) =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/jt-marte/OpenBattlefront-public/releases/tag/" + tag + "\",\"assets\":[" +
        string.Join(",", assets.Select(name =>
            "{\"name\":\"" + name + "\",\"size\":69000000,\"browser_download_url\":\"https://example.test/" + name + "\"}")) + "]}";

    [Fact]
    public void A_release_needs_the_exe_and_its_sha256()
    {
        var release = LauncherUpdate.Parse(Release("v0.3.3", "OpenBattlefront.exe", "OpenBattlefront.exe.sha256"));
        Assert.NotNull(release);
        Assert.Equal("0.3.3", release!.Version);
        Assert.Equal("https://example.test/OpenBattlefront.exe", release.DownloadUrl);
        Assert.Equal("https://example.test/OpenBattlefront.exe.sha256", release.Sha256Url);
        Assert.Equal(69000000, release.Size);

        // Nothing that can't be checked, nothing but the exe, no tag that isn't a version
        Assert.Null(LauncherUpdate.Parse(Release("v0.3.3", "OpenBattlefront.exe")));
        Assert.Null(LauncherUpdate.Parse(Release("v0.3.3", "OpenBattlefront-0.3.3.zip", "OpenBattlefront-0.3.3.zip.sha256")));
        Assert.Null(LauncherUpdate.Parse(Release("nightly", "OpenBattlefront.exe", "OpenBattlefront.exe.sha256")));
        Assert.Equal("0.4.0", LauncherUpdate.Parse(Release("0.4.0", "OpenBattlefront.exe", "OpenBattlefront.exe.sha256"))!.Version);
    }

    [Fact]
    public void Only_a_higher_version_is_an_update()
    {
        var release = LauncherUpdate.Parse(Release("v0.3.3", "OpenBattlefront.exe", "OpenBattlefront.exe.sha256"))!;
        Assert.True(LauncherUpdate.IsNewer(release, new Version(0, 3, 2)));
        Assert.True(LauncherUpdate.IsNewer(release, new Version(0, 3, 2, 0)));
        Assert.False(LauncherUpdate.IsNewer(release, new Version(0, 3, 3, 0)));
        Assert.False(LauncherUpdate.IsNewer(release, new Version(0, 4, 0)));
    }

    [Fact]
    public void Swapping_puts_the_new_exe_in_place_and_keeps_the_old_aside()
    {
        using var dir = new TempDir();
        string exe = dir.File("OpenBattlefront.exe", "old");
        dir.File("OpenBattlefront.exe.old", "older still");
        LauncherUpdate.Swap(exe, dir.File("OpenBattlefront.exe.download", "new"));
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".download"));

        LauncherUpdate.CleanUp(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.True(LauncherUpdate.CanReplace(exe));
    }

    [Fact]
    public void A_failed_swap_puts_the_old_exe_back()
    {
        using var dir = new TempDir();
        string exe = dir.File("OpenBattlefront.exe", "old");
        Assert.ThrowsAny<IOException>(() => LauncherUpdate.Swap(exe, dir.Sub("missing.download")));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_sha256_is_refused()
    {
        using var dir = new TempDir();
        byte[] exe = Encoding.UTF8.GetBytes("the new launcher");
        string good = TempDir.Sha("the new launcher");
        var release = LauncherUpdate.Parse(Release("v0.3.3", "OpenBattlefront.exe", "OpenBattlefront.exe.sha256"))!;
        foreach (var (sha, ok) in new[] { (good, true), (new string('0', 64), false) })
        {
            using var http = new HttpClient(new Answer(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256")
                    ? new StringContent(sha + " *OpenBattlefront.exe\n")
                    : new ByteArrayContent(exe),
            }));
            string destination = dir.Sub("OpenBattlefront.exe.download");
            var download = new ReleaseClient(LauncherUpdate.Repo, http).DownloadAsync(release, destination, null, default);
            if (ok)
            {
                await download;
                Assert.Equal(exe, File.ReadAllBytes(destination));
            }
            else
            {
                File.Delete(destination);
                await Assert.ThrowsAsync<InvalidDataException>(() => download);
                Assert.False(File.Exists(destination));
            }
        }
    }

    private sealed class Answer : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_answer(request));
    }
}
