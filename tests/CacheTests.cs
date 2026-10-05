using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class CacheTests
{
    private const long MB = 1024 * 1024;
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "cache-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var checks = new List<string>(); CoreClient? client = null;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        string Make(string folder, string name, long length)
        {
            string directory = Path.Combine(profile, folder); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name); using var file = File.Create(path); file.SetLength(length); return path;
        }
        async Task Sql(string sql) => await client!.Result("/sql chat " + sql);
        async Task Record(int id, string state, string? path, long bytes = 10 * MB) => await Sql($"INSERT INTO files (file_id,file_name,file_path,file_size,chunk_size,user_id,ci_file_status,protocol,updated_at) VALUES ({id},'synthetic.txt'," + (path == null ? "NULL" : "'" + path.Replace("'", "''") + "'") + $",{bytes},16384,1,'{state}','xftp',datetime('now'))");
        try
        {
            client = new CoreClient();
            Assert((await client.Init(Path.Combine(profile, "chat"), password))["type"]?.ToString() == "ok", "Synthetic encrypted cache profile opens");
            await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            await FileTransfer.Configure(client, profile);
            Assert((await client.CacheInfo())["used"]!.GetValue<long>() == 0, "Empty cache inventory succeeds through production worker");
            string input = Path.Combine(root, "original.txt"); File.WriteAllText(input, "SYNTHETIC-CACHE-TEXT");
            var originalHash = SHA256.HashData(File.ReadAllBytes(input));
            string large = Make("file-temp", "synthetic.part", 513 * MB);
            Assert((await client.CacheInfo())["used"]!.GetValue<long>() == 513 * MB, "File-temp counts toward quota");
            Assert(await Full(() => client.EncryptFile(input)), "Oversized temporary cache blocks new encryption");
            File.Delete(large);
            large = Make("file-assets", "synthetic.part", 513 * MB);
            Assert(await Full(() => client.EncryptFile(input)), "File-assets also blocks new encryption when full"); File.Delete(large);
            // Only synthetic databases are modified; no agent is started with these fixtures.
            await Record(1, "rcv_accepted", "active.bin");
            await Record(2, "rcv_invitation", null);
            var info = await client.CacheInfo();
            Assert(info["active"]!.GetValue<int>() == 1 && info["reserved"]!.GetValue<long>() == 44 * MB, "Unfinished receive reserves space; untouched invitation does not");
            large = Make("file-temp", "space.part", 448 * MB);
            Assert(await Full(() => client.ReceiveFile(2)), "Second receive refused when existing reservation consumes remaining space");
            Assert((await client.CacheInfo())["active"]!.GetValue<int>() == 1, "Rejected receive creates no additional task");
            await Stop(client); client = new CoreClient();
            Assert((await client.Init(Path.Combine(profile, "chat"), password))["type"]?.ToString() == "ok", "Encrypted profile reopens after worker shutdown");
            await FileTransfer.Configure(client, profile);
            Assert((await client.CacheInfo())["reserved"]!.GetValue<long>() == 44 * MB && await Full(() => client.ReceiveFile(2)), "Reservation and admission limit survive restart");
            File.Delete(large);
            foreach (string state in new[]{"rcv_warning temporary", "snd_stored", "snd_transfer 1 2", "future_unknown"})
            {
                await Sql($"UPDATE files SET ci_file_status='{state}' WHERE file_id=1");
                Assert((await client.CacheInfo())["reserved"]!.GetValue<long>() == 44 * MB, "Active or unknown state retains reservation: " + state);
            }
            foreach (string state in new[]{"snd_complete", "snd_cancelled", "snd_error other", "rcv_complete", "rcv_cancelled", "rcv_error other", "rcv_aborted", "rcv_invitation"})
            {
                await Sql($"UPDATE files SET ci_file_status='{state}' WHERE file_id=1");
                Assert((await client.CacheInfo())["reserved"]!.GetValue<long>() == 0, "Ended/reset state releases reservation: " + state);
            }
            await Sql("UPDATE files SET ci_file_status='rcv_accepted' WHERE file_id=1");
            string active = Make("files", "active.bin", 10);
            string shared = Make("files", "shared.bin", 20);
            string done = Make("files", "done.bin", 30);
            string temp = Make("file-temp", "orphan.part", 40);
            string asset = Make("file-assets", "orphan.part", 50);
            string orphan = Make("files", Guid.NewGuid().ToString("N") + ".bin", 60);
            string unknown = Make("files", "keep-user.txt", 70);
            await Record(3, "snd_complete", "shared.bin");
            await Record(4, "snd_transfer 1 2", "shared.bin");
            await Record(5, "rcv_complete", done);
            var plan = (await client.CacheInfo(true))["plan"]!.AsArray();
            Assert(plan.Count == 1 && plan[0]!["path"]!.ToString() == done, "Cleanup protects active, shared and unidentified working files");
            await Stop(client); client = null;
            string db = Path.Combine(profile, "chat_chat.db"); var dbHash = await HashAfterExit(db);
            var cleaned = FileCache.CleanOffline(profile, plan);
            Assert(cleaned.Count == 1 && cleaned.Bytes == 30 && !File.Exists(done), "Offline cleanup removes completed local copy");
            Assert(new[]{active,shared,temp,asset,orphan,unknown}.All(File.Exists), "Cleanup preserves all unfinished and uncertain files");
            var dbHashAfterCleanup = await HashAfterExit(db);
            Assert(dbHash.SequenceEqual(dbHashAfterCleanup), "Offline cleanup leaves encrypted chat database unchanged");
            client = new CoreClient(); await client.Init(Path.Combine(profile,"chat"),password); await FileTransfer.Configure(client,profile);
            Assert((await client.CacheInfo())["active"]!.GetValue<int>() == 2, "Remaining tasks recover after cleanup and unlock");
            await Sql("UPDATE files SET ci_file_status='snd_cancelled' WHERE file_id IN (1,4)");
            plan = (await client.CacheInfo(true))["plan"]!.AsArray();
            Assert(plan.Count == 5, "Idle cleanup includes known finished files and orphan temporary ciphertext");
            await Stop(client); client = null;
            File.AppendAllText(temp, "changed-after-plan");
            plan.Add(Entry(input)); plan.Add(Entry(db)); // forged paths must never widen scope
            cleaned = FileCache.CleanOffline(profile,plan);
            Assert(cleaned.Count == 4 && cleaned.Skipped == 3, "Changed files and forged external/database paths are skipped");
            Assert(File.Exists(temp) && File.Exists(unknown) && File.Exists(db) && originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))), "User text, profile and changed file are preserved");
            client = new CoreClient(); await client.Init(Path.Combine(profile,"chat"),password); await FileTransfer.Configure(client,profile);
            var encrypted = await client.EncryptFile(input);
            string exported = Path.Combine(root,"exported.txt"); await client.ExportFile(encrypted,new FileInfo(input).Length,exported);
            Assert(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(exported))), "Encryption/export works after cleanup and restart");
            Assert(!Directory.EnumerateFiles(profile).Any(p=>p.EndsWith(".json",StringComparison.OrdinalIgnoreCase)), "Reservations use encrypted task state without plaintext ledger");
            Save("passed"); return 0;
        }
        catch(Exception ex) { Save("failed", ex is CoreException ce ? ce.Diagnostic : ex.ToString()); return 1; }
        finally { client?.Dispose(); }
        void Save(string status,string? reason=null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"cache-qa.json"),new JsonObject{["status"]=status,["reason"]=reason,["checks"]=new JsonArray(checks.Select(c=>JsonValue.Create(c)).ToArray()),["syntheticRoot"]=root}.ToJsonString(new(){WriteIndented=true}));
    }
    private static JsonObject Entry(string path) => new(){["path"]=path,["bytes"]=new FileInfo(path).Length,["modified"]=File.GetLastWriteTimeUtc(path).Ticks};
    private static async Task<bool> Full(Func<Task<JsonNode>> action) { try { await action(); return false; } catch(CacheCapacityException) { return true; } }
    private static async Task Stop(CoreClient client)
    {
        using var worker = Process.GetProcessById(client.ProcessId); _ = worker.SafeHandle;
        client.Dispose(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task<byte[]> HashAfterExit(string path)
    {
        for(int attempt=0;;attempt++)
        {
            try { return SHA256.HashData(File.ReadAllBytes(path)); }
            catch(IOException) when(attempt<20) { await Task.Delay(100); }
        }
    }
}
