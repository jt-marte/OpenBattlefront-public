using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBattlefront.Core;

/// <summary>Content\content.json in the launcher's package: the included AddOn folders and every
/// file's size and SHA-256 (publish.ps1 -Content writes it). Paths look like AddOn/BF1/addme.script.</summary>
public sealed class ContentManifest
{
    public const string FileName = "content.json";
    private const string AddOnPrefix = "AddOn/";

    public int Schema { get; set; }

    /// <summary>Derived from the files, so it only changes when the content does.</summary>
    public string Version { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static ContentManifest Parse(string json)
    {
        ContentManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ContentManifest>(json, Options); }
        catch (JsonException e) { throw new InvalidDataException("The included content's content.json is not valid JSON.", e); }
        if (manifest == null) throw new InvalidDataException("The included content's content.json is empty.");
        manifest.Validate();
        return manifest;
    }

    public static ContentManifest Load(string dir) => Parse(File.ReadAllText(Path.Combine(dir, FileName)));

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Save(string dir) => File.WriteAllText(Path.Combine(dir, FileName), ToJson());

    /// <summary>Only the allowed folders, each file under one of them, and no programs.</summary>
    public void Validate()
    {
        if (Schema != 1) throw new InvalidDataException($"Unsupported content format {Schema}; update the launcher.");
        if (string.IsNullOrWhiteSpace(Version)) throw new InvalidDataException("The included content has no version.");
        if (Folders.Count == 0 || Files.Count == 0) throw new InvalidDataException("The included content lists no files.");
        foreach (string folder in Folders)
            if (!GameContent.IsAllowedFolder(folder)) throw new InvalidDataException($"The included content holds a folder the launcher doesn't allow: {folder}");
        if (Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Folders.Count) throw new InvalidDataException("The included content lists a folder twice.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!BuildManifest.IsSafeRelative(file.Path) || FolderOf(file.Path) is not { } folder || !Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in the included content: {file.Path}");
            if (GameContent.IsProgram(file.Path)) throw new InvalidDataException($"The included content holds a program: {file.Path}");
            if (!seen.Add(file.Path)) throw new InvalidDataException($"Duplicate path in the included content: {file.Path}");
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"Bad SHA-256 for {file.Path}");
            if (file.Size < 0) throw new InvalidDataException($"Bad size for {file.Path}");
        }
        foreach (string folder in Folders)
            if (!Files.Any(f => string.Equals(FolderOf(f.Path), folder, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The included content lists no files in {folder}.");
    }

    /// <summary>The AddOn folder a path is in (BF1 for AddOn/BF1/addme.script), or null.</summary>
    private static string? FolderOf(string path)
    {
        string[] parts = path.Split('/');
        return parts.Length >= 3 && parts[0].Equals("AddOn", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    /// <summary>A manifest path inside the AddOn folder, as a Windows path (BF1\addme.script).</summary>
    public static string InAddOn(string path) => path[AddOnPrefix.Length..].Replace('/', '\\');

    /// <summary>Lists the allowed folders of an AddOn folder, hashing every file. Anything else
    /// there (the Xbox Title Update, the 1.3 patch, other mods) is left out; a program inside an
    /// allowed folder stops it.</summary>
    public static ContentManifest Create(string addOn)
    {
        var manifest = new ContentManifest { Schema = 1 };
        foreach (string folder in GameContent.Folders)
        {
            string dir = Path.Combine(addOn, folder);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"{dir} is missing.");
            manifest.Folders.Add(folder);
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                string path = AddOnPrefix + folder + "/" + Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (GameContent.IsProgram(path)) throw new InvalidDataException($"{file} is a program; the included content carries none.");
                manifest.Files.Add(new ManifestFile { Path = path, Size = new FileInfo(file).Length, Sha256 = Hashing.Sha256(file) });
            }
        }
        string list = string.Join("\n", manifest.Files.Select(f => $"{f.Path}|{f.Size}|{f.Sha256}"));
        manifest.Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(list)))[..12].ToLowerInvariant();
        manifest.Validate();
        return manifest;
    }
}

/// <summary>
/// The included content: the Battlefront Conversion Pack (BF1) and the Xbox DLC (DLC_XBOX) ship
/// in the launcher's package under Content\, and are copied into Game\AddOn with no download.
/// Only those two folders are allowed: never the Xbox Title Update or the unofficial 1.3 patch,
/// and never programs.
/// </summary>
public sealed class GameContent
{
    public static readonly IReadOnlyList<string> Folders = new[] { "BF1", "DLC_XBOX" };
    public const string AddedNotice = "Added the included content: Battlefront Conversion Pack and Xbox DLC.";

    private const string Title = "Adding the included content";
    private const string DownloadAgain = "Download the launcher package again.";
    private const long SpareBytes = 512L << 20;
    private static readonly string[] Programs = { ".exe", ".dll", ".bat" };
    private readonly Action<string> _remove;

    /// <param name="remove">How a folder being replaced goes away; the Recycle Bin by default.</param>
    public GameContent(string dir, Action<string>? remove = null)
    {
        Dir = dir;
        _remove = remove ?? RecycleBin.Delete;
    }

    /// <summary>Content\ next to the launcher's exe.</summary>
    public static string DefaultDir => Path.Combine(AppContext.BaseDirectory, "Content");

    public string Dir { get; }

    public static bool IsAllowedFolder(string folder) => Folders.Contains(folder, StringComparer.OrdinalIgnoreCase);

    public static bool IsProgram(string path) => Programs.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>The AddOn folders the launcher put in place itself (the Mods page marks them
    /// Included), once it has.</summary>
    public static IReadOnlyList<string> Installed(LauncherState state) => state.ContentVersion != null ? Folders : Array.Empty<string>();

    /// <summary>The package's content.json, or null when this launcher came without content.</summary>
    public ContentManifest? Load() => File.Exists(Path.Combine(Dir, ContentManifest.FileName)) ? ContentManifest.Load(Dir) : null;

    /// <summary>Copies every file into Temp\ while checking its SHA-256, then swaps each folder
    /// into AddOn\, or into AddOn_disabled\ when the player had switched it off. A folder it
    /// replaces goes to the Recycle Bin. A damaged file stops it before anything is replaced.</summary>
    public async Task InstallAsync(ContentManifest manifest, InstallLayout layout, IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        manifest.Validate();
        long total = manifest.Files.Sum(f => f.Size);
        Directory.CreateDirectory(layout.Root);
        long free = Importer.FreeBytes(layout.Root);
        if (total + SpareBytes > free)
            throw new IOException($"Not enough disk space: the included content needs {Sizes.Format(total)} and the drive has {Sizes.Format(free)} free.");

        string staging = Path.Combine(layout.Temp, "content");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        try
        {
            await CopyAsync(manifest, staging, total, progress, ct);
            progress?.Report(new StepUpdate(Title, "Putting it in place"));
            foreach (string folder in manifest.Folders)
                Swap(layout, folder, Path.Combine(staging, folder));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task CopyAsync(ContentManifest manifest, string staging, long total, IProgress<StepUpdate>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        long lastReport = -1000;
        var clock = Stopwatch.StartNew();
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string from = Path.Combine(Dir, file.Path.Replace('/', '\\'));
            string to = Path.GetFullPath(Path.Combine(staging, ContentManifest.InAddOn(file.Path)));
            if (!to.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe path in the included content: {file.Path}");
            if (!File.Exists(from)) throw new InvalidDataException($"The included content is missing {file.Path}. {DownloadAgain}");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (clock.ElapsedMilliseconds - lastReport >= 100)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        double perSecond = done / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                        progress?.Report(new StepUpdate(Title, $"{Sizes.Format(done)} of {Sizes.Format(total)}  ·  {Sizes.Format((long)perSecond)}/s",
                            total > 0 ? (double)done / total : 0));
                    }
                }
            }
            if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The included content is damaged ({file.Path}). {DownloadAgain}");
        }
    }

    private void Swap(InstallLayout layout, string folder, string staged)
    {
        string on = Path.Combine(layout.AddOn, folder);
        string off = Path.Combine(layout.AddOnDisabled, folder);
        string to = Directory.Exists(off) && !Directory.Exists(on) ? off : on;
        foreach (string old in new[] { on, off })
            if (Directory.Exists(old)) _remove(old);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(staged, to);
    }
}
