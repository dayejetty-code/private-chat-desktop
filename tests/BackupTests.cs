using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrivateChat;

internal static class BackupTests
{
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string archive = Path.Combine(root, "saved.pcbackup");
        var checks = new List<string>(); CoreClient? core = null;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); }
        Dictionary<string, string> Hashes(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(folder, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        bool Same(Dictionary<string,string> a, Dictionary<string,string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var hash) && hash == p.Value);
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        try
        {
            core = new CoreClient(); Assert((await core.Init(Path.Combine(source, "chat"), password))["type"]?.ToString() == "ok", "Synthetic SQLCipher profile opens");
            var created = await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            string identity = created["user"]!.ToJsonString(); await FileTransfer.Configure(core, source);
            string txt = Path.Combine(root, "input.txt"); File.WriteAllText(txt, new string('x', 180000) + "\n合成本地备份测试");
            var encrypted = await core.EncryptFile(txt);
            string originalPath = Path.Combine(source, "files", encrypted["filePath"]!.GetValue<string>());
            // Include an absolute-path legacy receive record; restore must relocate it.
            await core.Result("/sql chat INSERT INTO files (file_id,file_name,file_path,file_size,chunk_size,user_id,ci_file_status,protocol,updated_at) VALUES (1,'synthetic.txt','" + originalPath.Replace("'", "''") + "'," + new FileInfo(txt).Length + ",16384,1,'rcv_complete','xftp',datetime('now'))");
            await core.Result("/sql chat CREATE TABLE qa_backup_messages (body TEXT NOT NULL)");
            await core.Result("/sql chat INSERT INTO qa_backup_messages VALUES ('SYNTHETIC-BACKUP-MESSAGE-ONLY')");
            await Stop(core); core = null;
            await ProfileBackup.WaitForProfile(source, CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(source, "tor")); File.WriteAllText(Path.Combine(source, "tor", "excluded-state"), "TOR-CACHE-EXCLUDED");
            File.WriteAllText(Path.Combine(source, "ignored.log"), "UNRELATED-LOG-EXCLUDED");
            var before = Hashes(source);
            Assert(await Fails(() => ProfileBackup.Create(source, archive, password + "wrong", CancellationToken.None)), "Wrong database password cannot publish a backup");
            Assert(!File.Exists(archive) && Same(before, Hashes(source)), "Failed backup preserves every source byte");
            await ProfileBackup.Create(source, archive, password, CancellationToken.None);
            Assert(File.Exists(archive) && Same(before, Hashes(source)), "Successful verified backup does not modify the original profile");
            byte[] bytes = File.ReadAllBytes(archive);
            string scan = Encoding.UTF8.GetString(bytes);
            Assert(!scan.Contains(source) && !scan.Contains("chat_chat.db") && !scan.Contains("synthetic.txt") && !scan.Contains("SYNTHETIC-BACKUP-MESSAGE-ONLY") && !scan.Contains(password), "Outer encryption hides paths, manifest, text and password");
            await ProfileBackup.Create(source, Path.Combine(root, "second.pcbackup"), password, CancellationToken.None);
            Assert(!bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "second.pcbackup"))), "Independent backups use fresh salt and nonce");
            Assert(await Fails(() => ProfileBackup.Create(source, archive, password, CancellationToken.None)) && bytes.SequenceEqual(File.ReadAllBytes(archive)), "Existing backup cannot be overwritten");
            Assert(await Fails(() => ProfileBackup.Restore(target, archive, password + "wrong", CancellationToken.None)) && Directory.GetFileSystemEntries(target).Length == 0, "Wrong archive password leaves destination empty");
            foreach (int offset in new[] { 0, 8, 40, 48, bytes.Length / 2, bytes.Length - 1 })
            {
                byte[] corrupt = (byte[])bytes.Clone(); corrupt[offset] ^= 1; string bad = Path.Combine(root, "bad.pcbackup"); File.WriteAllBytes(bad, corrupt);
                Assert(await Fails(() => ProfileBackup.Restore(target, bad, password, CancellationToken.None)), "Corruption rejected at byte " + offset);
                Assert(Directory.GetFileSystemEntries(target).Length == 0, "Corruption never installs partial data at byte " + offset);
            }
            foreach (int length in new[] { 0, 47, bytes.Length - 1, bytes.Length - 16, bytes.Length / 2 })
            {
                string bad = Path.Combine(root, "short.pcbackup"); File.WriteAllBytes(bad, bytes[..length]);
                Assert(await Fails(() => ProfileBackup.Restore(target, bad, password, CancellationToken.None)), "Truncation rejected at length " + length);
            }
            string appended = Path.Combine(root, "append.pcbackup"); File.WriteAllBytes(appended, bytes.Concat(new byte[] { 1 }).ToArray());
            Assert(await Fails(() => ProfileBackup.Restore(target, appended, password, CancellationToken.None)), "Trailing unauthenticated data rejected");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert(await Fails(() => ProfileBackup.Restore(target, archive, password, cancellation.Token)) && Directory.GetFileSystemEntries(target).Length == 0, "Cancelled restore leaves original destination untouched");
            }
            // An independent stream test corrupts frame order and validates authenticated completion.
            string raw = Path.Combine(root, "raw"); Directory.CreateDirectory(raw);
            File.WriteAllBytes(Path.Combine(raw, "chat_chat.db"), RandomNumberGenerator.GetBytes(3 * BackupArchive.ChunkSize));
            File.WriteAllBytes(Path.Combine(raw, "chat_agent.db"), RandomNumberGenerator.GetBytes(200));
            string frames = Path.Combine(root, "frames.pcbackup"); BackupArchive.Write(raw, raw, frames, password, CancellationToken.None);
            byte[] frameBytes = File.ReadAllBytes(frames);
            int metaLength = JsonSerializer.SerializeToUtf8Bytes(new BackupArchive.Manifest(1, raw, ProfileBackup.Inventory(raw).Select(p => new BackupArchive.Entry(Path.GetFileName(p), new FileInfo(p).Length)).ToArray())).Length;
            int dataStart = 48 + 20 + metaLength + 16; int frameLength = BackupArchive.ChunkSize + 16;
            byte[] first = frameBytes.AsSpan(dataStart, frameLength).ToArray(); frameBytes.AsSpan(dataStart + frameLength, frameLength).CopyTo(frameBytes.AsSpan(dataStart)); first.CopyTo(frameBytes, dataStart + frameLength);
            File.WriteAllBytes(frames, frameBytes);
            Assert(await Fails(() => Task.Run(() => BackupArchive.Extract(frames, Path.Combine(root, "frames-out"), password, CancellationToken.None))), "Reordered full ciphertext chunks rejected");
            foreach (string invalid in new[] { "../chat_chat.db", "files/../../outside", "files/a:stream", "files/CON", "files/a.", "files//x", "files/a\\b", "instance.lock", "tor/state", "files/LPT1.txt", "/chat_chat.db", "files/.. /x" })
                Assert(!BackupArchive.SafeEntry(invalid), "Unsafe archive entry refused: " + invalid);
            await ProfileBackup.Restore(target, archive, password, CancellationToken.None);
            Assert(!Directory.Exists(Path.Combine(target, "tor")) && !File.Exists(Path.Combine(target, "ignored.log")), "Restore excludes Tor state and unrelated logs");
            core = new CoreClient(); Assert((await core.Init(Path.Combine(target, "chat"), password))["type"]?.ToString() == "ok", "Restored profile opens at a different path");
            Assert((await core.Result("/u"))["user"]!["userId"]!.ToString() == created["user"]!["userId"]!.ToString(), "Local identity survives restore");
            var message = await core.Result("/sql chat SELECT body FROM qa_backup_messages");
            Assert(message["rows"]![1]!.ToString() == "SYNTHETIC-BACKUP-MESSAGE-ONLY", "Encrypted message content survives restore");
            await FileTransfer.Configure(core, target);
            var relocated = await core.Result("/sql chat SELECT file_path FROM files WHERE file_id=1");
            Assert(relocated["rows"]![1]!.ToString() == Path.GetFileName(originalPath), "Legacy absolute attachment path becomes portable");
            string exported = Path.Combine(root, "exported.txt"); await core.ExportFile(encrypted, new FileInfo(txt).Length, exported);
            Assert(SHA256.HashData(File.ReadAllBytes(txt)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(exported))), "Restored encrypted attachment exports with identical content hash");
            await core.Result("/sql chat UPDATE files SET ci_file_status='rcv_accepted' WHERE file_id=1");
            await Stop(core); core = null;
            await ProfileBackup.WaitForProfile(target, CancellationToken.None);
            Assert(await Fails(() => ProfileBackup.Create(target, Path.Combine(root, "active.pcbackup"), password, CancellationToken.None)), "Unfinished attachment tasks prevent a misleading backup");
            var existing = Hashes(target).Where(p => !p.Key.StartsWith(".restore-history")).ToDictionary(p => p.Key, p => p.Value);
            string? old = await ProfileBackup.Restore(target, archive, password, CancellationToken.None);
            Assert(old != null && existing.All(p => File.Exists(Path.Combine(old!, p.Key)) && p.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(old!, p.Key))))), "Replacing existing profile preserves prior encrypted files byte for byte");
            var targetBefore = Hashes(target);
            Assert(await Fails(() => ProfileBackup.Restore(target, appended, password, CancellationToken.None)) && Same(targetBefore, Hashes(target)), "Damaged restore never alters an existing profile or recovery copy");
            TestRecovery(root, Assert);
            Assert(Same(before, Hashes(source)), "Original source remains byte-for-byte unchanged after entire suite");
            Assert(!Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories).Any() && !Directory.GetDirectories(source, ".backup-work-*").Any(), "No incomplete archive or source staging directory remains");
            Save("passed"); return 0;
        }
        catch (Exception ex) { Save("failed", ex is CoreException ce ? ce.Diagnostic : ex.ToString()); return 1; }
        finally { core?.Dispose(); }
        void Save(string status, string? error = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "backup-qa.json"), new JsonObject { ["status"] = status, ["reason"] = error, ["checks"] = new JsonArray(checks.Select(c => JsonValue.Create(c)).ToArray()), ["syntheticRoot"] = root }.ToJsonString(new() { WriteIndented = true }));
    }

    private static void TestRecovery(string root, Action<bool, string> assert)
    {
        // Simulate process termination before moves, during old moves, during new moves,
        // after commit, and during rollback. Keep a live instance.lock throughout recovery.
        foreach (int step in Enumerable.Range(0, 5))
        {
            string profile = Path.Combine(root, "crash-" + step); Directory.CreateDirectory(profile);
            using var held = new FileStream(Path.Combine(profile, "instance.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            string tx = Path.Combine(profile, ".restore-transaction"), previous = Path.Combine(tx, "previous"); Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(tx, "pending.json"), JsonSerializer.Serialize(new { Originals = new[] { "chat_chat.db", "chat_agent.db" }, RecoveryName = "before-restore-20261005-120000-" + Guid.NewGuid().ToString("N") }));
            File.WriteAllText(Path.Combine(profile, "chat_chat.db"), "original-chat"); File.WriteAllText(Path.Combine(profile, "chat_agent.db"), "original-agent");
            if (step >= 1) File.Move(Path.Combine(profile, "chat_chat.db"), Path.Combine(previous, "chat_chat.db"));
            if (step >= 2) { File.Move(Path.Combine(profile, "chat_agent.db"), Path.Combine(previous, "chat_agent.db")); File.WriteAllText(Path.Combine(profile, "chat_chat.db"), "new-chat"); }
            if (step == 3) { File.WriteAllText(Path.Combine(profile, "chat_agent.db"), "new-agent"); File.WriteAllText(Path.Combine(tx, "committed"), "committed"); }
            if (step == 4) { File.Delete(Path.Combine(profile, "chat_chat.db")); File.WriteAllText(Path.Combine(profile, "chat_chat.db"), "partially-rolled-back"); }
            ProfileRestore.Startup(profile); ProfileRestore.Startup(profile);
            string expected = step == 3 ? "new" : "original";
            assert(File.ReadAllText(Path.Combine(profile, "chat_chat.db")) == expected + "-chat" && File.ReadAllText(Path.Combine(profile, "chat_agent.db")) == expected + "-agent", "Crash recovery is complete and idempotent at stage " + step);
        }
    }
    private static async Task Stop(CoreClient core)
    { using var process = Process.GetProcessById(core.ProcessId); _ = process.SafeHandle; core.Dispose(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }

    public static int RunUi()
    {
        var app = new Application(); string root = Path.Combine(RuntimeSecurity.Base, "qa", "backup-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var window = new MainWindow(root); var checks = new List<string>();
        void Assert(bool value, string text) { if (!value) throw new Exception(text); checks.Add(text); }
        try
        {
            typeof(MainWindow).GetField("captureProtected", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            typeof(MainWindow).GetMethod("Backup_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert(!((Button)window.FindName("BackupCreateButton")).IsEnabled && ((Button)window.FindName("BackupRestoreButton")).IsEnabled, "Empty locked profile can restore without creating a new identity");
            string syncNotice = ((TextBlock)window.FindName("BackupSyncNotice")).Text;
            Assert(syncNotice.Contains("OneDrive") && syncNotice.Contains("百度网盘") && syncNotice.Contains("未同步的本地文件夹或 U 盘"), "Backup panel explains third-party sync with concrete examples and a local-only storage choice");
            ((TextBlock)window.FindName("BackupStatus")).Text = "备份已保存并通过完整性校验：\nC:\\Users\\合成用户\\PrivateChatBackups\\PrivateChat-20261005-120000.pcbackup\n请妥善保管口令，遗忘后无法恢复。";
            foreach (var (width, height) in new[] { (940,620), (1440,900), (2560,1440) })
            {
                window.Width = width; window.Height = height; var surface = (FrameworkElement)window.Content;
                surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
                var panel = (Grid)window.FindName("BackupPanel"); var card = (Border)panel.Children[0];
                Assert(card.ActualWidth <= 600 && card.ActualHeight <= height - 48, "Backup layout remains bounded at " + width);
                var image = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); image.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(RuntimeSecurity.Base, "backup-ui-" + width + ".png")); encoder.Save(output);
            }
            ((PasswordBox)window.FindName("BackupPassword")).Password = "synthetic-test-password"; window.EmergencyLock();
            Assert(((PasswordBox)window.FindName("BackupPassword")).Password == "" && ((Grid)window.FindName("BackupPanel")).Visibility == Visibility.Collapsed, "Lock clears backup password and hides paths");
            typeof(MainWindow).GetField("backupRunning", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true); window.EmergencyLock();
            Assert(!((Button)window.FindName("UnlockButton")).IsEnabled && !((Button)window.FindName("BackupButton")).IsEnabled, "Lock cannot allow concurrent unlock or backup while local operation runs");
            typeof(MainWindow).GetField("backupRunning", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
            File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"backup-ui-qa.json"),JsonSerializer.Serialize(new { status="passed", checks }, new JsonSerializerOptions { WriteIndented=true })); return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"backup-ui-qa.json"), JsonSerializer.Serialize(new { status="failed", reason=ex.ToString(), checks })); return 1; }
        finally { window.Close(); }
    }
}
