using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrivateChat;

internal static class KeyTests
{
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string text) { if (!value) throw new Exception(text); checks.Add(text); Save("running"); }
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        Dictionary<string,string> Hashes(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(path,p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        bool Same(Dictionary<string,string> a, Dictionary<string,string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string? value) && p.Value == value);
        try
        {
            string password = "合成测试 with spaces ' " + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
            string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using (var core = new CoreClient())
            {
                Check((await core.Init(Path.Combine(root, "chat"), password))["type"]?.ToString() == "ok", "Native legacy profile accepts Unicode and spaces");
                await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                await core.Result("/sql chat CREATE TABLE qa_key_data (body TEXT)");
                await core.Result("/sql chat INSERT INTO qa_key_data VALUES ('SYNTHETIC-KEY-DATA')");
                foreach (string db in new[] { "chat", "agent" })
                {
                    string path = Path.Combine(root, "export_" + db + ".db");
                    await core.Result("/sql " + db + " ATTACH DATABASE '" + path.Replace("'", "''") + "' AS pc_key_export KEY '" + key + "'");
                    await core.Result("/sql " + db + " SELECT sqlcipher_export('pc_key_export')");
                    // Native /sql is transaction-wrapped; DETACH is forbidden while
                    // its write transaction is active. CloseStore closes attachments.
                }
                await core.CloseStore();
            }
            using (var reopened = new CoreClient())
            {
                Check((await reopened.Init(Path.Combine(root, "export"), key))["type"]?.ToString() == "ok", "SQLCipher staged export opens under independent random key");
                Check((await reopened.Result("/sql chat SELECT body FROM qa_key_data"))["rows"]?[1]?.ToString() == "SYNTHETIC-KEY-DATA", "Staged export preserves synthetic contents");
                await reopened.CloseStore();
            }
            string legacy = Path.Combine(root, "legacy"), protectedRoot = Path.Combine(root, "protected");
            Directory.CreateDirectory(legacy); Directory.CreateDirectory(protectedRoot);
            using (var legacyCore = new CoreClient())
            {
                await legacyCore.Init(Path.Combine(legacy, "chat"), password);
                await legacyCore.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                await legacyCore.Result("/sql chat CREATE TABLE qa_key_data (body TEXT)");
                await legacyCore.Result("/sql chat INSERT INTO qa_key_data VALUES ('SYNTHETIC-MIGRATION-DATA')");
                await legacyCore.CloseStore();
            }
            await ProfileBackup.WaitForProfile(legacy, CancellationToken.None);
            var original = Hashes(legacy);
            string oldArchive = Path.Combine(root, "legacy-v1.pcbackup");
            BackupArchive.Write(legacy, legacy, oldArchive, password, CancellationToken.None);
            Check(await Fails(() => ProfileKeyMigration.Enable(legacy, password + "wrong", CancellationToken.None)) && Same(original, Hashes(legacy)), "Wrong password migration preserves every legacy file");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check(await Fails(() => ProfileKeyMigration.Enable(legacy, password, cancelled.Token)) && Same(original, Hashes(legacy)), "Cancelled migration does not modify legacy profile");
            }
            await ProfileKeyMigration.Enable(legacy, password, CancellationToken.None);
            Check(ProfileKeys.Exists(legacy), "Existing Unicode-passphrase profile migrates to a random database key");
            string legacyKey = ProfileKeys.Resolve(legacy, password);
            Check(legacyKey.Length == 64 && legacyKey != password, "Database key is independent of the user password");
            using (var migrated = new CoreClient())
            {
                Check((await migrated.Init(Path.Combine(legacy, "chat"), password))["type"]?.ToString() == "ok", "Migrated profile reopens using its wrapped key");
                Check((await migrated.Result("/sql chat SELECT body FROM qa_key_data"))["rows"]?[1]?.ToString() == "SYNTHETIC-MIGRATION-DATA", "Migration retains existing encrypted message content");
                await migrated.CloseStore();
            }
            using (var raw = new CoreClient())
                Check((await raw.InitRaw(Path.Combine(legacy, "chat"), password))["type"]?.ToString() != "ok", "Old password alone cannot open migrated database ciphertext");
            await ProfileBackup.WaitForProfile(legacy, CancellationToken.None);
            string oldRestored = Path.Combine(root, "legacy-restored"); Directory.CreateDirectory(oldRestored);
            await ProfileBackup.Restore(oldRestored, oldArchive, password, CancellationToken.None);
            Check(ProfileKeys.Exists(oldRestored), "Version 1 backups restore into the new independent-key format");
            using (var oldCopy = new CoreClient())
            {
                await oldCopy.Init(Path.Combine(oldRestored, "chat"), password);
                Check((await oldCopy.Result("/sql chat SELECT body FROM qa_key_data"))["rows"]?[1]?.ToString() == "SYNTHETIC-MIGRATION-DATA", "Legacy backup retains its original messages after key migration");
                await oldCopy.CloseStore();
            }
            ProfileKeys.CreateNew(protectedRoot, password);
            string activeKey = ProfileKeys.Resolve(protectedRoot, password);
            Check(activeKey != legacyKey, "Same user password yields independent keys for different profiles");
            byte[] envelope = File.ReadAllBytes(Path.Combine(protectedRoot, ProfileKeys.FileName));
            Check(envelope.Length > ProfileKeys.EnvelopeBytes && envelope.Length <= ProfileKeys.MaximumEnvelopeBytes && !System.Text.Encoding.UTF8.GetString(envelope).Contains(activeKey), "User-bound local key envelope contains no plaintext database key");
            Check(await Fails(() => Task.Run(() => ProfileKeys.Resolve(protectedRoot, password + "wrong"))), "Wrong password fails key authentication");
            foreach (int offset in new[] { 0, 8, 40, 52, 84, 99 })
            {
                byte[] bad = (byte[])envelope.Clone(); bad[offset] ^= 1; File.WriteAllBytes(Path.Combine(protectedRoot, ProfileKeys.FileName), bad);
                Check(await Fails(() => Task.Run(() => ProfileKeys.Resolve(protectedRoot, password))), "Key envelope alteration rejected at byte " + offset);
            }
            File.WriteAllBytes(Path.Combine(protectedRoot, ProfileKeys.FileName), envelope);
            using (var core = new CoreClient())
            {
                await core.Init(Path.Combine(protectedRoot, "chat"), password);
                await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                await core.Result("/sql chat CREATE TABLE qa_key_data (body TEXT)");
                await core.Result("/sql chat INSERT INTO qa_key_data VALUES ('SYNTHETIC-PROTECTED-DATA')");
                await FileTransfer.Configure(core, protectedRoot);
                await core.CloseStore();
            }
            await ProfileBackup.WaitForProfile(protectedRoot, CancellationToken.None);
            var protectedBefore = Hashes(protectedRoot);
            string archive = Path.Combine(root, "kept.pcbackup"), secondArchive = Path.Combine(root, "second.pcbackup");
            await ProfileBackup.Create(protectedRoot, archive, password, CancellationToken.None);
            await ProfileBackup.Create(protectedRoot, secondArchive, password, CancellationToken.None);
            Check(Same(protectedBefore, Hashes(protectedRoot)), "Independent backup creation preserves active ciphertext and key envelope");
            Check(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(archive),0,8) == "PCBACK02", "Random-key backups use explicit version 2 format");
            string unpack = Path.Combine(root, "unpack"), unpack2 = Path.Combine(root, "unpack2");
            BackupArchive.Extract(archive, unpack, password, CancellationToken.None);
            BackupArchive.Extract(secondArchive, unpack2, password, CancellationToken.None);
            string archiveKey = ProfileKeys.Resolve(unpack, password), archiveKey2 = ProfileKeys.Resolve(unpack2, password);
            Check(activeKey != archiveKey && archiveKey != archiveKey2, "Each saved backup has a new database key, distinct from the active profile");
            string restored = Path.Combine(root, "restored"); Directory.CreateDirectory(restored);
            await ProfileBackup.Restore(restored, archive, password, CancellationToken.None);
            string restoredKey = ProfileKeys.Resolve(restored, password);
            Check(restoredKey != archiveKey && restoredKey != activeKey, "Restore rotates to a fresh local key without reusing the archive key");
            using (var core = new CoreClient())
            {
                await core.Init(Path.Combine(restored, "chat"), password);
                Check((await core.Result("/sql chat SELECT body FROM qa_key_data"))["rows"]?[1]?.ToString() == "SYNTHETIC-PROTECTED-DATA", "Independent-key restore preserves message contents");
                await core.CloseStore();
            }
            await ProfileBackup.WaitForProfile(restored, CancellationToken.None);
            // Restore over an existing protected profile to create a genuine internal
            // recovery copy with an older random key; both must be destroyed first.
            await ProfileBackup.Restore(protectedRoot, archive, password, CancellationToken.None);
            string exportedTxt = Path.Combine(protectedRoot, "exported.txt"); File.WriteAllText(exportedTxt, "USER-EXPORT-KEEP");
            byte[] keptArchive = SHA256.HashData(File.ReadAllBytes(archive));
            var plan = await ProfileDeletion.PrepareProfile(protectedRoot, password, CancellationToken.None);
            Check(plan.Keys == 2, "Destruction reviews both active and internal rollback key envelopes");
            string recovered = Path.Combine(root, "ciphertext-only"); Directory.CreateDirectory(recovered);
            foreach (string db in new[] { "chat_chat.db", "chat_agent.db" }) File.Copy(Path.Combine(protectedRoot, db), Path.Combine(recovered, db));
            string blocked = Path.Combine(protectedRoot, "chat_chat.db"); File.SetAttributes(blocked, FileAttributes.ReadOnly);
            Check(await Fails(() => Task.Run(() => ProfileDeletion.Execute(plan, plan.Phrase, true, CancellationToken.None))), "Blocked ciphertext cleanup reports incomplete destruction");
            Check(ProfileDeletion.Pending(protectedRoot) && File.Exists(blocked) && !plan.Entries.Where(plan.IsKey).Any(e => File.Exists(e.Path)), "All internal key envelopes are destroyed before remaining ciphertext cleanup");
            using (var noKey = new CoreClient())
                Check((await noKey.Init(Path.Combine(recovered, "chat"), password))["type"]?.ToString() != "ok", "Recovered database ciphertext plus original password cannot replace the missing random key");
            Check(!ProfileKeys.Exists(recovered), "Failed unlock never silently recreates a missing key envelope");
            File.SetAttributes(blocked, FileAttributes.Normal);
            var remaining = await ProfileDeletion.PrepareProfile(protectedRoot, "", CancellationToken.None);
            ProfileDeletion.Execute(remaining, remaining.Phrase, true, CancellationToken.None);
            Check(!ProfileDeletion.Pending(protectedRoot) && !ProfileKeys.Exists(protectedRoot) && !File.Exists(blocked), "Reviewed cleanup completes after earlier key destruction");
            Check(SHA256.HashData(File.ReadAllBytes(archive)).SequenceEqual(keptArchive) && File.ReadAllText(exportedTxt) == "USER-EXPORT-KEEP", "Identity destruction preserves manual archive and exported text");
            await ProfileBackup.Restore(protectedRoot, archive, password, CancellationToken.None);
            using (var kept = new CoreClient())
            {
                Check((await kept.Init(Path.Combine(protectedRoot, "chat"), password))["type"]?.ToString() == "ok", "User-retained backup still restores identity after local key destruction");
                Check((await kept.Result("/sql chat SELECT body FROM qa_key_data"))["rows"]?[1]?.ToString() == "SYNTHETIC-PROTECTED-DATA", "Restored retained backup still contains its original messages");
                await kept.CloseStore();
            }
            Check(!Directory.GetDirectories(protectedRoot, ".backup-work-*").Any(), "Backup and migration staging copies are removed after completion");
            TestRecovery(root, Check);
            Save("passed"); return 0;
        }
        catch (Exception ex) { Save("failed", ex is CoreException ce ? ce.Diagnostic : ex.ToString()); return 1; }
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "key-qa.json"), JsonSerializer.Serialize(new { status, reason, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void TestRecovery(string root, Action<bool,string> check)
    {
        foreach (int step in Enumerable.Range(0, 6))
        {
            string profile = Path.Combine(root, "key-crash-" + step); Directory.CreateDirectory(profile);
            string tx = Path.Combine(profile, ".restore-transaction"), previous = Path.Combine(tx, "previous"); Directory.CreateDirectory(previous);
            string[] names = ["chat_chat.db", "chat_agent.db", ProfileKeys.FileName];
            foreach (string name in names) File.WriteAllText(Path.Combine(profile, name), "original-" + name);
            File.WriteAllText(Path.Combine(tx, "pending.json"), JsonSerializer.Serialize(new { Originals = names, RecoveryName = "before-restore-20261005-120000-" + Guid.NewGuid().ToString("N") }));
            if (step >= 1) foreach (string name in names) File.Move(Path.Combine(profile, name), Path.Combine(previous, name));
            if (step >= 2) foreach (string name in names.Take(2)) File.WriteAllText(Path.Combine(profile, name), "new-" + name);
            if (step >= 3) File.WriteAllText(Path.Combine(profile, ProfileKeys.FileName), "new-" + ProfileKeys.FileName);
            if (step == 4) File.WriteAllText(Path.Combine(tx, "committed"), "committed");
            if (step == 5) File.WriteAllText(Path.Combine(profile, "chat_chat.db"), "original-chat_chat.db");
            ProfileRestore.Startup(profile); ProfileRestore.Startup(profile);
            string prefix = step == 4 ? "new-" : "original-";
            check(names.All(name => File.ReadAllText(Path.Combine(profile, name)) == prefix + name), "Recovery keeps database pair and key envelope consistent at synthetic transaction step " + step);
        }
    }
}
