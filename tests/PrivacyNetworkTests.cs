using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PrivateChat;

// Two synthetic identities only. Tor is stopped during key migration; the
// upgraded identity is then tested with real relay traffic through mandatory Tor.
internal static class PrivacyNetworkTests
{
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "privacy-network-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string alicePath = Path.Combine(root, "alice"), bobPath = Path.Combine(root, "bob"), restoredPath = alicePath;
        Directory.CreateDirectory(alicePath); Directory.CreateDirectory(bobPath); Directory.CreateDirectory(restoredPath);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string torCache = Path.Combine(RuntimeSecurity.Base, "qa", "privacy-tor-cache");
        var checks = new List<string>(); CoreClient? alice = null, bob = null; TorService? tor = null;
        void Pass(string text) { checks.Add(text); Save("running"); }
        void Assert(bool value, string text) { if (!value) throw new Exception(text); Pass(text); }
        try
        {
            Save("running", "Tor bootstrap pending");
            tor = new TorService(); tor.Changed += progress => Console.WriteLine("Tor " + progress + "%");
            tor.Start(torCache); await Until(() => Task.FromResult(tor.Ready), 300, "Tor bootstrap"); Pass("Tor connected for synthetic pre-upgrade conversation");
            alice = await Endpoint(alicePath, true); bob = await Endpoint(bobPath, true);
            string link = (await alice.Result("/_connect 1 incognito=on"))["connLinkInvitation"]!["connFullLink"]!.ToString(); await bob.Result("/_connect 1 incognito=on " + link);
            long ac = 0, bc = 0;
            await Until(async () => { ac = await Contact(alice); bc = await Contact(bob); return ac > 0 && bc > 0; }, 180, "Contact handshake"); Pass("Two independent identities established a real Tor relay connection");
            string code = (await alice.Result($"/_get code @{ac}"))["connectionCode"]!.ToString();
            Assert(code == (await bob.Result($"/_get code @{bc}"))["connectionCode"]!.ToString(), "Initial safety codes agree");
            Assert((await alice.Result($"/_verify code @{ac} {code}"))["verified"]!.GetValue<bool>() && (await bob.Result($"/_verify code @{bc} {code}"))["verified"]!.GetValue<bool>(), "Both synthetic contacts verified before upgrade");
            string before = "SYNTHETIC-PRIOR-TO-UPGRADE-" + Guid.NewGuid().ToString("N");
            await alice.Result(PrivacyPolicy.Send(ac, before)); await Until(() => Has(bob, bc, before), 120, "Pre-upgrade message"); Pass("Real encrypted conversation exists before upgrade");
            await alice.Result("/_stop"); await Stop(alice); alice = null; await bob.Result("/_stop"); await Stop(bob); bob = null;
            tor.Dispose(); tor = null;
            await ProfileKeyMigration.Enable(alicePath, password, CancellationToken.None);
            Assert(ProfileKeys.IsStrong(alicePath), "Existing identity upgrades to scrypt-protected random key with both endpoints and Tor stopped");
            tor = new TorService(); tor.Start(torCache); await Until(() => Task.FromResult(tor.Ready), 300, "Tor reconnect");
            // Restart the same identity in place after an offline key upgrade.
            alice = await Endpoint(restoredPath, false); bob = await Endpoint(bobPath, false);
            await Until(async () => await Contact(alice) == ac && await Contact(bob) == bc, 120, "Upgraded contacts ready");
            var restoredCode = await alice.Result($"/_get code @{ac}");
            Assert(code == restoredCode["connectionCode"]!.ToString() && PrivacyPolicy.VerifiedContact(restoredCode["contact"]), "Contact safety code and verified state survive upgrade");
            Assert(await Has(alice, ac, before), "Original chat history appears through the normal chat API after upgrade");
            string after = "SYNTHETIC-FROM-UPGRADED-" + Guid.NewGuid().ToString("N");
            await alice.Result(PrivacyPolicy.Send(ac, after)); await Until(() => Has(bob, bc, after), 120, "Upgraded outbound message"); Pass("Upgraded identity sends a real encrypted message to its existing peer");
            string reply = "SYNTHETIC-TO-UPGRADED-" + Guid.NewGuid().ToString("N");
            await bob.Result(PrivacyPolicy.Send(bc, reply)); await Until(() => Has(alice, ac, reply), 120, "Upgraded inbound message"); Pass("Upgraded identity receives a real encrypted reply from its existing peer");
            Assert(PrivacyPolicy.IsStrict((await alice.Result("/network"))["networkConfig"]!, tor.Port), "Upgraded endpoint retains mandatory Tor configuration");
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
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "privacy-network-qa.json"), new JsonObject { ["status"] = status, ["reason"] = reason, ["syntheticRoot"] = root, ["checks"] = new JsonArray(checks.Select(c => JsonValue.Create(c)).ToArray()) }.ToJsonString(new() { WriteIndented = true }));
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
