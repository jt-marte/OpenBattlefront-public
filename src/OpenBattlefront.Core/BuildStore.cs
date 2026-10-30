using System.IO.Compression;

namespace OpenBattlefront.Core;

/// <summary>
/// Release packages unpacked in Builds\&lt;version&gt;, and the copy of one of them into Game\.
/// </summary>
public sealed class BuildStore
{
    private readonly InstallLayout _layout;

    public BuildStore(InstallLayout layout) => _layout = layout;

    public string Dir(string version) => Path.Combine(_layout.Builds, version);

    public BuildManifest? TryLoad(string? version)
    {
        if (string.IsNullOrEmpty(version) || !File.Exists(Path.Combine(Dir(version), BuildManifest.FileName))) return null;
        try { return BuildManifest.Load(Dir(version)); }
        catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
    }

    /// <summary>Unpacks a release zip into Builds\&lt;version&gt; and checks every file against its
    /// manifest. A zip with any file the manifest does not list is refused.</summary>
    public BuildManifest Unpack(string zipPath, IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifestEntry = zip.GetEntry(BuildManifest.FileName) ?? throw new InvalidDataException("This zip is not an OpenSWBF2 release package (no manifest.json).");
        string json;
        using (var reader = new StreamReader(manifestEntry.Open())) json = reader.ReadToEnd();
        var manifest = BuildManifest.Parse(json);

        var listed = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName == BuildManifest.FileName) continue;
            if (!listed.ContainsKey(entry.FullName)) throw new InvalidDataException($"The package holds a file its manifest does not list: {entry.FullName}");
        }

        string final = Dir(manifest.Version);
        string staging = final + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        int done = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException($"The package is missing {file.Path}.");
            string to = Path.GetFullPath(Path.Combine(staging, file.Path.Replace('/', '\\')));
            if (!to.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe path in package: {file.Path}");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            entry.ExtractToFile(to, overwrite: true);
            progress?.Report(new StepUpdate($"Unpacking OpenSWBF2 {manifest.Version}", file.Path, ++done / (double)manifest.Files.Count));
        }
        File.WriteAllText(Path.Combine(staging, BuildManifest.FileName), json);

        progress?.Report(new StepUpdate($"Checking OpenSWBF2 {manifest.Version}", "SHA-256 of every file"));
        var problems = manifest.Verify(staging);
        if (problems.Count > 0)
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidDataException("The package is damaged: " + string.Join(", ", problems.Take(5)));
        }

        if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
        Directory.Move(staging, final);
        return manifest;
    }

    /// <summary>Copies the game's exe, DLLs and dxvk\ from a build into Game\.</summary>
    public void Deploy(BuildManifest manifest)
    {
        string from = Dir(manifest.Version);
        foreach (var file in manifest.RuntimeFiles)
        {
            string source = Path.Combine(from, file.Path.Replace('/', '\\'));
            string dest = Path.Combine(_layout.Game, file.Path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                File.Copy(source, dest + ".new", overwrite: true);
                File.Move(dest + ".new", dest, overwrite: true);
            }
            catch (IOException e)
            {
                throw new IOException($"Could not replace {file.Path}. Close the game and try again.", e);
            }
        }
    }

    /// <summary>Deletes every unpacked build except the ones named.</summary>
    public void Prune(params string?[] keep)
    {
        if (!Directory.Exists(_layout.Builds)) return;
        var keepSet = keep.Where(k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (string dir in Directory.GetDirectories(_layout.Builds))
            if (!keepSet.Contains(Path.GetFileName(dir)))
                Directory.Delete(dir, recursive: true);
    }
}
