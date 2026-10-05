using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PrivateChat;

internal static class PrivacyTests
{
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateAnonymousToken(IntPtr thread);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool RevertToSelf();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Dictionary<string, string> Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p), Hash);
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var value) && value == e.Value);
    private static void LegacyEnvelope(string root, string password, byte[] secret)
    {
        byte[] envelope = new byte[100]; "PCKEY001"u8.CopyTo(envelope); RandomNumberGenerator.Fill(envelope.AsSpan(8, 44));
        byte[] wrap = Rfc2898DeriveBytes.Pbkdf2(password, envelope.AsSpan(8, 32), 600000, HashAlgorithmName.SHA256, 32);
        try { using var cipher = new AesGcm(wrap, 16); cipher.Encrypt(envelope.AsSpan(40, 12), secret, envelope.AsSpan(52, 32), envelope.AsSpan(84, 16), envelope.AsSpan(0, 52)); File.WriteAllBytes(Path.Combine(root, ProfileKeys.FileName), envelope); }
        finally { CryptographicOperations.ZeroMemory(wrap); CryptographicOperations.ZeroMemory(envelope); }
    }
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "privacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var checks = new List<string>(); double deriveMs = 0;
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "privacy-qa.json"), JsonSerializer.Serialize(new { status, reason, checks, deriveMs }, new JsonSerializerOptions { WriteIndented = true }));
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks.Add(text); Save("running"); }
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        try
        {
            byte[] vector = PasswordKdf.TestVector("", [], 16, 1, 1, 64);
            Check(Convert.ToHexString(vector) == "77D6576238657B203B19CA42C18A0497F16B4844E3074AE8DFDFFA3FEDE21442FCD0069DED0948F8326A753A0FC81F17E8D3E0FB2E0D3628CF35E20C38D18906", "Pinned OpenSSL scrypt matches RFC 7914 empty-password test vector");
            var watch = Stopwatch.StartNew();
            byte[] derived = PasswordKdf.Derive("synthetic scrypt test", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()); deriveMs = watch.Elapsed.TotalMilliseconds;
            Check(Convert.ToHexString(derived) == "7169A2D0E75C12705ADD3991F61E2810DC549A6A5C15FDC998B0BF2AC8F779BF", "Production work factors match independently computed Python hashlib.scrypt fixture");
            Check(PasswordKdf.Cost == 131072 && PasswordKdf.BlockSize == 8 && PasswordKdf.Parallelism == 1, "scrypt uses 128 MiB memory-cost configuration with fixed authenticated format");
            byte[] synthetic = RandomNumberGenerator.GetBytes(100), bound = UserKeyProtection.Protect(synthetic);
            Check(UserKeyProtection.Unprotect(bound).SequenceEqual(synthetic), "Windows CurrentUser protection round-trips only the synthetic key material");
            bool anonymousRejected = false;
            if (!ImpersonateAnonymousToken(GetCurrentThread())) throw new IOException("Cannot enter anonymous test context");
            try { try { UserKeyProtection.Unprotect(bound); } catch (CryptographicException) { anonymousRejected = true; } }
            finally { if (!RevertToSelf()) Environment.FailFast("Cannot leave synthetic impersonation context"); }
            Check(anonymousRejected, "Windows DPAPI rejects the same protected bytes under an anonymous impersonated context");
            Check(UserKeyProtection.Unprotect(bound).SequenceEqual(synthetic), "Original user context still decrypts after rejected anonymous attempt");
            byte[] tampered = (byte[])bound.Clone(); tampered[^1] ^= 1;
            Check(await Fails(() => Task.Run(() => UserKeyProtection.Unprotect(tampered))), "DPAPI rejects authenticated-data tampering without a weaker fallback");
            using (var dll = File.OpenRead(Path.ChangeExtension(RuntimeSecurity.WorkerStart().FileName, ".dll")))
            using (var pe = new PEReader(dll))
            {
                var md = pe.GetMetadataReader();
                var types = md.TypeDefinitions.Select(h => md.GetTypeDefinition(h)).ToArray();
                Check(!types.Any(t => md.GetString(t.Name) == "BackupArchive"), "Production assembly contains no backup archive reader or writer");
                var common = types.Single(t => md.GetString(t.Name) == "ProfileBackup");
                var methods = common.GetMethods().Select(h => md.GetString(md.GetMethodDefinition(h).Name)).ToArray();
                Check(!methods.Intersect(new[] { "Create", "Restore", "BackupPath" }).Any(), "Production assembly contains no backup create or restore entrypoints");
                var window = types.Single(t => md.GetString(t.Name) == "MainWindow");
                Check(!window.GetMethods().Any(h => md.GetString(md.GetMethodDefinition(h).Name).StartsWith("Backup", StringComparison.Ordinal)), "Production window has no backup action handlers");
                Check(!window.GetMethods().Any(h => md.GetString(md.GetMethodDefinition(h).Name) == "DeletionChoose_Click") &&
                    !types.Single(t => md.GetString(t.Name) == "ProfileDeletion").GetMethods().Any(h => md.GetString(md.GetMethodDefinition(h).Name) == "PrepareBackups"), "Production contains no selected-backup deletion action or preparation entrypoint");
            }
            string profile = Path.Combine(root, "upgraded"); Directory.CreateDirectory(profile);
            string password = "旧口令 Unicode with spaces ' 2026"; byte[] oldSecret = RandomNumberGenerator.GetBytes(32);
            LegacyEnvelope(profile, password, oldSecret);
            Check(!ProfileKeys.IsStrong(profile) && ProfileKeys.Resolve(profile, password) == Convert.ToHexString(oldSecret), "Version 1 envelopes remain readable without modifying their bytes");
            using (var core = new CoreClient())
            {
                await core.Init(Path.Combine(profile, "chat"), password);
                await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                await core.Result("/sql chat CREATE TABLE qa_private (body TEXT)");
                await core.Result("/sql chat INSERT INTO qa_private VALUES ('SYNTHETIC-PRESERVED-CHAT')");
                await core.CloseStore();
            }
            await ProfileBackup.WaitForProfile(profile, CancellationToken.None);
            string history = Path.Combine(profile, ".restore-history", "older-copy"); Directory.CreateDirectory(history);
            File.WriteAllText(Path.Combine(history, "keep.txt"), "PREEXISTING-HISTORY");
            File.WriteAllText(Path.Combine(profile, "saved.pcbackup"), "EXISTING-MANUAL-BACKUP");
            File.WriteAllText(Path.Combine(profile, "export.txt"), "EXISTING-EXPORT");
            var before = Hashes(profile);
            Check(await Fails(() => ProfileKeyMigration.Enable(profile, password + "bad", CancellationToken.None)) && Same(before, Hashes(profile)), "Wrong upgrade password leaves every source file unchanged");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                Check(await Fails(() => ProfileKeyMigration.Enable(profile, password, stop.Token)) && Same(before, Hashes(profile)), "Pre-cancelled upgrade leaves version 1 profile unchanged");
            }
            await ProfileKeyMigration.Enable(profile, password, CancellationToken.None);
            Check(ProfileKeys.IsStrong(profile), "Version 1 profile upgrades to Windows-user-bound PCKEY003");
            string newSecret = ProfileKeys.Resolve(profile, password);
            Check(newSecret != Convert.ToHexString(oldSecret), "Upgrade rotates database encryption key instead of only rewrapping the old key");
            using (var core = new CoreClient())
            {
                Check((await core.Init(Path.Combine(profile, "chat"), password))["type"]?.ToString() == "ok", "Production worker opens upgraded profile using existing password");
                Check((await core.Result("/sql chat SELECT body FROM qa_private"))["rows"]?[1]?.ToString() == "SYNTHETIC-PRESERVED-CHAT", "Upgrade preserves encrypted message contents");
                await core.CloseStore();
            }
            using (var core = new CoreClient())
                Check((await core.InitRaw(Path.Combine(profile, "chat"), Convert.ToHexString(oldSecret)))["type"]?.ToString() != "ok", "Old recovered database key cannot open current upgraded database");
            await ProfileBackup.WaitForProfile(profile, CancellationToken.None);
            Check(Directory.GetDirectories(Path.Combine(profile, ".restore-history")).Length == 1 && !Directory.Exists(Path.Combine(profile, ".restore-transaction")) && !Directory.GetDirectories(profile, ".backup-work-*").Any(), "Successful upgrade leaves no new persistent rollback or staging copy");
            Check(File.ReadAllText(Path.Combine(history, "keep.txt")) == "PREEXISTING-HISTORY" && File.ReadAllText(Path.Combine(profile, "saved.pcbackup")) == "EXISTING-MANUAL-BACKUP" && File.ReadAllText(Path.Combine(profile, "export.txt")) == "EXISTING-EXPORT", "Preexisting history, manual backup and exported TXT are retained");
            Check(await Fails(() => ProfileKeyMigration.Enable(profile, password, CancellationToken.None)), "Already upgraded profile cannot be needlessly migrated again");
            byte[] valid = File.ReadAllBytes(Path.Combine(profile, ProfileKeys.FileName));
            foreach (int offset in new[] { 7, 8, 40, 52, 84, 99 })
            {
                byte[] bad = (byte[])valid.Clone(); bad[offset] ^= 1; File.WriteAllBytes(Path.Combine(profile, ProfileKeys.FileName), bad);
                Check(await Fails(() => Task.Run(() => ProfileKeys.Resolve(profile, password))), "User-bound envelope tamper or downgrade is rejected at byte " + offset);
            }
            File.WriteAllBytes(Path.Combine(profile, ProfileKeys.FileName), valid);
            Check(await Fails(() => Task.Run(() => ProfileKeys.Resolve(profile, password + "wrong"))), "Wrong user-bound envelope password never falls back to legacy derivation");
            string newProfile = Path.Combine(root, "new"); Directory.CreateDirectory(newProfile);
            Check(await Fails(() => Task.Run(() => ProfileKeys.CreateNew(newProfile, "123456789012"))) && !ProfileKeys.Exists(newProfile), "New profiles reject short passwords without creating a key file");
            ProfileKeys.CreateNew(newProfile, "new synthetic phrase with four words");
            Check(ProfileKeys.IsStrong(newProfile), "Fresh profiles default to Windows-user-bound key protection");
            string v2 = Path.Combine(root, "version2"); Directory.CreateDirectory(v2);
            string v2Password = "synthetic previous-version password";
            byte[] v2Secret = RandomNumberGenerator.GetBytes(32), v2Envelope = new byte[100];
            "PCKEY002"u8.CopyTo(v2Envelope); RandomNumberGenerator.Fill(v2Envelope.AsSpan(8, 44));
            byte[] v2Wrap = PasswordKdf.Derive(v2Password, v2Envelope.AsSpan(8, 32));
            using (var aes = new AesGcm(v2Wrap, 16)) aes.Encrypt(v2Envelope.AsSpan(40, 12), v2Secret, v2Envelope.AsSpan(52, 32), v2Envelope.AsSpan(84, 16), v2Envelope.AsSpan(0, 52));
            File.WriteAllBytes(Path.Combine(v2, ProfileKeys.FileName), v2Envelope);
            Check(!ProfileKeys.IsStrong(v2) && ProfileKeys.Resolve(v2, v2Password) == Convert.ToHexString(v2Secret), "Password-only PCKEY002 remains readable and is offered a user-binding upgrade");
            using (var legacy = new CoreClient())
            {
                await legacy.Init(Path.Combine(v2, "chat"), v2Password); await legacy.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                await legacy.Result("/sql chat CREATE TABLE qa_binding (body TEXT)"); await legacy.Result("/sql chat INSERT INTO qa_binding VALUES ('SYNTHETIC-V2-CONTENT')"); await legacy.CloseStore();
            }
            await ProfileBackup.WaitForProfile(v2, CancellationToken.None); await ProfileKeyMigration.Enable(v2, v2Password, CancellationToken.None);
            Check(ProfileKeys.IsStrong(v2) && ProfileKeys.Resolve(v2, v2Password) != Convert.ToHexString(v2Secret), "PCKEY002 upgrade rotates the database key and binds its new envelope to Windows user");
            using (var upgraded = new CoreClient())
            {
                await upgraded.Init(Path.Combine(v2, "chat"), v2Password);
                Check((await upgraded.Result("/sql chat SELECT body FROM qa_binding"))["rows"]?[1]?.ToString() == "SYNTHETIC-V2-CONTENT", "PCKEY002-to-PCKEY003 migration preserves encrypted contents"); await upgraded.CloseStore();
            }
            byte[] stored = File.ReadAllBytes(Path.Combine(v2, ProfileKeys.FileName));
            Check(stored.AsSpan(0, 8).SequenceEqual("PCKEY003"u8) && UserKeyProtection.Unprotect(stored.AsSpan(8)).AsSpan(0, 8).SequenceEqual("PCKEY002"u8), "New envelope combines Windows user protection with authenticated scrypt/AES wrapping");
            File.WriteAllBytes(Path.Combine(v2, ProfileKeys.FileName), stored.Concat(new byte[ProfileKeys.MaximumEnvelopeBytes]).ToArray());
            Check(await Fails(() => Task.Run(() => ProfileKeys.Resolve(v2, v2Password))), "Oversized user-bound key file is rejected before DPAPI or password derivation");
            File.WriteAllBytes(Path.Combine(v2, ProfileKeys.FileName), stored);
            CryptographicOperations.ZeroMemory(synthetic); CryptographicOperations.ZeroMemory(v2Secret); CryptographicOperations.ZeroMemory(v2Wrap);
            // Interrupt after commit by making this transaction's previous key read-only.
            string interrupted = Path.Combine(root, "interrupted"); Directory.CreateDirectory(interrupted);
            string tx = Path.Combine(interrupted, ".restore-transaction"), prev = Path.Combine(tx, "previous"); Directory.CreateDirectory(prev);
            File.WriteAllText(Path.Combine(interrupted, "chat_chat.db"), "COMMITTED-CURRENT-DATA");
            string prevKey = Path.Combine(prev, ProfileKeys.FileName); File.WriteAllBytes(prevKey, RandomNumberGenerator.GetBytes(100));
            File.WriteAllText(Path.Combine(prev, "chat_chat.db"), "OLD-DATA");
            File.WriteAllText(Path.Combine(tx, "pending.json"), JsonSerializer.Serialize(new { Originals = new[] { ProfileKeys.FileName, "chat_chat.db" }, RecoveryName = "before-restore-20261005-000000-" + new string('a', 32), DiscardPrevious = true }));
            File.WriteAllText(Path.Combine(tx, "committed"), "committed"); File.SetAttributes(prevKey, FileAttributes.ReadOnly);
            Check(await Fails(() => Task.Run(() => ProfileRestore.Recover(interrupted))) && Directory.Exists(tx), "Blocked post-commit cleanup retains its durable journal");
            Check(File.ReadAllText(Path.Combine(interrupted, "chat_chat.db")) == "COMMITTED-CURRENT-DATA" && !File.Exists(Path.Combine(prev, "chat_chat.db")), "Partial cleanup never resurrects old data and still clears other reviewed files");
            File.SetAttributes(prevKey, FileAttributes.Normal); ProfileRestore.Startup(interrupted);
            Check(!Directory.Exists(tx) && !Directory.Exists(Path.Combine(interrupted, ".restore-history")) && File.ReadAllText(Path.Combine(interrupted, "chat_chat.db")) == "COMMITTED-CURRENT-DATA", "Startup finishes interrupted committed cleanup without rollback or permanent copy");
            // 148 characters reproduced the old nested-export failure (journal
            // length 260). Keep this independent of the implementation's budget.
            foreach (int length in new[] { 148, 200 })
            {
                string parent = Path.Combine(RuntimeSecurity.Base, "qa", "lp-" + Guid.NewGuid().ToString("N")[..8]);
                if (parent.Length + 2 >= length) throw new IOException("Long-path test harness root is too long");
                string longProfile = Path.Combine(parent, new string('p', length - parent.Length - 1));
                Directory.CreateDirectory(longProfile);
                string longPassword = "Synthetic long path password 2026";
                using (var old = new CoreClient())
                {
                    await old.Init(Path.Combine(longProfile, "chat"), longPassword);
                    await old.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                    await old.Result("/sql chat CREATE TABLE path_fixture (body TEXT)");
                    await old.Result("/sql chat INSERT INTO path_fixture VALUES ('SYNTHETIC-LONG-PATH')");
                    await old.CloseStore();
                }
                await ProfileBackup.WaitForProfile(longProfile, CancellationToken.None);
                if (length == 148)
                {
                    await ProfileKeyMigration.Enable(longProfile, longPassword, CancellationToken.None);
                    Check(ProfileKeys.IsStrong(longProfile), "Previously failing 148-character profile upgrades successfully");
                    using var opened = new CoreClient(); await opened.Init(Path.Combine(longProfile, "chat"), longPassword);
                    Check((await opened.Result("/sql chat SELECT body FROM path_fixture"))["rows"]?[1]?.ToString() == "SYNTHETIC-LONG-PATH", "Long-path upgrade preserves encrypted content");
                    await opened.CloseStore();
                    Check(!Directory.EnumerateDirectories(longProfile, ".backup-work-*").Any() && !Directory.Exists(Path.Combine(longProfile, ".restore-transaction")), "Long-path upgrade leaves no migration copy or pending transaction");
                }
                else
                {
                    var snapshot = Hashes(longProfile); bool pathRejected = false;
                    try { await ProfileKeyMigration.Enable(longProfile, longPassword, CancellationToken.None); }
                    catch (MigrationPathTooLongException) { pathRejected = true; }
                    Check(pathRejected && Same(snapshot, Hashes(longProfile)), "Unsupported 200-character profile rejects before changing any original bytes");
                    Check(!Directory.EnumerateDirectories(longProfile, ".backup-work-*").Any(), "Unsupported path never creates a migration staging copy");
                }
            }
            CryptographicOperations.ZeroMemory(oldSecret); CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(vector);
            Save("passed"); return 0;
        }
        catch (Exception ex) { Save("failed", ex is CoreException ce ? ce.Diagnostic : ex.ToString()); return 1; }
    }
    public static int RunUi()
    {
        var app = new Application(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "privacy-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var window = new MainWindow(root); var checks = new List<string>(); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T UI<T>(string name) => (T)window.FindName(name);
        void Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks.Add(text); }
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "privacy-ui-qa.json"), JsonSerializer.Serialize(new { status, reason, checks, screenshots = root }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            typeof(MainWindow).GetField("captureProtected", flags)!.SetValue(window, true);
            Check(window.FindName("BackupButton") == null && window.FindName("BackupPanel") == null, "Backup and restore UI are removed rather than merely disabled");
            Check(UI<TextBlock>("UnlockHint").Text.Contains("16") && UI<TextBlock>("UnlockHint").Text.Contains("不提供备份"), "New-profile UI explains stronger passphrase minimum and no recovery");
            UI<PasswordBox>("Password").Password = "123456789012"; UI<PasswordBox>("ConfirmPassword").Password = "123456789012";
            Invoke("Unlock_Click", window, new RoutedEventArgs());
            Check(UI<TextBlock>("UnlockError").Text.Contains("16") && !ProfileKeys.Exists(root) && typeof(MainWindow).GetField("core", flags)!.GetValue(window) == null, "Actual unlock handler rejects short new passwords without a worker or files");
            var create = Task.Run(async () =>
            {
                using var core = new CoreClient(); await core.Init(Path.Combine(root, "chat"), "legacy-pass!");
                await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}"); await core.CloseStore();
                await ProfileBackup.WaitForProfile(root, CancellationToken.None);
            });
            PumpUntil(() => create.IsCompleted, 30); create.GetAwaiter().GetResult();
            Invoke("SetUnlockText"); Invoke("Deletion_Click", window, new RoutedEventArgs());
            Check(UI<Button>("KeyEnableButton").IsEnabled && window.FindName("DeletionChooseButton") == null, "Legacy profiles can upgrade and the backup deletion entry is absent");
            UI<PasswordBox>("DeletionPassword").Password = "legacy-pass!";
            Invoke("KeyEnable_Click", window, new RoutedEventArgs());
            PumpUntil(() => !(bool)typeof(MainWindow).GetField("deletionBusy", flags)!.GetValue(window)!, 45);
            Check(ProfileKeys.IsStrong(root) && UI<TextBlock>("DeletionStatus").Text.Contains("已启用"), "Actual UI upgrades existing 12-character legacy password without resetting identity");
            Check(UI<PasswordBox>("DeletionPassword").Password.Length == 0 && !UI<Button>("KeyEnableButton").IsEnabled, "Upgrade clears the UI password and disables repeated migration");
            Check(typeof(MainWindow).GetField("core", flags)!.GetValue(window) == null && typeof(MainWindow).GetField("tor", flags)!.GetValue(window) == null, "Encryption upgrade completes entirely offline");
            foreach (var (width, height) in new[] { (940, 620), (2560, 1440) })
            {
                window.Width = width; window.Height = height; var surface = (FrameworkElement)window.Content;
                surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(root, "privacy-" + width + ".png")); encoder.Save(output);
                Check(((Border)UI<Grid>("DeletionPanel").Children[0]).ActualWidth <= 670.5, "Upgrade dialog remains bounded at width " + width);
            }
            Save("passed"); return 0;
        }
        catch (Exception ex) { Save("failed", ex.ToString()); return 1; }
        finally { window.Close(); app.Shutdown(); }
    }
    private static void PumpUntil(Func<bool> done, int seconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done())
        {
            if (elapsed.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Privacy UI test");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
    }
}
