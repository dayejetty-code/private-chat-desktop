using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PrivateChat;

// Two synthetic identities only. Tor is stopped during archive operations; the
// restored identity is then tested with real relay traffic through mandatory Tor.
internal static class BackupNetworkTests
{
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "backup-network-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string alicePath = Path.Combine(root, "alice"), bobPath = Path.Combine(root, "bob"), restoredPath = Path.Combine(root, "restored");
        Directory.CreateDirectory(alicePath); Directory.CreateDirectory(bobPath); Directory.CreateDirectory(restoredPath);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string torCache = Path.Combine(RuntimeSecurity.Base, "qa", "backup-tor-cache");
        var checks = new List<string>(); CoreClient? alice = null, bob = null; TorService? tor = null;
        void Pass(string text) { checks.Add(text); Save("running"); }
        void Assert(bool value, string text) { if (!value) throw new Exception(text); Pass(text); }
        try
        {
            Save("running", "Tor bootstrap pending");
            tor = new TorService(); tor.Changed += progress => Console.WriteLine("Tor " + progress + "%");
            tor.Start(torCache); await Until(() => Task.FromResult(tor.Ready), 300, "Tor bootstrap"); Pass("Tor connected for synthetic pre-backup conversation");
            alice = await Endpoint(alicePath, true); bob = await Endpoint(bobPath, true);
            string link = (await alice.Result("/_connect 1 incognito=on"))["connLinkInvitation"]!["connFullLink"]!.ToString(); await bob.Result("/_connect 1 incognito=on " + link);
            long ac = 0, bc = 0;
            await Until(async () => { ac = await Contact(alice); bc = await Contact(bob); return ac > 0 && bc > 0; }, 180, "Contact handshake"); Pass("Two independent identities established a real Tor relay connection");
            string code = (await alice.Result($"/_get code @{ac}"))["connectionCode"]!.ToString();
            Assert(code == (await bob.Result($"/_get code @{bc}"))["connectionCode"]!.ToString(), "Initial safety codes agree");
            Assert((await alice.Result($"/_verify code @{ac} {code}"))["verified"]!.GetValue<bool>() && (await bob.Result($"/_verify code @{bc} {code}"))["verified"]!.GetValue<bool>(), "Both synthetic contacts verified before backup");
            string before = "SYNTHETIC-PRIOR-TO-BACKUP-" + Guid.NewGuid().ToString("N");
            await alice.Result(PrivacyPolicy.Send(ac, before)); await Until(() => Has(bob, bc, before), 120, "Pre-backup message"); Pass("Real encrypted conversation exists before backup");
            await alice.Result("/_stop"); await Stop(alice); alice = null; await bob.Result("/_stop"); await Stop(bob); bob = null;
            tor.Dispose(); tor = null;
            string archive = Path.Combine(root, "conversation.pcbackup");
            await ProfileBackup.Create(alicePath, archive, password, CancellationToken.None);
            await ProfileBackup.Restore(restoredPath, archive, password, CancellationToken.None);
            Pass("Backup and cross-directory restore succeed with both chat cores and Tor stopped");
            tor = new TorService(); tor.Start(torCache); await Until(() => Task.FromResult(tor.Ready), 300, "Tor reconnect");
            // Original Alice stays stopped permanently: never clone a live ratchet.
            alice = await Endpoint(restoredPath, false); bob = await Endpoint(bobPath, false);
            await Until(async () => await Contact(alice) == ac && await Contact(bob) == bc, 120, "Restored contacts ready");
            var restoredCode = await alice.Result($"/_get code @{ac}");
            Assert(code == restoredCode["connectionCode"]!.ToString() && PrivacyPolicy.VerifiedContact(restoredCode["contact"]), "Contact safety code and verified state survive restore");
            Assert(await Has(alice, ac, before), "Original chat history appears through the normal chat API after restore");
            string after = "SYNTHETIC-FROM-RESTORED-" + Guid.NewGuid().ToString("N");
            await alice.Result(PrivacyPolicy.Send(ac, after)); await Until(() => Has(bob, bc, after), 120, "Restored outbound message"); Pass("Restored identity sends a real encrypted message to its existing peer");
            string reply = "SYNTHETIC-TO-RESTORED-" + Guid.NewGuid().ToString("N");
            await bob.Result(PrivacyPolicy.Send(bc, reply)); await Until(() => Has(alice, ac, reply), 120, "Restored inbound message"); Pass("Restored identity receives a real encrypted reply from its existing peer");
            Assert(PrivacyPolicy.IsStrict((await alice.Result("/network"))["networkConfig"]!, tor.Port), "Restored endpoint retains mandatory Tor configuration");
            Save("passed"); return 0;
        }
        catch (Exception ex) { Save("failed", ex is CoreException ce ? ce.Diagnostic : ex.ToString()); return 1; }
        finally { alice?.Dispose(); bob?.Dispose(); tor?.Dispose(); }

        async Task<CoreClient> Endpoint(string path, bool create)
        {
            var client = new CoreClient();
            try
            {
                if ((await client.Init(Path.Combine(path, "chat"), password))["type"]?.ToString() != "ok") throw new IOException("Cannot open profile");
                if (create) await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                var cfg = PrivacyPolicy.Configure((await client.Result("/network"))["networkConfig"]!, tor!.Port); cfg["hostMode"] = "public";
                await client.Result("/_network " + cfg.ToJsonString()); await FileTransfer.Configure(client, path);
                await client.Result("/_start main=on snd_files=off"); await client.Result("/_set receipts contacts 1 off clear_overrides=on"); await client.Result("/_set accept member contacts 1 off");
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "backup-network-qa.json"), new JsonObject { ["status"] = status, ["reason"] = reason, ["syntheticRoot"] = root, ["checks"] = new JsonArray(checks.Select(c => JsonValue.Create(c)).ToArray()) }.ToJsonString(new() { WriteIndented = true }));
    }
    private static async Task Stop(CoreClient core)
    { using var process = Process.GetProcessById(core.ProcessId); _ = process.SafeHandle; core.Dispose(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    private static async Task<long> Contact(CoreClient core) => (await core.Result("/_get chats 1 pcc=on"))["chats"]!.AsArray().Select(c => c?["chatInfo"]?["contact"]).FirstOrDefault(c => c?["activeConn"]?["connStatus"]?["type"]?.ToString() == "ready")?["contactId"]?.GetValue<long>() ?? 0;
    private static async Task<bool> Has(CoreClient core, long contact, string text) => (await core.Result($"/_get chat @{contact} count=100"))["chat"]!["chatItems"]!.AsArray().Any(i => i?["meta"]?["itemText"]?.ToString() == text);
    private static async Task Until(Func<Task<bool>> condition, int seconds, string reason)
    {
        var timeout = Stopwatch.StartNew(); while (timeout.Elapsed.TotalSeconds < seconds) { if (await condition()) return; await Task.Delay(1000); }
        throw new TimeoutException(reason);
    }
}
