namespace MailSearch.Config;

/// <summary>What the data directory costs on disk, split the way a user thinks about it.</summary>
public sealed record DataUsage(long DatabaseBytes, long ModelBytes, long OtherBytes)
{
    public long TotalBytes => DatabaseBytes + ModelBytes + OtherBytes;
}

/// <summary>
/// Measuring and emptying the data directory. A synced mailbox plus two ONNX models runs to
/// several gigabytes, so "how do I get that space back" needs an answer that is not "find
/// %LOCALAPPDATA% yourself" — the app menu and the uninstaller both come through here.
/// </summary>
public static class DataPurge
{
    public static DataUsage Measure(DataPaths paths)
    {
        if (!Directory.Exists(paths.Root)) return new DataUsage(0, 0, 0);

        // The WAL and shared-memory files sit next to the database and can be large on their own.
        long database = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" }) database += FileBytes(paths.DatabaseFile + suffix);

        var models = DirectoryBytes(paths.ModelsDirectory);
        var total = DirectoryBytes(paths.Root);
        return new DataUsage(database, models, Math.Max(0, total - database - models));
    }

    /// <summary>
    /// Deletes the data directory and everything in it. Returns the entries that could not be
    /// removed — a running second instance keeps mail.db locked, and silently reporting success
    /// while gigabytes stay on disk is the one outcome worth avoiding here.
    /// </summary>
    public static IReadOnlyList<string> DeleteAll(DataPaths paths)
    {
        var failures = new List<string>();
        if (!Directory.Exists(paths.Root)) return failures;

        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.Root))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) { failures.Add($"{entry}: {ex.Message}"); }
        }

        // Leave the root behind if anything survived, so what is left stays where the user expects it.
        if (failures.Count == 0)
        {
            try { Directory.Delete(paths.Root); }
            catch (Exception ex) { failures.Add($"{paths.Root}: {ex.Message}"); }
        }
        return failures;
    }

    private static long FileBytes(string path)
    {
        try { return new FileInfo(path) is { Exists: true } f ? f.Length : 0; }
        catch { return 0; }
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += FileBytes(file);
        }
        catch { /* a partially readable directory still gives a useful number */ }
        return total;
    }
}
