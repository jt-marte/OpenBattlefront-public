using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBattlefront.Tests;

/// <summary>
/// The launcher's hosted mod service and the Docker server's modserver.py must speak the same
/// protocol: the same made-up mods, the same requests, and the answers must match (status codes,
/// JSON, file bytes and range headers). No game runs behind either, so both report it offline.
/// Runs only when asked:
///   OBF_MODSERVER_PY = OpenSWBF2's docker\modserver.py (Python 3 on PATH as "python")
/// </summary>
public class ProtocolParityTests
{
    private const string Join = HostFixture.Join, Admin = HostFixture.Admin;

    private sealed record Step(string Method, string Target, string? Password = Join, object? Json = null,
        Dictionary<string, string>? Headers = null, bool StatusOnly = false, string? TakeToken = null);

    private static IEnumerable<Step> Steps()
    {
        yield return new("GET", "/mods.json");
        yield return new("GET", "/mods.json", Admin);
        yield return new("GET", "/mods.json", null);
        yield return new("HEAD", "/mods.json");
        yield return new("GET", "/mods/BF1/addme.script");
        yield return new("HEAD", "/mods/BF1/Readme.txt");
        foreach (string range in new[] { "bytes=100-199", "bytes=-500", "bytes=299000-", "bytes=300000-", "bytes=0-1,5-6", "items=0-1", "bytes=9-2" })
            yield return new("GET", "/mods/BF1/data/_lvl_pc/bf1.lvl", Headers: new() { ["Range"] = range });
        yield return new("GET", "/mods/BF1/data/_lvl_pc/bf1.lvl", Headers: new() { ["Range"] = "bytes=10-", ["If-Range"] = "\"" + new string('0', 64) + "\"" });
        foreach (string path in ModHostServiceTests.Traversal)
            yield return new("GET", path, Admin);
        foreach (string path in new[] { "/mods/BF1/setup.exe", "/mods/BF1/tools/run.BAT", "/mods/AAA-v1.3patch/addme.script", "/mods/NoAddme/x.lvl", "/mods/bf1/addme.script", "/nope" })
            yield return new("GET", path);
        yield return new("GET", "/status");
        yield return new("POST", "/lobby/join", Json: new { name = "Pat" }, TakeToken: "token");
        yield return new("POST", "/lobby/join", Json: new { name = "a\"b" });
        yield return new("POST", "/lobby/join", null, new { name = "Pat" });
        yield return new("POST", "/lobby/update", Json: new { token = "{token}", state = "downloading", progress = 45 });
        yield return new("GET", "/status?token={token}");
        yield return new("POST", "/lobby/update", Json: new { token = "{token}", state = "waiting", progress = 100 });
        yield return new("POST", "/lobby/update", Json: new { token = "{token}", state = "playing" });
        yield return new("POST", "/lobby/update", Json: new { token = "nope", state = "waiting" });
        yield return new("GET", "/status?token={token}");
        yield return new("POST", "/admin/match?do=start");
        yield return new("POST", "/admin/match?do=start", Admin);
        yield return new("POST", "/admin/match?do=lobby", Admin);
        yield return new("POST", "/admin/match?do=later", Admin);
        yield return new("DELETE", "/admin/match", Admin);
        yield return new("POST", "/lobby/leave", Json: new { token = "{token}" });
        yield return new("GET", "/status?token={token}");
        yield return new("POST", "/upload/NEW/begin", Join, new { files = Array.Empty<object>() });
        // The texts differ on purpose: one says MOD_UPLOADS=0, the other that it's a launcher's server
        yield return new("POST", "/upload/NEW/begin", Admin, new { files = Array.Empty<object>() }, StatusOnly: true);
        // Neither has a map list: no game wrote one
        yield return new("GET", "/missions.json");
        yield return new("GET", "/missions.json", null);
        for (int i = 0; i < 5; i++) yield return new("GET", "/mods.json", "guess");
        yield return new("GET", "/mods.json", Admin);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task The_hosted_service_answers_as_modserver_py()
    {
        string? script = Environment.GetEnvironmentVariable("OBF_MODSERVER_PY");
        if (script == null) return;

        await using var host = new HostFixture();
        host.Start(game: false);
        int pyPort = FreePort();
        var info = new ProcessStartInfo("python", $"\"{script}\" serve --host 127.0.0.1 --port {pyPort} --addons \"{host.AddOn}\" --config \"{host.Temp.Sub("pyconfig")}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The running game's name and join password, as the entrypoint writes them before each game start
        host.Temp.File(@"pyconfig\settings\effective.json", $$"""{"name": "Test server", "joinPassword": "{{Join}}"}""");
        info.Environment["ADMIN_PASSWORD"] = Admin;
        info.Environment["OPENSWBF2_VERSION"] = "0.2.2";
        info.Environment["MOD_UPLOADS"] = "0";
        info.Environment["MOD_RATE"] = "0";
        info.Environment["PORT"] = FreePort().ToString(); // no game there: rcon finds nobody, as on the host
        using var python = Process.Start(info)!;
        try
        {
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    using var probe = new TcpClient("127.0.0.1", pyPort);
                    break;
                }
                catch (SocketException) { await Task.Delay(100); }
            }

            var tokens = new Dictionary<int, string>();
            int n = 0;
            foreach (var step in Steps())
            {
                n++;
                var py = Send(pyPort, step, tokens);
                var cs = Send(host.Port, step, tokens);
                string what = $"step {n}: {step.Method} {step.Target}";
                Assert.True(py.Status == cs.Status, $"{what}: modserver.py {py.Status} {py.Text}, launcher {cs.Status} {cs.Text}");
                if (step.TakeToken != null && py.Status == 200)
                {
                    tokens[pyPort] = py.Json.GetProperty("token").GetString()!;
                    tokens[host.Port] = cs.Json.GetProperty("token").GetString()!;
                    continue;
                }
                if (step.StatusOnly) continue;
                foreach (string header in new[] { "Content-Range", "Accept-Ranges", "ETag" })
                    Assert.True(py.Headers.GetValueOrDefault(header) == cs.Headers.GetValueOrDefault(header), $"{what}: {header} {py.Headers.GetValueOrDefault(header)} vs {cs.Headers.GetValueOrDefault(header)}");
                bool json = py.Headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json");
                if (step.Method == "HEAD")
                {
                    // JSON's length depends on spacing (Python writes ", "); a file's must match
                    if (!json) Assert.True(py.Headers["Content-Length"] == cs.Headers["Content-Length"], $"{what}: Content-Length");
                }
                else if (json)
                    Assert.True(Normal(py.Body) == Normal(cs.Body), $"{what}:\n modserver.py {Normal(py.Body)}\n launcher     {Normal(cs.Body)}");
                else
                    Assert.True(py.Body.AsSpan().SequenceEqual(cs.Body), $"{what}: the bytes differ");
            }
        }
        finally
        {
            python.Kill();
            python.WaitForExit();
        }
    }

    private static RawHttp.Answer Send(int port, Step step, Dictionary<int, string> tokens)
    {
        string token = tokens.GetValueOrDefault(port, "none");
        object? json = step.Json == null ? null : JsonNode.Parse(JsonSerializer.Serialize(step.Json)!.Replace("{token}", token));
        return RawHttp.Send(port, step.Method, step.Target.Replace("{token}", token), step.Password, json, step.Headers);
    }

    /// <summary>JSON with sorted keys, so key order doesn't count. The admin's mods.json says
    /// "settings" only where they differ on purpose: the Docker server keeps what an admin
    /// applies, a hosting launcher keeps its settings on its own PC.</summary>
    private static string Normal(byte[] body)
    {
        static JsonNode? Sort(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
            JsonArray a => new JsonArray(a.Select(e => Sort(e?.DeepClone())).ToArray()),
            _ => node?.DeepClone(),
        };
        var root = JsonNode.Parse(body);
        (root as JsonObject)?.Remove("settings");
        return Sort(root)?.ToJsonString() ?? "null";
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
