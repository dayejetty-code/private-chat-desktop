using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal sealed class BackupBusyFilesException : IOException { }

// All entry points require the caller's profile instance lock and a stopped worker.
internal static class ProfileBackup
{
    internal static readonly string[] DatabaseFiles = ["chat_chat.db", "chat_agent.db", "chat_chat.db-wal", "chat_agent.db-wal", "chat_chat.db-shm", "chat_agent.db-shm", "chat_chat.db-journal", "chat_agent.db-journal"];
    internal static readonly string[] Folders = ["files", "file-temp", "file-assets"];
    internal static IEnumerable<string> Roots => DatabaseFiles.Concat(Folders).Append(ProfileKeys.FileName);
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumFiles = 50000;
    internal static bool SafeEntry(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Contains('\\')) return false;
        string[] parts = name.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p != FileTransfer.SafeName(p) || p.EndsWith(' ') || p.EndsWith('.'))) return false;
        return parts.Length == 1 ? DatabaseFiles.Contains(name, StringComparer.Ordinal) || name == ProfileKeys.FileName : Folders.Contains(parts[0], StringComparer.Ordinal);
    }

#if ENABLE_QA
    // Legacy archive compatibility fixtures only. Absent from production builds.
    internal static string BackupPath(string path, string profile)
    {
        path = FileTransfer.LocalPath(path);
        if (!string.Equals(Path.GetExtension(path), ".pcbackup", StringComparison.OrdinalIgnoreCase) || FileTransfer.Within(profile, path)) throw new IOException("Choose a backup outside the profile");
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? cloud = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(cloud) && FileTransfer.Within(cloud, path)) throw new IOException("Choose a non-synced local folder");
        }
        return path;
    }

    internal static async Task Create(string profile, string target, string password, CancellationToken token)
    {
        profile = FileTransfer.LocalPath(profile); target = BackupPath(target, profile);
        if (File.Exists(target)) throw new IOException("Backup already exists");
        string stage = NewStage(profile), part = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await WaitForProfile(profile, token);
            await Task.Run(() => CopyProfile(profile, stage, token), token);
            await Validate(stage, profile, password, normalize: false, token);
            await ProfileKeyMigration.RotateStage(stage, password, token);
            await Task.Run(() => BackupArchive.Write(stage, profile, part, password, token), token);
            // Read and authenticate every byte again before exposing a completed backup.
            string verify = NewStage(profile);
            try { await Task.Run(() => BackupArchive.Extract(part, verify, password, token), token); }
            finally { DeleteOwned(profile, verify); }
            token.ThrowIfCancellationRequested(); BackupPath(target, profile);
            File.Move(part, target); // Never replace an existing backup.
        }
        finally
        {
            if (File.Exists(part)) File.Delete(FileTransfer.LocalPath(part));
            DeleteOwned(profile, stage);
        }
    }

    internal static async Task<string?> Restore(string profile, string archive, string password, CancellationToken token)
    {
        profile = FileTransfer.LocalPath(profile); archive = BackupPath(archive, profile);
        string stage = NewStage(profile);
        try
        {
            var manifest = await Task.Run(() => BackupArchive.Extract(archive, stage, password, token), token);
            await Validate(stage, manifest.ProfilePath, password, normalize: true, token);
            await ProfileKeyMigration.RotateStage(stage, password, token);
            token.ThrowIfCancellationRequested();
            // Cancellation is honored until commit starts. Commit/rollback is indivisible
            // from the UI's perspective; startup recovery handles process/power loss.
            return await Task.Run(() => ProfileRestore.Install(profile, stage), CancellationToken.None);
        }
        finally { DeleteOwned(profile, stage); }
    }

#endif
    internal static async Task Validate(string stage, string origin, string password, bool normalize, CancellationToken token)
    {
        foreach (string name in new[] { "chat_chat.db", "chat_agent.db" })
            if (!File.Exists(Path.Combine(stage, name)) || new FileInfo(Path.Combine(stage, name)).Length < 16) throw new InvalidDataException("Missing encrypted database");
        using var client = new CoreClient();
        using var worker = Process.GetProcessById(client.ProcessId); _ = worker.SafeHandle;
        using var cancellation = token.Register(client.Dispose);
        try
        {
            token.ThrowIfCancellationRequested();
            if ((await client.Init(Path.Combine(stage, "chat"), password))["type"]?.ToString() != "ok") throw new InvalidDataException("Cannot open encrypted database");
            var user = await client.Result("/u");
            if (user["user"]?["userId"] == null) throw new InvalidDataException("No local identity");
            // Init opens stores only. Never issue /_start or construct a TorService here.
            await FileTransfer.Configure(client, stage);
            foreach (string database in new[] { "chat", "agent" })
            {
                var check = await client.Result("/sql " + database + " PRAGMA integrity_check");
                if (check["rows"] is not JsonArray rows || rows.Count != 2 || rows[1]?.ToString() != "ok") throw new InvalidDataException("Database integrity check failed");
            }
            if (normalize)
            {
                // Older receive operations stored absolute paths. Only paths under the
                // original cache are converted, and the original directory is never read.
                var result = await client.Result("/sql chat SELECT json_object('id',file_id,'path',file_path) AS backup_paths FROM files WHERE file_path IS NOT NULL");
                if (result["rows"] is not JsonArray rows || (rows.Count > 0 && rows[0]?.ToString() != "backup_paths") || rows.Count > 50001) throw new InvalidDataException("Invalid file inventory");
                foreach (var row in rows.Skip(1))
                {
                    var record = JsonNode.Parse(row!.GetValue<string>())!;
                    string path = record["path"]!.GetValue<string>();
                    if (Path.IsPathRooted(path))
                    {
                        string oldCache = Path.Combine(origin, "files");
                        if (!Path.IsPathFullyQualified(oldCache) || !FileTransfer.Within(oldCache, path)) throw new InvalidDataException("External attachment path");
                        path = Path.GetRelativePath(oldCache, path).Replace('\\', '/');
                    }
                    if (!SafeEntry("files/" + path)) throw new InvalidDataException("Unsafe attachment path");
                    await client.Result("/sql chat UPDATE files SET file_path='" + path.Replace("'", "''") + "' WHERE file_id=" + record["id"]!.GetValue<long>());
                }
            }
            var info = await client.CacheInfo();
            if (info["active"]!.GetValue<int>() != 0) throw new BackupBusyFilesException();
            // Native SQL commands run inside a transaction, so an explicit checkpoint
            // there is invalid. Close both stores normally; retain any remaining WAL
            // alongside its encrypted database instead of assuming it was checkpointed.
            await client.CloseStore();
        }
        finally
        {
            client.Dispose(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // Windows may signal process exit before its last file handle is released.
            await WaitForProfile(stage, CancellationToken.None);
        }
        token.ThrowIfCancellationRequested();
    }

    internal static async Task WaitForProfile(string profile, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (string name in DatabaseFiles)
                {
                    string path = FileTransfer.LocalPath(Path.Combine(profile, name));
                    if (File.Exists(path)) { using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                }
                return;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && deadline.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(100, token); }
        }
    }

    internal static string NewStage(string profile)
    {
        string stage = FileTransfer.LocalPath(Path.Combine(profile, ".backup-work-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stage); return stage;
    }
    internal static List<string> Inventory(string profile)
    {
        var files = new List<string>();
        foreach (string name in Roots)
        {
            string path = FileTransfer.LocalPath(Path.Combine(profile, name));
            if (Directory.Exists(path)) Walk(path); else if (File.Exists(path)) Add(path);
        }
        if (!File.Exists(Path.Combine(profile, "chat_chat.db")) || !File.Exists(Path.Combine(profile, "chat_agent.db"))) throw new InvalidDataException("Missing databases");
        return files;
        void Add(string path)
        {
            if (!SafeEntry(Path.GetRelativePath(profile, path).Replace('\\', '/'))) throw new IOException("Unsupported snapshot path");
            files.Add(path); if (files.Count > MaximumFiles) throw new IOException("Too many files");
        }
        void Walk(string directory)
        {
            foreach (string item in Directory.EnumerateFileSystemEntries(directory))
            {
                FileTransfer.LocalPath(item);
                if (Directory.Exists(item)) Walk(item); else Add(item);
            }
        }
    }
    internal static void CopyProfile(string source, string stage, CancellationToken token)
    {
        var files = Inventory(source); long size = files.Sum(p => new FileInfo(p).Length);
        if (size > MaximumBytes) throw new IOException("Migration exceeds 2 GiB");
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested(); string target = Path.Combine(stage, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(FileTransfer.LocalPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536]; int count;
            while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            output.Flush(true);
        }
    }
    internal static void DeleteOwned(string profile, string path)
    {
        path = FileTransfer.LocalPath(path);
        if (!FileTransfer.Within(profile, path)) throw new IOException("Cleanup escaped profile");
        if (Directory.Exists(path))
        {
            // Inspect every component before recursive removal; never follow a link.
            foreach (string entry in Directory.EnumerateFileSystemEntries(path)) DeleteOwned(profile, entry);
            Directory.Delete(path);
        }
        else if (File.Exists(path))
        {
            if (ProfileKeys.IsInternalKey(profile, path)) DeletionFile.DestroyKey(DeletionFile.Inspect(path));
            else if (DatabaseFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal) ||
                Path.GetRelativePath(profile, path).Split(Path.DirectorySeparatorChar).Any(part => Folders.Contains(part, StringComparer.Ordinal)))
                DeletionFile.Erase(DeletionFile.Inspect(path), CancellationToken.None);
            // Recovery journal/commit markers contain no chat data. Unlink them
            // atomically: an interrupted overwrite could leave a malformed journal
            // that prevents startup from completing an otherwise committed cleanup.
            else DeletionFile.Delete(DeletionFile.Inspect(path));
        }
    }
}
