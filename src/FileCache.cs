using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal sealed class CacheCapacityException : IOException { }
internal sealed record CacheRecord(long Id, long Size, string? Path, string State)
{
    // Unknown/new states retain their space and files until explicitly understood.
    public bool Active => State is not ("snd_complete" or "snd_cancelled" or "snd_error" or "rcv_complete" or "rcv_cancelled" or "rcv_error" or "rcv_aborted" or "rcv_invitation");
    public bool Finished => State is "snd_complete" or "snd_cancelled" or "snd_error" or "rcv_complete" or "rcv_cancelled" or "rcv_error" or "rcv_aborted";
}

internal sealed record CacheEntry(string Path, long Bytes, long Modified);

internal static class FileCache
{
    private static readonly string[] Folders = ["files", "file-temp", "file-assets"];
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
    // Read-only, pinned SimpleX 7.0.3 schema. No bodies, names or encryption keys leave the core.
    private const string MetadataQuery = "/sql chat SELECT json_object('id',file_id,'size',file_size,'path',file_path,'state',ci_file_status) AS cache_metadata FROM files ORDER BY file_id LIMIT 50001";
    public static List<CacheRecord> Read(Func<string, JsonNode> command)
    {
        var result = command(MetadataQuery);
        if (result["type"]?.ToString() != "sQLResult" || result["rows"] is not JsonArray rows || (rows.Count > 0 && rows[0]?.ToString() != "cache_metadata") || rows.Count > 50001)
            throw new IOException("Cannot verify file task inventory");
        var records = new List<CacheRecord>();
        foreach (var row in rows.Skip(1))
        {
            var value = JsonNode.Parse(row!.GetValue<string>())!;
            records.Add(new(value["id"]!.GetValue<long>(), value["size"]!.GetValue<long>(), value["path"]?.GetValue<string>(), (value["state"]?.GetValue<string>() ?? "unknown").Split(' ')[0]));
        }
        return records;
    }
    public static long Budget(long bytes)
    {
        if (bytes < 0) throw new IOException("Invalid file size");
        // Conservative allowance for ciphertext, XFTP padding/chunks and working copies.
        return checked(bytes * 4 + 4 * 1024 * 1024);
    }
    public static List<CacheEntry> Scan(string root)
    {
        var files = new List<CacheEntry>();
        foreach (string folder in Folders)
        {
            string path = FileTransfer.LocalPath(System.IO.Path.Combine(root, folder));
            if (!Directory.Exists(path)) continue;
            var queue = new Queue<string>(); queue.Enqueue(path);
            while (queue.TryDequeue(out string? directory))
                foreach (string item in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileTransfer.LocalPath(item); // Never follow junctions, remote paths or alternate streams.
                    if (Directory.Exists(item)) queue.Enqueue(item);
                    else
                    {
                        var info = new FileInfo(item);
                        files.Add(new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks));
                        if (files.Count > 50000) throw new IOException("Cache inventory too large");
                    }
                }
        }
        return files;
    }
    public static long Reserved(IEnumerable<CacheRecord> records) => records.Where(r => r.Active).Sum(r => Budget(r.Size));
    public static void RequireRoom(string root, IReadOnlyList<CacheRecord> records, long extra)
    {
        if (extra < 0) throw new IOException("Invalid cache budget");
        long used = Scan(root).Sum(f => f.Bytes), reserved = Reserved(records);
        if (checked(used + reserved + extra) > FileTransfer.CacheLimit) throw new CacheCapacityException();
        // Query the granted profile directory, without granting access to the drive root.
        if (!GetDiskFreeSpaceExW(FileTransfer.LocalPath(root), out ulong available, out _, out _)) throw new IOException("Cannot check profile disk space");
        if (available < checked((ulong)(reserved + extra + 64 * 1024 * 1024)))
            throw new CacheCapacityException();
    }
    public static JsonObject Describe(string root, IReadOnlyList<CacheEntry> entries, IReadOnlyList<CacheRecord> records, bool includePlan)
    {
        var references = new Dictionary<string, List<CacheRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(r => r.Path != null))
        {
            string path = FileTransfer.LocalPath(System.IO.Path.IsPathRooted(record.Path!) ? record.Path! : System.IO.Path.Combine(FileTransfer.Cache(root), record.Path!));
            if (!references.TryGetValue(path, out var list)) references[path] = list = new();
            list.Add(record);
        }
        int active = records.Count(r => r.Active);
        var clean = new List<CacheEntry>();
        foreach (var entry in entries)
        {
            if (references.TryGetValue(entry.Path, out var refs))
            { if (refs.All(r => r.Finished)) clean.Add(entry); }
            else if (active == 0)
            {
                bool cache = FileTransfer.Within(FileTransfer.Cache(root), entry.Path);
                // Retain unknown user files; only our GUID ciphertext stages are considered orphaned.
                if (!cache || System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(entry.Path), @"\A[a-f0-9]{32}\.bin\z")) clean.Add(entry);
            }
        }
        var result = new JsonObject { ["used"] = entries.Sum(f => f.Bytes), ["reserved"] = Reserved(records), ["active"] = active, ["cleanable"] = clean.Sum(f => f.Bytes), ["count"] = clean.Count, ["limit"] = FileTransfer.CacheLimit };
        if (includePlan) result["plan"] = new JsonArray(clean.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["bytes"] = f.Bytes, ["modified"] = f.Modified }).ToArray());
        return result;
    }
    // Caller must stop and wait for the owned core before executing this plan.
    // Each leaf is revalidated; database files and external paths can never be selected.
    public static (int Count, long Bytes, int Skipped) CleanOffline(string root, JsonArray plan)
    {
        int count = 0, skipped = 0; long bytes = 0;
        foreach (var value in plan)
        {
            try
            {
                string path = FileTransfer.LocalPath(value!["path"]!.GetValue<string>());
                if (!Folders.Any(folder => FileTransfer.Within(System.IO.Path.Combine(root, folder), path))) throw new IOException("Invalid cleanup path");
                long length = value["bytes"]!.GetValue<long>(), modified = value["modified"]!.GetValue<long>();
                using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (held.Length != length || File.GetLastWriteTimeUtc(path).Ticks != modified) { skipped++; continue; }
                FileTransfer.LocalPath(path);
                File.Delete(path);
                bytes += length; count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
        }
        return (count, bytes, skipped);
    }
}
