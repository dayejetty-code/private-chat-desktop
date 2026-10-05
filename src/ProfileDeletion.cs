using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace PrivateChat;

internal sealed record DeletionProgress(string Stage, int Completed, int Total, int Failures, long VerifiedBytes, long TotalBytes);
internal sealed class DeletionIncompleteException(int failures) : IOException("Some reviewed entries could not be cleared")
{ internal int Failures { get; } = failures; }

internal sealed class DeletionPlan
{
    internal string? Profile { get; }
    internal DeletionFile.Stamp? Root { get; }
    internal IReadOnlyList<DeletionFile.Stamp> Entries { get; }
    internal int UnrecognizedRoots { get; }
    internal string Phrase => Profile == null ? "删除所选备份" : "销毁账号与聊天数据";
    internal long Bytes => Entries.Where(e => !e.Directory).Sum(e => e.Bytes);
    internal int Files => Entries.Count(e => !e.Directory);
    internal bool IsKey(DeletionFile.Stamp entry)
    {
        return Profile != null && !entry.Directory && ProfileKeys.IsInternalKey(Profile, entry.Path);
    }
    internal int Keys => Entries.Count(IsKey);
    internal DeletionPlan(string? profile, DeletionFile.Stamp? root, IEnumerable<DeletionFile.Stamp> entries, int unknown = 0)
    { Profile = profile; Root = root; Entries = Array.AsReadOnly(entries.ToArray()); UnrecognizedRoots = unknown; }
}

internal static class ProfileDeletion
{
    internal const string PendingName = ".delete-pending";
    internal static bool Pending(string root) => File.Exists(Path.Combine(root, PendingName)) || Directory.Exists(Path.Combine(root, PendingName));
    internal static bool Managed(string name) => ProfileBackup.Roots.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        new[] { "tor", ".restore-history", ".restore-transaction" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        Regex.IsMatch(name, @"\A\.backup-work-[a-f0-9]{32}\z");
    internal static async Task<DeletionPlan> PrepareProfile(string root, string password, CancellationToken token)
    {
        root = FileTransfer.LocalPath(root);
        using var pin = new DeletionFile.Pins(root, true);
        if (!Pending(root))
        {
            if (password.Length is < 12 or > 256 || !File.Exists(Path.Combine(root, "chat_chat.db")) || !File.Exists(Path.Combine(root, "chat_agent.db"))) throw new InvalidDataException("Existing profile password required");
            await ProfileBackup.WaitForProfile(root, token);
            // The UI stops the old worker and Tor first and retains instance.lock.
            // This worker validates the existing password offline, without making a backup.
            using var client = new CoreClient();
            using var process = Process.GetProcessById(client.ProcessId); _ = process.SafeHandle;
            using var cancellation = token.Register(client.Dispose);
            try
            {
                if ((await client.Init(Path.Combine(root, "chat"), password))["type"]?.ToString() != "ok") throw new InvalidDataException("Incorrect profile password");
                await client.Result("/u"); await client.CloseStore();
            }
            finally { client.Dispose(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await ProfileBackup.WaitForProfile(root, CancellationToken.None); }
        }
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => InspectProfile(root, token), token);
    }
    internal static DeletionPlan InspectProfile(string root, CancellationToken token = default)
    {
        var files = new List<DeletionFile.Stamp>(); int unknown = 0;
        var rootStamp = DeletionFile.Inspect(root);
        if (!rootStamp.Directory) throw new IOException("Invalid profile directory");
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            if (Managed(name)) Walk(entry, 0);
            else if (name is not ("instance.lock" or PendingName)) ++unknown;
        }
        return new(root, rootStamp, files, unknown);
        void Walk(string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            path = FileTransfer.LocalPath(path);
            if (!FileTransfer.Within(root, path) || depth > 64 || files.Count >= 50000) throw new IOException("Deletion inventory exceeds safe bounds");
            var stamp = DeletionFile.Inspect(path); files.Add(stamp);
            if (stamp.Directory) foreach (string child in Directory.EnumerateFileSystemEntries(path)) Walk(child, depth + 1);
        }
    }
#if ENABLE_QA
    internal static DeletionPlan PrepareBackups(string profile, IEnumerable<string> paths)
    {
        profile = FileTransfer.LocalPath(profile);
        var selected = paths.Select(FileTransfer.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length is < 1 or > 100) throw new IOException("Choose 1 to 100 backups");
        // Exact files only, including archives the user put inside the profile.
        // Never recursively scan the default backup directory or another drive.
        return new(null, null, selected.Select(p => DeletionFile.Inspect(p, backup: true)));
    }
#endif
    internal static void Execute(DeletionPlan plan, string phrase, bool acknowledged, CancellationToken token, IProgress<DeletionProgress>? progress = null)
    {
#if !ENABLE_QA
        if (plan.Profile == null) throw new InvalidOperationException("Only local profile cleanup is available");
#endif
        if (!acknowledged || phrase != plan.Phrase) throw new InvalidOperationException("Explicit deletion confirmation required");
        token.ThrowIfCancellationRequested();
        using var pin = plan.Profile == null ? null : new DeletionFile.Pins(plan.Profile, true);
        if (plan.Profile != null)
        {
            var current = InspectProfile(plan.Profile, token);
            if (!DeletionFile.Same(plan.Root!, current.Root!) || current.Entries.Count != plan.Entries.Count ||
                !current.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Zip(plan.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)).All(pair => DeletionFile.Same(pair.First, pair.Second)))
                throw new IOException("Profile changed after review");
        }
        else foreach (var entry in plan.Entries) if (!DeletionFile.Same(entry, DeletionFile.Inspect(entry.Path, backup: true))) throw new IOException("Backup changed after review");
        token.ThrowIfCancellationRequested();
        string? marker = plan.Profile == null ? null : FileTransfer.LocalPath(Path.Combine(plan.Profile, PendingName));
        if (marker != null && !Pending(plan.Profile!))
        {
            using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write("Private Chat deletion was explicitly confirmed. Do not open or restore this profile until reviewed cleanup completes."u8); output.Flush(true);
        }
        int failures = 0, completed = 0; long verifiedBytes = 0;
        // Once committed, attempt every reviewed key even if one is occupied or
        // cancellation is requested. Stop is honored again before bulk file work.
        progress?.Report(new("keys", 0, plan.Keys, 0, 0, plan.Bytes));
        foreach (var entry in plan.Entries.Where(plan.IsKey))
        {
            try { DeletionFile.DestroyKey(entry); verifiedBytes += entry.Bytes; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ++failures; }
            progress?.Report(new("keys", ++completed, plan.Keys, failures, verifiedBytes, plan.Bytes));
        }
        token.ThrowIfCancellationRequested();
        var rest = plan.Entries.Where(e => !plan.IsKey(e)).OrderByDescending(e => e.Path.Count(c => c == '\\')).ThenBy(e => e.Directory).ToArray();
        completed = 0;
        foreach (var entry in rest)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (entry.Directory || plan.Profile == null) DeletionFile.Delete(entry);
                else
                {
                    long before = verifiedBytes;
                    var throttle = Stopwatch.StartNew();
                    DeletionFile.Erase(entry, token, bytes =>
                    {
                        if (bytes == entry.Bytes || throttle.ElapsedMilliseconds >= 100)
                        { progress?.Report(new("files", completed, rest.Length, failures, before + bytes, plan.Bytes)); throttle.Restart(); }
                    });
                    verifiedBytes += entry.Bytes;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ++failures; }
            progress?.Report(new("files", ++completed, rest.Length, failures, verifiedBytes, plan.Bytes));
        }
        if (failures != 0) throw new DeletionIncompleteException(failures);
        if (plan.Profile != null)
        {
            progress?.Report(new("verify", 0, 1, 0, verifiedBytes, plan.Bytes));
            if (InspectProfile(plan.Profile).Entries.Count != 0) throw new IOException("Some application data remains");
            if (marker != null) DeletionFile.Delete(DeletionFile.Inspect(marker));
        }
    }
}
