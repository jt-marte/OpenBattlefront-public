using System.Net;
using System.Text;
using System.Text.Json;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class ModUploadTests
{
    private static readonly ServerAddress Local = new("127.0.0.1", 3700);

    private static string FakeMod(TempDir temp, string name = "MyMod")
    {
        temp.File($@"AddOn\{name}\addme.script", "addme");
        temp.File($@"AddOn\{name}\data\_lvl_pc\my.lvl", "maps");
        temp.File($@"AddOn\{name}\Setup.exe", "MZ");
        temp.File($@"AddOn\{name}\tools\fix.bat", "@echo");
        return temp.Sub($@"AddOn\{name}");
    }

    [Fact]
    public void Prepares_a_mod_without_its_programs()
    {
        using var temp = new TempDir();
        var upload = ModUpload.Prepare(FakeMod(temp), new FileHashCache());
        Assert.Equal("MyMod", upload.Folder);
        Assert.Equal(new[] { "addme.script", "data/_lvl_pc/my.lvl" }, upload.Files.Select(f => f.Path));
        Assert.Equal(new[] { "Setup.exe", "tools/fix.bat" }, upload.LeftOut);
        Assert.Equal(TempDir.Sha("maps"), upload.Files[1].Sha256);
        Assert.Equal(9, upload.Size);
    }

    [Fact]
    public void Refuses_the_patch_bad_names_and_folders_without_addme()
    {
        using var temp = new TempDir();
        Assert.Throws<InvalidDataException>(() => ModUpload.Prepare(FakeMod(temp, "AAA-v1.3patch"), new FileHashCache()));
        Assert.Throws<InvalidDataException>(() => ModUpload.Prepare(FakeMod(temp, "My Mod"), new FileHashCache()));
        temp.File(@"AddOn\NoAddme\data\x.lvl");
        Assert.Throws<InvalidDataException>(() => ModUpload.Prepare(temp.Sub(@"AddOn\NoAddme"), new FileHashCache()));
    }

    [Fact]
    public void Compares_a_local_mod_with_the_server()
    {
        using var temp = new TempDir();
        var upload = ModUpload.Prepare(FakeMod(temp), new FileHashCache());
        var server = new FakeModServer();
        Assert.Equal(ServerModState.NotOnServer, ModCompare.Compare(upload, ServerModsManifest.Parse(server.ManifestJson())));
        server.Add("mymod", "addme.script", "addme");
        server.Add("mymod", "data/_lvl_pc/my.lvl", "maps");
        Assert.Equal(ServerModState.Same, ModCompare.Compare(upload, ServerModsManifest.Parse(server.ManifestJson())));
        server.Add("mymod", "data/_lvl_pc/my.lvl", "other maps");
        Assert.Equal(ServerModState.Different, ModCompare.Compare(upload, ServerModsManifest.Parse(server.ManifestJson())));
        server.Add("mymod", "data/_lvl_pc/my.lvl", "maps");
        server.Add("mymod", "extra.txt", "x");
        Assert.Equal(ServerModState.Different, ModCompare.Compare(upload, ServerModsManifest.Parse(server.ManifestJson())));
    }

    [Fact]
    public async Task Uploads_only_the_files_the_server_lacks_then_commits()
    {
        using var temp = new TempDir();
        var upload = ModUpload.Prepare(FakeMod(temp), new FileHashCache());
        var server = new FakeModServer();
        server.Staged.Add("addme.script");
        using var http = ModService.CreateHttp(new Version(0, 3, 0), server);
        var client = new ModUploadClient(http, Local, server.AdminPassword);
        var reports = new List<StepUpdate>();
        await client.UploadAsync(upload, new SyncProgress<StepUpdate>(reports.Add), default);

        Assert.Equal(new[] { "MyMod/data/_lvl_pc/my.lvl" }, server.Uploaded.Keys);
        Assert.Equal("maps", Encoding.UTF8.GetString(server.Uploaded["MyMod/data/_lvl_pc/my.lvl"]));
        var put = server.Requests.Single(r => r.Method == "PUT");
        Assert.Equal(TempDir.Sha("maps"), put.Sha256Header);
        Assert.Equal(4, put.ContentLength);
        Assert.All(server.Requests, r => Assert.Equal(server.AdminPassword, r.Auth));

        var commit = server.Requests.Last();
        Assert.Equal("/upload/MyMod/commit", commit.Path);
        Assert.NotNull(commit.ContentLength);
        using var listing = JsonDocument.Parse(commit.Body!);
        Assert.Equal(new[] { "addme.script", "data/_lvl_pc/my.lvl" },
            listing.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.Contains(reports, r => r.Fraction == 1);
    }

    [Fact]
    public async Task Restarts_removes_and_says_when_the_password_is_wrong()
    {
        var server = new FakeModServer();
        using var http = ModService.CreateHttp(new Version(0, 3, 0), server);
        var client = new ModUploadClient(http, Local, server.AdminPassword);
        Assert.Equal("when-empty", await client.RestartAsync(whenEmpty: true, default));
        Assert.Equal("none", await client.CancelRestartAsync(default));
        Assert.Equal("none", await client.RestartAsync(whenEmpty: false, default));
        Assert.Equal("/admin/restart?when=now", server.Requests.Last().Path);
        await client.RemoveAsync("MyMod", default);
        Assert.Equal(("DELETE", "/upload/MyMod"), (server.Requests.Last().Method, server.Requests.Last().Path));
        Assert.NotNull((await client.GetManifestAsync(default)).Pending);

        var wrong = new ModUploadClient(http, Local, "nope");
        var e = await Assert.ThrowsAsync<ModServiceException>(() => wrong.GetManifestAsync(default));
        Assert.Equal("Wrong admin password.", e.Message);
        var refused = await Assert.ThrowsAsync<ModServiceException>(() => wrong.RestartAsync(false, default));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
    }

    [Fact]
    public async Task Shows_the_services_own_reason()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":\"BF1 is one of the server's own mods (the read-only addons folder)\"}", Encoding.UTF8, "application/json"),
        };
        var e = await ModService.ErrorAsync(response, default);
        Assert.Equal("BF1 is one of the server's own mods (the read-only addons folder).", e.Message);
    }

    /// <summary>Reports at once, on the calling thread (Progress&lt;T&gt; posts later).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public SyncProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
