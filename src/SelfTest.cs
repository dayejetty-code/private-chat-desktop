using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class SelfTest
{
    public static async Task<int> Run(string[] args)
    {
        var root = Path.Combine(RuntimeSecurity.Base, "qa", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Pass(string name) { checks.Add(name); Console.WriteLine("PASS " + name); }
        void Assert(bool value, string name) { if (!value) throw new Exception(name); Pass(name); }
        try
        {
            RuntimeSecurity.Verify("core"); RuntimeSecurity.Verify("tor"); Pass("Runtime package checksums");
            SecurityTests.PolicyChecks(Assert);
            await SecurityTests.TorExitCheck(root, Assert);
            await SecurityTests.ParentCrashCheck(root, Assert);
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string prefix = Path.Combine(root, "alice");
            int pid;
            using (var alice = new CoreClient())
            {
                pid = alice.ProcessId;
                Assert((await alice.Init(prefix, password))["type"]?.ToString() == "ok", "Encrypted database creation through native core");
                var user = await alice.Result("/_create user {\"profile\":{\"displayName\":\"SyntheticAlice\",\"fullName\":\"\"},\"pastTimestamp\":false}");
                Assert(user["user"]?["userId"]?.GetValue<long>() == 1, "Local profile creation without starting network");
                var cfg = (await alice.Result("/network"))["networkConfig"]!;
                await alice.Result("/_network " + PrivacyPolicy.Configure(cfg, 59999).ToJsonString());
                Assert(PrivacyPolicy.IsStrict((await alice.Result("/network"))["networkConfig"]!, 59999), "Always-SOCKS and contact isolation read back from core");
                await alice.Result("/_start main=on snd_files=off");
                await alice.Result("/_set receipts contacts 1 off clear_overrides=on");
                await alice.Result("/_set accept member contacts 1 off");
                await alice.Result("/_files_encrypt on");
                Pass("Privacy defaults accepted by real core before Tor starts");
                await HistoryTests.NativeChecks(alice, Assert);
                await RelayTests.NativeChecks(alice,1,59999,Assert);
                for (int i = 0; i < 100; i++) await alice.Result("/u");
                Pass("Native allocation and IPC repeated calls");
                var payload = "Quoted \" message\n/_network {}\n中文测试";
                var cmd = PrivacyPolicy.Send(3, payload);
                var json = JsonNode.Parse(cmd[(cmd.IndexOf(" json ", StringComparison.Ordinal) + 6)..])!;
                Assert(json[0]?["msgContent"]?["text"]?.ToString() == payload, "Message text stays JSON data, including command-like content");
                Assert(!PrivacyPolicy.ValidInvite("simplex:/invitation#x\n/_stop") && !PrivacyPolicy.ValidInvite("https://evil.invalid/invitation#x"), "Invitation validation rejects injected commands and unrelated URLs");
            }
            await Task.Delay(250);
            Assert(!ProcessExists(pid), "Core process terminates on lock/disposal");
            var files = Directory.GetFiles(root, "alice*.db*");
            Assert(files.Any(f => f.EndsWith("_chat.db")), "Encrypted database exists on disk");
            Assert(files.All(f => !Encoding.UTF8.GetString(File.ReadAllBytes(f)).Contains("SyntheticAlice", StringComparison.Ordinal)), "Synthetic profile text absent from raw database and WAL files");
            Assert(files.Where(f => f.EndsWith(".db")).All(f => !File.ReadAllBytes(f).Take(16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0"))), "SQLite plaintext headers absent");
            using (var wrong = new CoreClient()) Assert((await wrong.Init(prefix, "incorrect-password-only"))["type"]?.ToString() == "errorNotADatabase", "Wrong database passphrase rejected");
            using (var reopened = new CoreClient())
            {
                Assert((await reopened.Init(prefix, password))["type"]?.ToString() == "ok", "Correct passphrase reopens database");
                Assert((await reopened.Result("/u"))["user"]?["localDisplayName"]?.ToString() == "SyntheticAlice", "Profile survives restart");
                await RelayTests.ReopenChecks(reopened,Assert);
            }
            SecurityTests.ScanSyntheticFiles(root,new[]{"SYNTHETIC-RELAY-PASSWORD"},Assert);
            using (var tor = new TorService())
            {
                tor.Start(root);
                int torPid = tor.ProcessId;
                tor.Dispose(); await Task.Delay(350);
                Assert(!ProcessExists(torPid), "Tor process terminates on lock/disposal");
            }
            if (args.Contains("--network"))
            {
                var modeIndex = Array.IndexOf(args, "--mode");
                string mode = modeIndex >= 0 ? args[modeIndex + 1] : "direct";
                await NetworkTest(root, password, mode, args.Contains("--public-relays"), Pass, Assert);
            }
            File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "qa-result.json"), new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["status"] = "passed", ["checks"] = new JsonArray(checks.Select(s => JsonValue.Create(s)).ToArray()), ["networkRequested"] = args.Contains("--network"), ["relayEntry"] = args.Contains("--public-relays") ? "public-hosts-through-Tor" : "prefer-onion-through-Tor" }.ToJsonString(new() { WriteIndented = true }));
            Console.WriteLine("Completed: " + checks.Count + " checks."); return 0;
        }
        catch (Exception ex)
        {
            string detail = ex is CoreException ce ? "core error: " + ce.Diagnostic : ex.Message;
            File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "qa-result.json"), new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["status"] = "incomplete", ["completedChecks"] = new JsonArray(checks.Select(s => JsonValue.Create(s)).ToArray()), ["reason"] = detail }.ToJsonString(new() { WriteIndented = true }));
            Console.WriteLine("INCOMPLETE " + detail); return 1;
        }
    }
    private static bool ProcessExists(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch { return false; } }
    private static async Task NetworkTest(string root, string password, string mode, bool publicRelays, Action<string> pass, Action<bool,string> assert)
    {
        using var tor = new TorService();
        int last = -1;
        tor.Changed += progress => { if (progress != last) { last = progress; Console.WriteLine("Tor bootstrap " + progress + "%"); } };
        tor.Start(Path.Combine(RuntimeSecurity.Base, "qa/network-cache"), mode);
        await WaitUntil(() => Task.FromResult(tor.Ready), 150, "Tor bootstrap did not complete; real delivery not verified");
        pass("Tor bootstrapped using " + mode);
        using var alice = new CoreClient(); using var bob = new CoreClient();
        alice.Event += value => { if (value["error"] is JsonNode error) Console.WriteLine("Alice core event: " + error.ToJsonString()); };
        bob.Event += value => { if (value["error"] is JsonNode error) Console.WriteLine("Bob core event: " + error.ToJsonString()); };
        var alicePath = Path.Combine(root, "net-alice"); var bobPath = Path.Combine(root, "net-bob");
        foreach (var (client, path) in new[] { (alice, alicePath), (bob, bobPath) })
        {
            assert((await client.Init(path, password))["type"]?.ToString() == "ok", "Network test encrypted profile initialized");
            await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            var cfg = (await client.Result("/network"))["networkConfig"]!;
            var strict = PrivacyPolicy.Configure(cfg, tor.Port);
            if (publicRelays) strict["hostMode"] = "public";
            await client.Result("/_network " + strict.ToJsonString());
            await client.Result("/_start main=on snd_files=off");
            await client.Result("/_set receipts contacts 1 off clear_overrides=on");
            await client.Result("/_set accept member contacts 1 off");
            await client.Result("/_files_encrypt on");
            assert(PrivacyPolicy.IsStrict((await client.Result("/network"))["networkConfig"]!, tor.Port), "Network endpoint applies UI privacy defaults and retains mandatory Tor");
        }
        var selectedRelays = await RelayTests.NetworkChecks(alice,1,tor.Port,assert);
        await RelaySettings.Save(bob,1,selectedRelays,tor.Port,()=>true);
        assert(RelaySettings.Enabled(await RelaySettings.Read(bob,1)).SequenceEqual(selectedRelays.Order(StringComparer.Ordinal)),"Both synthetic endpoints use the tested relay list for new connections");
        Console.WriteLine("Network stage: creating invitation");
        var invite = await alice.Result("/_connect 1 incognito=on");
        string link = invite["connLinkInvitation"]?["connFullLink"]?.ToString() ?? throw new Exception("Invitation format mismatch");
        assert(PrivacyPolicy.ValidInvite(link), "Core produces supported one-time invitation");
        Console.WriteLine("Network stage: accepting invitation");
        await bob.Result("/_connect 1 incognito=on " + link);
        long aliceContact = 0, bobContact = 0;
        await WaitUntil(async () => { aliceContact = await GetContact(alice); bobContact = await GetContact(bob); return aliceContact > 0 && bobContact > 0; }, 120, "Contact handshake not completed");
        pass("Two isolated clients connect through Tor");
        string aCode = (await alice.Result($"/_get code @{aliceContact}"))["connectionCode"]!.ToString();
        string bCode = (await bob.Result($"/_get code @{bobContact}"))["connectionCode"]!.ToString();
        assert(aCode == bCode, "Both endpoints agree on safety code");
        assert((await alice.Result($"/_verify code @{aliceContact} {bCode}"))["verified"]?.GetValue<bool>() == true, "Safety code verification succeeds");
        var fresh = await alice.Result($"/_get code @{aliceContact}");
        assert(PrivacyPolicy.VerifiedContact(fresh["contact"]), "Freshly recomputed safety code retains verified contact before send");
        string badCode = new string(bCode.Where(char.IsAsciiDigit).Select(c => c == '9' ? '0' : (char)(c + 1)).ToArray());
        assert((await bob.Result($"/_verify code @{bobContact} {badCode}"))["verified"]?.GetValue<bool>() == false, "Mismatched peer safety code rejected by native core");
        assert(!PrivacyPolicy.VerifiedContact((await bob.Result($"/_get code @{bobContact}"))["contact"]), "Failed verification leaves peer unverified");
        assert((await bob.Result($"/_verify code @{bobContact} {aCode}"))["verified"]?.GetValue<bool>() == true, "Peer verifies correct safety code before reply");
        string payload = "PrivateChat synthetic test " + Guid.NewGuid().ToString("N") + " 中文\nsecond line";
        await alice.Result(PrivacyPolicy.Send(aliceContact, payload));
        await WaitUntil(async () => (await bob.Result($"/_get chat @{bobContact} count=100"))["chat"]?["chatItems"]?.AsArray().Any(i => i?["meta"]?["itemText"]?.ToString() == payload) == true, 100, "Encrypted message delivery not confirmed");
        pass("Real end-to-end encrypted Unicode/multiline message received");
        await bob.Result(PrivacyPolicy.Send(bobContact, "Acknowledged"));
        await WaitUntil(async () => (await alice.Result($"/_get chat @{aliceContact} count=100"))["chat"]?["chatItems"]?.AsArray().Any(i => i?["meta"]?["itemText"]?.ToString() == "Acknowledged") == true, 100, "Return message not confirmed");
        pass("Bidirectional delivery confirmed");
        var beforeRestore = (await alice.Result($"/_get code @{aliceContact}"))["contact"]?["activeConn"]?["connId"]?.GetValue<long>();
        await RelaySettings.Save(alice,1,null,tor.Port,()=>true);
        var afterRestore = (await alice.Result($"/_get code @{aliceContact}"))["contact"]?["activeConn"]?["connId"]?.GetValue<long>();
        assert(beforeRestore != null && beforeRestore == afterRestore,"Restoring relay presets keeps the existing contact connection intact");
        await bob.Result("/_stop");
        string offline = "Synthetic offline message " + Guid.NewGuid().ToString("N");
        await alice.Result(PrivacyPolicy.Send(aliceContact, offline));
        await Task.Delay(1500);
        await bob.Result("/_start main=on snd_files=off");
        await WaitUntil(async () => (await bob.Result($"/_get chat @{bobContact} count=100"))["chat"]?["chatItems"]?.AsArray().Any(i => i?["meta"]?["itemText"]?.ToString() == offline) == true, 100, "Offline delivery not confirmed");
        pass("Offline message received after recipient reconnects");
        SecurityTests.ScanSyntheticFiles(root,new[]{password,payload,offline},assert);
        tor.Dispose();
        assert(!tor.Ready, "Tor loss immediately disables network-ready state");
        assert(PrivacyPolicy.IsStrict((await alice.Result("/network"))["networkConfig"]!, tor.Port), "Core retains mandatory SOCKS after Tor loss");
        await alice.Result("/_stop"); await bob.Result("/_stop");
        alice.Dispose(); bob.Dispose();
        SecurityTests.ScanSyntheticFiles(root,new[]{password,payload,offline},assert);
    }
    private static async Task<long> GetContact(CoreClient core)
    {
        var chats = (await core.Result("/_get chats 1 pcc=on"))["chats"]!.AsArray();
        var contact = chats.Select(c => c?["chatInfo"]?["contact"]).FirstOrDefault(c => c?["activeConn"]?["connStatus"]?["type"]?.ToString() == "ready");
        return contact?["contactId"]?.GetValue<long>() ?? 0;
    }
    private static async Task WaitUntil(Func<Task<bool>> test, int seconds, string reason)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (await test()) return; await Task.Delay(1000); }
        throw new TimeoutException(reason);
    }
}
