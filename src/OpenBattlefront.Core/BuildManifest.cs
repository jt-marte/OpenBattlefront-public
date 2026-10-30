using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBattlefront.Core;

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>manifest.json from an OpenSWBF2 release package (tools\package-release.ps1).</summary>
public sealed partial class BuildManifest
{
    public const string FileName = "manifest.json";

    public int Schema { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string? Commit { get; set; }
    public bool Dirty { get; set; }
    public string? Built { get; set; }
    public string MinLauncherVersion { get; set; } = "0.0.0";
    public string Exe { get; set; } = "BattlefrontII.exe";
    public string LvlBuilder { get; set; } = "LuaCompiler.exe";
    public string Index { get; set; } = "data/scripts/index.json";
    public Dictionary<string, string[]> StockLvls { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static BuildManifest Parse(string json)
    {
        BuildManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<BuildManifest>(json, Options); }
        catch (JsonException e) { throw new InvalidDataException("The package's manifest.json is not valid JSON.", e); }
        if (manifest == null) throw new InvalidDataException("The package's manifest.json is empty.");
        manifest.Validate();
        return manifest;
    }

    public static BuildManifest Load(string dir) => Parse(File.ReadAllText(System.IO.Path.Combine(dir, FileName)));

    public void Validate()
    {
        if (Schema != 1) throw new InvalidDataException($"Unsupported package format {Schema}; update the launcher.");
        if (!string.Equals(Platform, "x64", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"This package is for {Platform}, not x64.");
        if (!System.Version.TryParse(Version, out _)) throw new InvalidDataException($"Bad package version '{Version}'.");
        if (Files.Count == 0) throw new InvalidDataException("The package lists no files.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!IsSafeRelative(file.Path)) throw new InvalidDataException($"Unsafe path in package: {file.Path}");
            if (!seen.Add(file.Path)) throw new InvalidDataException($"Duplicate path in package: {file.Path}");
            if (!Sha256Pattern().IsMatch(file.Sha256)) throw new InvalidDataException($"Bad SHA-256 for {file.Path}");
            if (file.Size < 0) throw new InvalidDataException($"Bad size for {file.Path}");
        }
        foreach (string required in new[] { Exe, LvlBuilder, Index })
            if (!seen.Contains(required)) throw new InvalidDataException($"The package is missing {required}.");
    }

    /// <summary>A relative path with forward slashes that stays inside the folder it is extracted to.</summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/')) return false;
        return path.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0);
    }

    /// <summary>Files that go next to the game in Game\. The scripts and the lvl builder stay in
    /// the build folder.</summary>
    public IEnumerable<ManifestFile> RuntimeFiles => Files.Where(f =>
        !f.Path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) &&
        !f.Path.Equals(LvlBuilder, StringComparison.OrdinalIgnoreCase) &&
        !f.Path.Equals("README.md", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every problem with the files in <paramref name="dir"/>, or none.</summary>
    public IReadOnlyList<string> Verify(string dir)
    {
        var problems = new List<string>();
        foreach (var file in Files)
        {
            string path = System.IO.Path.Combine(dir, file.Path.Replace('/', '\\'));
            var info = new FileInfo(path);
            if (!info.Exists) problems.Add($"missing {file.Path}");
            else if (info.Length != file.Size) problems.Add($"wrong size {file.Path}");
            else if (!string.Equals(Hashing.Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"wrong SHA-256 {file.Path}");
        }
        return problems;
    }

    public bool NeedsNewerLauncher(Version launcher) =>
        System.Version.TryParse(MinLauncherVersion, out var min) && launcher < min;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
