using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PrivateChat;

internal static class ErasureTests
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    private sealed class InlineProgress(Action<DeletionProgress> report) : IProgress<DeletionProgress>
    { public void Report(DeletionProgress value) => report(value); }
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "erase-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string text) { if (!value) throw new Exception(text); checks.Add(text); Save("running"); }
        bool Fails(Action action) { try { action(); return false; } catch { return true; } }
        string FileAt(string name, int size = 131113) { string path = Path.Combine(root, name); File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size)); return path; }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            string complete = FileAt("complete.bin"); var stamp = DeletionFile.Inspect(complete); long last = 0;
            DeletionFile.Erase(stamp, CancellationToken.None, count => { if (count <= last || count > stamp.Bytes) throw new Exception("Non-monotonic progress"); last = count; });
            Check(!File.Exists(complete) && last == stamp.Bytes, "Full multi-chunk file length is verified before deletion");
            string empty = FileAt("empty.bin", 0); DeletionFile.Erase(DeletionFile.Inspect(empty), CancellationToken.None);
            Check(!File.Exists(empty), "Empty ordinary file is handled without inventing verified bytes");
            string cancelled = FileAt("cancelled.bin"); string before = Hash(cancelled);
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel(); Check(Fails(() => DeletionFile.Erase(DeletionFile.Inspect(cancelled), stop.Token)) && Hash(cancelled) == before, "Cancellation before overwrite preserves the whole file");
            }
            string inspected = FileAt("verified-but-retained.bin", 262151); long originalLength = new FileInfo(inspected).Length;
            using (var stop = new CancellationTokenSource())
            {
                Check(Fails(() => DeletionFile.Erase(DeletionFile.Inspect(inspected), stop.Token, count => { if (count == originalLength) stop.Cancel(); })), "Stop after read-back prevents final file deletion");
                Check(File.Exists(inspected) && new FileInfo(inspected).Length == originalLength && File.ReadAllBytes(inspected).All(b => b == 0), "Retained file independently confirms complete overwrite rather than ordinary unlink");
            }
            DeletionFile.Erase(DeletionFile.Inspect(inspected), CancellationToken.None);
            Check(!File.Exists(inspected), "Freshly reviewed partial operation can complete");
            string occupied = FileAt("occupied.bin"); before = Hash(occupied); stamp = DeletionFile.Inspect(occupied);
            using (var held = new FileStream(occupied, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(Fails(() => DeletionFile.Erase(stamp, CancellationToken.None)), "Occupied file is never falsely reported erased");
            Check(Hash(occupied) == before, "Occupied-file refusal preserves its original bytes");
            string changed = FileAt("changed.bin"); stamp = DeletionFile.Inspect(changed); File.Move(changed, changed + ".original"); File.WriteAllText(changed, "REPLACEMENT-KEEP");
            Check(Fails(() => DeletionFile.Erase(stamp, CancellationToken.None)) && File.ReadAllText(changed) == "REPLACEMENT-KEEP", "Replacement file identity is checked before any overwrite");
            string readOnly = FileAt("readonly.bin"); before = Hash(readOnly); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            Check(Fails(() => DeletionFile.Erase(DeletionFile.Inspect(readOnly), CancellationToken.None)) && Hash(readOnly) == before, "Read-only file produces a refusal without changing attributes or bytes");
            File.SetAttributes(readOnly, FileAttributes.Normal);
            string alternate = FileAt("streams.bin"); File.WriteAllText(alternate + ":private-test", "HIDDEN-SYNTHETIC-CONTENT"); before = Hash(alternate);
            Check(Fails(() => DeletionFile.Erase(DeletionFile.Inspect(alternate), CancellationToken.None)), "Additional data stream prevents an incomplete clearance claim");
            Check(Hash(alternate) == before && File.ReadAllText(alternate + ":private-test") == "HIDDEN-SYNTHETIC-CONTENT", "Unexpected stream refusal happens before changing either stream");
            string sparse = FileAt("sparse.bin", 131072);
            using (var file = new FileStream(sparse, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(DeviceIoControl(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "Synthetic sparse-file fixture created");
            before = Hash(sparse);
            Check(Fails(() => DeletionFile.Erase(DeletionFile.Inspect(sparse), CancellationToken.None)) && Hash(sparse) == before, "Sparse file is refused without expanding allocated storage");

            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            async Task<string> Profile(string name)
            {
                string profile = Path.Combine(root, name); Directory.CreateDirectory(profile); ProfileKeys.CreateNew(profile, password);
                using (var core = new CoreClient())
                { await core.Init(Path.Combine(profile, "chat"), password); await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}"); await core.CloseStore(); }
                await ProfileBackup.WaitForProfile(profile, CancellationToken.None);
                string prior = Path.Combine(profile, ".restore-history", "synthetic", "previous"); Directory.CreateDirectory(prior);
                File.Copy(Path.Combine(profile, ProfileKeys.FileName), Path.Combine(prior, ProfileKeys.FileName));
                return profile;
            }
            string blockedProfile = await Profile("blocked-key");
            File.WriteAllText(Path.Combine(blockedProfile, "manual.pcbackup"), "USER-MANUAL-BACKUP-KEEP"); File.WriteAllText(Path.Combine(blockedProfile, "export.txt"), "USER-EXPORT-KEEP");
            var plan = await ProfileDeletion.PrepareProfile(blockedProfile, password, CancellationToken.None);
            string blockedKey = Path.Combine(blockedProfile, ProfileKeys.FileName); File.SetAttributes(blockedKey, FileAttributes.ReadOnly);
            DeletionIncompleteException? incomplete = null;
            try { ProfileDeletion.Execute(plan, plan.Phrase, true, CancellationToken.None); } catch (DeletionIncompleteException ex) { incomplete = ex; }
            Check(incomplete?.Failures == 1 && ProfileDeletion.Pending(blockedProfile), "One blocked key produces an explicit incomplete result and retains the pending marker");
            Check(File.Exists(blockedKey) && !plan.Entries.Where(e => plan.IsKey(e) && e.Path != blockedKey).Any(e => File.Exists(e.Path)), "Blocked key does not prevent all other reviewed keys from being destroyed");
            Check(!File.Exists(Path.Combine(blockedProfile, "chat_chat.db")) && !File.Exists(Path.Combine(blockedProfile, "chat_agent.db")), "Remaining ciphertext is still erased when one key cannot be removed");
            Check(File.ReadAllText(Path.Combine(blockedProfile, "manual.pcbackup")) == "USER-MANUAL-BACKUP-KEEP" && File.ReadAllText(Path.Combine(blockedProfile, "export.txt")) == "USER-EXPORT-KEEP", "Stronger cleanup preserves manual backups and exported files");
            File.SetAttributes(blockedKey, FileAttributes.Normal); plan = await ProfileDeletion.PrepareProfile(blockedProfile, "", CancellationToken.None);
            ProfileDeletion.Execute(plan, plan.Phrase, true, CancellationToken.None);
            Check(!ProfileDeletion.Pending(blockedProfile) && !ProfileKeys.Exists(blockedProfile), "Pending key cleanup can finish after the obstruction is removed");

            string stoppedProfile = await Profile("stop-during-keys"); plan = await ProfileDeletion.PrepareProfile(stoppedProfile, password, CancellationToken.None);
            string dbHash = Hash(Path.Combine(stoppedProfile, "chat_chat.db"));
            using (var stop = new CancellationTokenSource())
            {
                var progress = new InlineProgress(value => { if (value.Stage == "keys" && value.Completed == 1) stop.Cancel(); });
                Check(Fails(() => ProfileDeletion.Execute(plan, plan.Phrase, true, stop.Token, progress)), "Stop requested during key phase is reported as incomplete");
                Check(plan.Entries.Where(plan.IsKey).All(e => !File.Exists(e.Path)), "All reviewed keys are attempted before honoring cancellation");
                Check(ProfileDeletion.Pending(stoppedProfile) && Hash(Path.Combine(stoppedProfile, "chat_chat.db")) == dbHash, "Stop is honored before bulk ciphertext overwrite and prevents reopening");
            }
            plan = await ProfileDeletion.PrepareProfile(stoppedProfile, "", CancellationToken.None); var progressRecords = new List<DeletionProgress>();
            ProfileDeletion.Execute(plan, plan.Phrase, true, CancellationToken.None, new InlineProgress(progressRecords.Add));
            Check(!ProfileDeletion.Pending(stoppedProfile) && progressRecords.Any(p => p.Stage == "verify" && p.VerifiedBytes == p.TotalBytes), "Completion requires full read-back accounting and a final residual scan");
            Save("passed"); return 0;
        }
        catch (Exception ex) { Save("failed", ex is CoreException ce ? ce.Diagnostic : ex.ToString()); return 1; }
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "erase-qa.json"), JsonSerializer.Serialize(new { status, reason, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
