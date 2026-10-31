using System.Diagnostics;

namespace OpenBattlefront.Core;

public sealed record ImportItem(string Source, string Destination, string Display, long Size, DateTime LastWriteUtc, bool Overwrite)
{
    /// <summary>Already copied: same size and time, or a file that is never overwritten.</summary>
    public bool IsUpToDate()
    {
        var dest = new FileInfo(Destination);
        if (!dest.Exists) return false;
        return !Overwrite || (dest.Length == Size && dest.LastWriteTimeUtc == LastWriteUtc);
    }
}

public sealed class ImportPlan
{
    public ImportPlan(IReadOnlyList<ImportItem> items)
    {
        Items = items;
        Pending = items.Where(i => !i.IsUpToDate()).ToList();
        TotalBytes = items.Sum(i => i.Size);
        PendingBytes = Pending.Sum(i => i.Size);
    }

    public IReadOnlyList<ImportItem> Items { get; }
    public IReadOnlyList<ImportItem> Pending { get; }
    public long TotalBytes { get; }
    public long PendingBytes { get; }
}

/// <summary>What else to bring besides the game data. Profiles and saves never come along:
/// OpenBattlefront starts with a clean slate.</summary>
public sealed record ImportOptions(bool Mods = true);

public sealed record ImportPreview(long DataBytes, int ModCount, long ModBytes);

/// <summary>
/// Copies the player's game data into the launcher's Game\ folder. The four core lvls are not
/// copied (the launcher builds its own from Stock\), and the player's folder is only ever read.
/// </summary>
public static class Importer
{
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    /// <param name="relative">A path relative to GameData\data, such as _lvl_pc\shell.lvl.</param>
    public static bool ShouldCopyDataFile(string relative)
    {
        relative = relative.Replace('/', '\\');
        string name = Path.GetFileName(relative);
        if (name.EndsWith(".orig", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(Path.GetDirectoryName(relative), "_lvl_pc", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Equals("vidmode.ini", StringComparison.OrdinalIgnoreCase)) return false;
            if (StockLvls.Names.Any(n => name.Equals(n + ".lvl", StringComparison.OrdinalIgnoreCase))) return false;
            if (ScriptMods.IsPatchFile(name)) return false; // OpenSWBF2 has 1.3-style support built in
        }
        return true;
    }

    /// <param name="relative">A path relative to GameData\addon, such as BF1\addme.script.</param>
    public static bool ShouldCopyAddOnFile(string relative)
    {
        relative = relative.Replace('/', '\\');
        int slash = relative.IndexOf('\\');
        return !ScriptMods.IsPatchAddOn(slash < 0 ? relative : relative[..slash]);
    }

    public static ImportPreview Preview(string gameData)
    {
        string data = Path.Combine(gameData, "data");
        long dataBytes = Files(data).Where(f => ShouldCopyDataFile(Path.GetRelativePath(data, f.FullName))).Sum(f => f.Length);
        string addon = Path.Combine(gameData, "addon");
        int mods = Directory.Exists(addon) ? ModManager.FindModRoots(addon).Count(r => ShouldCopyAddOnFile(Path.GetRelativePath(addon, r))) : 0;
        long modBytes = Files(addon).Where(f => ShouldCopyAddOnFile(Path.GetRelativePath(addon, f.FullName))).Sum(f => f.Length);
        return new ImportPreview(dataBytes, mods, modBytes);
    }

    public static ImportPlan Plan(string gameData, InstallLayout layout, ImportOptions options)
    {
        var items = new List<ImportItem>();
        AddTree(items, Path.Combine(gameData, "data"), Path.Combine(layout.Game, "data"), "data", ShouldCopyDataFile, overwrite: true);
        foreach (string name in StockLvls.Names)
        {
            string? source = StockLvls.FindIn(gameData, name);
            if (source != null)
                items.Add(Item(new FileInfo(source), Path.Combine(layout.StockLvlDir, name + ".lvl"), $"stock {name}.lvl", overwrite: true));
        }
        if (options.Mods)
            AddTree(items, Path.Combine(gameData, "addon"), layout.AddOn, "AddOn", ShouldCopyAddOnFile, overwrite: true);
        return new ImportPlan(items);
    }

    public static long FreeBytes(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public static async Task CopyAsync(ImportPlan plan, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        int files = 0;
        var clock = Stopwatch.StartNew();
        long lastReport = -1000;

        void Report(string? current)
        {
            double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            progress?.Report(new TransferProgress(done, plan.PendingBytes, files, plan.Pending.Count, current, done / seconds));
        }

        foreach (var item in plan.Pending)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            string partial = item.Destination + ".partial";
            await using (var src = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (clock.ElapsedMilliseconds - lastReport >= 100)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        Report(item.Display);
                    }
                }
            }
            File.Move(partial, item.Destination, overwrite: true);
            File.SetLastWriteTimeUtc(item.Destination, item.LastWriteUtc);
            files++;
        }
        Report(null);
    }

    private static IEnumerable<FileInfo> Files(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", AllFiles) : Enumerable.Empty<FileInfo>();

    private static void AddTree(List<ImportItem> items, string from, string to, string label, Func<string, bool> filter, bool overwrite)
    {
        foreach (var file in Files(from))
        {
            string relative = Path.GetRelativePath(from, file.FullName);
            if (filter(relative))
                items.Add(Item(file, Path.Combine(to, relative), Path.Combine(label, relative), overwrite));
        }
    }

    private static ImportItem Item(FileInfo file, string destination, string display, bool overwrite) =>
        new(file.FullName, destination, display, file.Length, file.LastWriteTimeUtc, overwrite);
}
