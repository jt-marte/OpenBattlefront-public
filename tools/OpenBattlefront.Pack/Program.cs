using System.IO.Compression;
using System.Text;
using OpenBattlefront.Core;

// Builds the launcher package for publish.ps1 -Content: OpenBattlefront.exe, the included content
// (GameContent.Folders, taken from an AddOn folder) under Content\AddOn\, and Content\content.json.
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: OpenBattlefront.Pack <OpenBattlefront.exe> <AddOn folder> <package.zip>");
    return 2;
}
string exe = args[0], addOn = args[1], zipPath = args[2];

try
{
    Console.WriteLine($"Hashing {string.Join(" and ", GameContent.Folders)} in {addOn}");
    var manifest = ContentManifest.Create(addOn);
    Console.WriteLine($"Content {manifest.Version}: {manifest.Files.Count} files, {Sizes.Format(manifest.Files.Sum(f => f.Size))}");

    string partial = zipPath + ".partial";
    File.Delete(partial);
    using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
    {
        zip.CreateEntryFromFile(exe, Path.GetFileName(exe), CompressionLevel.Optimal);
        foreach (var file in manifest.Files)
            zip.CreateEntryFromFile(Path.Combine(addOn, ContentManifest.InAddOn(file.Path)), "Content/" + file.Path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(zip.CreateEntry("Content/" + ContentManifest.FileName).Open(), new UTF8Encoding(false));
        writer.Write(manifest.ToJson());
    }
    File.Move(partial, zipPath, overwrite: true);
    return 0;
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
