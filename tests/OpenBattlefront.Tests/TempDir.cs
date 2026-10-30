using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "obf-test-" + Guid.NewGuid().ToString("N"));

    public string File(string relative, string content = "x")
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Sub(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }

    public static string Sha(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>A fake GameData folder whose core lvls hold "&lt;name&gt; stock", and the accepted
    /// hashes for them.</summary>
    public (string GameData, Dictionary<string, string[]> Accepted) FakeGame(string at = "Game Root\\GameData")
    {
        var accepted = new Dictionary<string, string[]>();
        foreach (string name in StockLvls.Names)
        {
            File($"{at}\\data\\_lvl_pc\\{name}.lvl", name + " stock");
            accepted[name] = new[] { Sha(name + " stock") };
        }
        File($"{at}\\data\\_lvl_pc\\core.lvl", "core");
        File($"{at}\\data\\_lvl_pc\\sound\\gal.lvl", "sound");
        File($"{at}\\BattlefrontII.exe", "retail exe");
        return (Sub(at), accepted);
    }

    /// <summary>A release zip whose manifest lists <paramref name="files"/>.</summary>
    public string Package(string version, IDictionary<string, string> files, string? extraEntry = null, string? manifestOverride = null)
    {
        var entries = files.Select(f => new { path = f.Key, size = (long)Encoding.UTF8.GetByteCount(f.Value), sha256 = Sha(f.Value) });
        string manifest = manifestOverride ?? System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = 1,
            name = "OpenSWBF2",
            version,
            platform = "x64",
            minLauncherVersion = "0.1.0",
            exe = "BattlefrontII.exe",
            lvlBuilder = "LuaCompiler.exe",
            index = "data/scripts/index.json",
            stockLvls = new Dictionary<string, string[]>(),
            files = entries,
        });
        string zip = Sub($"OpenSWBF2-{version}-x64.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        foreach (var file in files) Add(file.Key, file.Value);
        Add("manifest.json", manifest);
        if (extraEntry != null) Add(extraEntry, "surprise");
        return zip;
    }

    public static Dictionary<string, string> BuildFiles(string tag = "1") => new()
    {
        ["BattlefrontII.exe"] = "game " + tag,
        ["avcodec-62.dll"] = "codec " + tag,
        ["dxvk/d3d9.dll"] = "dxvk " + tag,
        ["LuaCompiler.exe"] = "luac " + tag,
        ["data/scripts/index.json"] = "{}",
        ["data/scripts/shell/ifs_start.lua"] = "-- start " + tag,
        ["README.md"] = "readme",
    };
}
