using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

/// <summary>
/// Join and admin against the real mod service, the Docker server's modserver.py, on 127.0.0.1 with
/// made-up mods. Runs only when asked:
///   OBF_MODSERVER_PY = OpenSWBF2's docker\modserver.py (Python 3 on PATH as "python")
/// </summary>
public class ModSyncEndToEndTests
{
    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task Syncs_and_uploads_against_modserver_py()
    {
        string? script = Environment.GetEnvironmentVariable("OBF_MODSERVER_PY");
        if (script == null) return;

        using var temp = new TempDir();
        temp.File(@"server\addons\BF1\addme.script", "bf1 addme");
        File.WriteAllBytes(temp.File(@"server\addons\BF1\data\_lvl_pc\bf1.lvl"), Enumerable.Range(0, 2_000_000).Select(i => (byte)i).ToArray());
        temp.File(@"server\addons\BF1\setup.exe", "MZ");
        temp.File(@"server\addons\AAA-v1.3patch\addme.script", "patch");
        Directory.CreateDirectory(temp.Sub(@"server\config"));

        int port = FreePort();
        var info = new ProcessStartInfo("python", $"\"{script}\" serve --host 127.0.0.1 --port {port} --addons \"{temp.Sub(@"server\addons")}\" --config \"{temp.Sub(@"server\config")}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["JOIN_PASSWORD"] = "secret";
        info.Environment["ADMIN_PASSWORD"] = "Admin12345";
        info.Environment["SERVER_NAME"] = "E2E server";
        info.Environment["MOD_RATE"] = "0";
        using var service = Process.Start(info)!;
        try
        {
            var server = new ServerAddress("127.0.0.1", port);
            using var http = ModService.CreateHttp(new Version(0, 3, 0));
            ModServiceReply reply = new(ModServiceStatus.Unavailable);
            for (int i = 0; i < 50 && reply.Status == ModServiceStatus.Unavailable; i++)
            {
                await Task.Delay(100);
                reply = await ModService.GetManifestAsync(http, server, null, default);
            }
            Assert.Equal(ModServiceStatus.NeedsPassword, reply.Status);

            // A player with no mods
            reply = await ModService.GetManifestAsync(http, server, "secret", default);
            Assert.Equal("E2E server", reply.Manifest!.Server);
            Assert.Equal(new[] { "BF1" }, reply.Manifest.Folders.Select(f => f.Name));
            var layout = new InstallLayout(temp.Sub("player"));
            var plan = ModSyncPlan.Create(reply.Manifest, layout, new FileHashCache());
            Assert.Equal(ModSyncAction.New, plan.Items.Single().Action);
            await new ModSyncClient(http, layout, _ => { }).InstallAsync(server, "secret", plan, null, default);
            Assert.Equal(File.ReadAllBytes(temp.Sub(@"server\addons\BF1\data\_lvl_pc\bf1.lvl")),
                File.ReadAllBytes(Path.Combine(layout.AddOn, @"BF1\data\_lvl_pc\bf1.lvl")));
            Assert.False(File.Exists(Path.Combine(layout.AddOn, @"BF1\setup.exe")));
            Assert.True(ModSyncPlan.Create(reply.Manifest, layout, new FileHashCache()).IsEmpty);

            // An admin uploads a mod the server lacks
            temp.File(@"admin\AddOn\NEW\addme.script", "new addme");
            temp.File(@"admin\AddOn\NEW\data\_lvl_pc\new.lvl", "new maps");
            temp.File(@"admin\AddOn\NEW\install.bat", "@echo");
            var upload = ModUpload.Prepare(temp.Sub(@"admin\AddOn\NEW"), new FileHashCache());
            var admin = new ModUploadClient(http, server, "Admin12345");
            Assert.Equal(ServerModState.NotOnServer, ModCompare.Compare(upload, await admin.GetManifestAsync(default)));
            await admin.UploadAsync(upload, null, default);
            var pending = (await admin.GetManifestAsync(default)).Pending!;
            Assert.Equal(("NEW", "add"), (pending.Single().Name, pending.Single().Action));
            Assert.True(File.Exists(temp.Sub(@"server\config\addons\.pending\NEW\data\_lvl_pc\new.lvl")));
            Assert.False(File.Exists(temp.Sub(@"server\config\addons\.pending\NEW\install.bat")));
            Assert.Equal("when-empty", await admin.RestartAsync(whenEmpty: true, default));
            Assert.Equal("none", await admin.CancelRestartAsync(default));

            var wrong = await Assert.ThrowsAsync<ModServiceException>(() => new ModUploadClient(http, server, "secret").RestartAsync(false, default));
            Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);
        }
        finally
        {
            service.Kill();
            service.WaitForExit();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
