using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateChat;

// Tests wire interoperability only, using new disposable identities on both cores.
// It neither reads the real profile nor attempts a database downgrade.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3) return 64;
        string oldWorker = Path.GetFullPath(args[0]), candidate = Path.GetFullPath(args[1]);
        string report = Path.GetFullPath(args[2]);
        string root = Path.Combine(AppContext.BaseDirectory, "synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var versions = new List<JsonNode>();
        string status = "running";
        string? reason = null;
        void Check(bool valid, string name) { if (!valid) throw new IOException(name); checks.Add(name); }
        CoreClient? old = null, fresh = null;
        using var oldTor = new TorService();
        using var tor = new TorService();
        try
        {
            // Each executable directory has its own AppContainer identity.
            // Cross-package loopback is intentionally denied, so each core
            // must have a Tor transport launched under that core's identity.
            Environment.SetEnvironmentVariable("PRIVATECHAT_QA_WORKER", oldWorker);
            oldTor.Start(Path.Combine(root, "old-tor"));
            old = new CoreClient();
            Environment.SetEnvironmentVariable("PRIVATECHAT_QA_WORKER", candidate);
            tor.Start(Path.Combine(root, "tor"));
            fresh = new CoreClient();
            await Until(() => Task.FromResult(oldTor.Ready && tor.Ready), 300, "Separate Tor transports bootstrap");
            Check(true, "Both core-specific Tor transports connected");
            oldTor.Guard(old); tor.Guard(fresh);
            foreach (var (client, name, port) in new[] { (old, "old", oldTor.Port), (fresh, "candidate", tor.Port) })
            {
                string profile = Path.Combine(root, name); Directory.CreateDirectory(profile);
                string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                Check((await client.Init(Path.Combine(profile, "chat"), password))["type"]?.ToString() == "ok", name + " initialized new encrypted profile");
                await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                versions.Add((await client.Result("/version"))["versionInfo"]!.DeepClone());
                var cfg = PrivacyPolicy.Configure((await client.Result("/network"))["networkConfig"]!, port);
                cfg["hostMode"] = "public";
                await client.Result("/_network " + cfg.ToJsonString());
                Check(PrivacyPolicy.IsStrict((await client.Result("/network"))["networkConfig"]!, port), name + " retains mandatory Tor");
                await client.Result("/_start main=on snd_files=off");
                await client.Result("/_set receipts contacts 1 off clear_overrides=on");
                await client.Result("/_set accept member contacts 1 off");
            }
            // Probe relays before invitation creation so an unavailable random
            // preset is recorded separately from a wire-compatibility failure.
            var selected = await RelayTests.NetworkChecks(old, 1, oldTor.Port, Check);
            await RelaySettings.Save(fresh, 1, selected, tor.Port, () => true);
            Check(RelaySettings.Enabled(await RelaySettings.Read(fresh, 1)).SequenceEqual(selected.Order(StringComparer.Ordinal)), "Both core versions use the tested relays");
            string link = (await old.Result("/_connect 1 incognito=on"))["connLinkInvitation"]!["connFullLink"]!.ToString();
            await fresh.Result("/_connect 1 incognito=on " + link);
            long a = 0, b = 0;
            await Until(async () => { a = await Contact(old); b = await Contact(fresh); return a > 0 && b > 0; }, 180, "Mixed-core handshake");
            Check(true, "Stable and candidate cores establish contact over Tor");
            string code = (await old.Result($"/_get code @{a}"))["connectionCode"]!.ToString();
            Check(code == (await fresh.Result($"/_get code @{b}"))["connectionCode"]!.ToString(), "Mixed-core safety codes match");
            Check((await old.Result($"/_verify code @{a} {code}"))["verified"]!.GetValue<bool>() &&
                  (await fresh.Result($"/_verify code @{b} {code}"))["verified"]!.GetValue<bool>(), "Both cores verify the peer");
            string marker = "SYNTHETIC-MIXED-CORE-中文-" + Guid.NewGuid().ToString("N");
            await old.Result(PrivacyPolicy.Send(a, marker));
            await Until(() => Has(fresh, b, marker), 120, "Old to candidate delivery");
            Check(true, "Candidate receives stable-core message");
            await fresh.Result(PrivacyPolicy.Send(b, marker + "-reply"));
            await Until(() => Has(old, a, marker + "-reply"), 120, "Candidate to old delivery");
            Check(true, "Stable core receives candidate reply");
            await old.Result("/_stop");
            await fresh.Result(PrivacyPolicy.Send(b, marker + "-offline"));
            await Task.Delay(1500);
            await old.Result("/_start main=on snd_files=off");
            await Until(() => Has(old, a, marker + "-offline"), 120, "Mixed-core offline delivery");
            Check(true, "Stable core receives queued candidate message after reconnect");
            status = "passed";
        }
        catch (Exception ex) { status = "failed"; reason = ex is CoreException ce ? ce.Diagnostic : ex.Message; }
        finally
        {
            old?.Dispose(); fresh?.Dispose();
            File.WriteAllText(report, JsonSerializer.Serialize(new { status, reason, checks, versions, syntheticRoot = root,
                oldAssemblySha256 = Hash(Path.ChangeExtension(oldWorker, ".dll")), candidateAssemblySha256 = Hash(Path.ChangeExtension(candidate, ".dll")) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { status, reason, checks = checks.Count }));
        return status == "passed" ? 0 : 1;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task<long> Contact(CoreClient client) => (await client.Result("/_get chats 1 pcc=on"))["chats"]!.AsArray()
        .Select(c => c?["chatInfo"]?["contact"]).FirstOrDefault(c => c?["activeConn"]?["connStatus"]?["type"]?.ToString() == "ready")?["contactId"]?.GetValue<long>() ?? 0;
    private static async Task<bool> Has(CoreClient client, long contact, string text) => (await client.Result($"/_get chat @{contact} count=100"))["chat"]!["chatItems"]!.AsArray().Any(i => i?["meta"]?["itemText"]?.ToString() == text);
    private static async Task Until(Func<Task<bool>> condition, int seconds, string stage)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) { if (await condition()) return; await Task.Delay(1000); }
        throw new TimeoutException(stage);
    }
}
