using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class FileTransferTests
{
    public static async Task<int> Run(bool network)
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        string report = Path.Combine(RuntimeSecurity.Base, "file-qa.json");
        try
        {
            foreach (string name in new[] { "../escape.exe", "..\\escape.exe", "CON.txt", "test\u202Etxt.exe", "x:stream", "COM1.log" })
                Assert(FileTransfer.SafeName(name) != name && !FileTransfer.SafeName(name).Contains('\\') && !FileTransfer.SafeName(name).Contains('/'), "Untrusted filename normalized: " + name.Replace('\u202E','_'));
            foreach (string path in new[] { "\\\\server\\file", "C:\\file:stream", "\\\\?\\C:\\file", "C:\\bad\npath" })
                Assert(Reject(() => FileTransfer.LocalPath(path)), "Remote/device/ADS/control path rejected");
            foreach (string name in new[] { "CON.txt", "CON .txt", "AUX .txt", "NUL .txt", "COM1 .txt", "LPT1 .txt", "COM¹.txt", "CONIN$.txt", "CONOUT$ .txt" })
            {
                string safe = FileTransfer.SafeName(name);
                Assert(safe.StartsWith("文件_", StringComparison.Ordinal) && FileTransfer.LocalPath(Path.Combine(root, safe)) == Path.Combine(root, safe), "Device-style filename becomes an ordinary local filename: " + name);
                Assert(Reject(() => FileTransfer.LocalPath(Path.Combine(root, name))) && Reject(() => FileTransfer.CachedPath(root, name)), "Manual and cache paths reject device-style names: " + name);
                Assert(Reject(() => FileTransfer.LocalPath(Path.Combine(root, name, "child.txt"))), "Device-style directory component rejected: " + name);
            }
            foreach (string name in new[] { "CONSOLE.txt", "COM10.txt", "auxiliary.txt", "普通 文档.txt" })
                Assert(FileTransfer.SafeName(name) == name && FileTransfer.LocalPath(Path.Combine(root, name)) == Path.Combine(root, name), "Ordinary filename is unaffected: " + name);
            Assert(Reject(() => FileTransfer.CachedPath(root, "..\\escape")) && Reject(() => FileTransfer.CachedPath(root, "C:\\escape")), "Cache traversal and absolute core paths rejected");
            var oversize = JsonNode.Parse("{\"fileId\":1,\"fileProtocol\":\"xftp\",\"fileSize\":26214401,\"fileStatus\":{\"type\":\"rcvInvitation\"}}")!;
            Assert(!FileTransfer.Receivable(oversize), "Oversized file offers cannot be accepted");
            var bad = oversize.DeepClone(); bad["fileSize"] = 1L; bad["fileProtocol"] = "smp";
            Assert(!FileTransfer.Receivable(bad), "Unexpected transfer protocol cannot be accepted");
            foreach (string status in new[] { "sndError", "sndWarning", "rcvError", "rcvWarning", "invalid" })
            { bad["fileProtocol"] = "xftp"; bad["fileStatus"] = new JsonObject { ["type"] = status }; Assert(!FileTransfer.StatusLabel(bad).StartsWith("已接收") && !FileTransfer.Exportable(bad), "Non-success file status not treated as completed: " + status); }
            string profile = Path.Combine(root, "local"); Directory.CreateDirectory(profile);
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string marker = "SYNTHETIC-PRIVATE-FILE-" + Guid.NewGuid().ToString("N");
            byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(marker + " 中文文件\n", 8192)));
            string input = Path.Combine(root, "测试 原稿.txt"); File.WriteAllBytes(input, bytes);
            JsonNode encrypted;
            using (var client = new CoreClient())
            {
                Assert((await client.Init(Path.Combine(profile, "chat"), password))["type"]?.ToString() == "ok", "File test profile initialized");
                await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                await FileTransfer.Configure(client, profile);
                encrypted = await client.EncryptFile(input);
                Assert(encrypted["cryptoArgs"]?["fileKey"] != null, "Native encryption returns file-specific keys");
                string cipher = FileTransfer.CachedPath(profile, encrypted["filePath"]!.ToString());
                Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(cipher)).Contains(marker), "File plaintext absent from encrypted cache");
                Assert(Path.GetFileName(cipher) != Path.GetFileName(input), "Cache path does not retain original filename");
                string output = Path.Combine(root, "导出.txt");
                Assert((await client.ExportFile(encrypted, bytes.Length, output))["type"]?.ToString() == "ok", "Native authenticated export succeeds");
                Assert(SHA256.HashData(File.ReadAllBytes(output)).SequenceEqual(SHA256.HashData(bytes)), "Exported bytes match original SHA-256");
                Assert(File.ReadAllText(output + ":Zone.Identifier").Contains("ZoneId=3"), "Export retains Windows downloaded-file zone marker");
                var absoluteSource = encrypted.DeepClone(); absoluteSource["filePath"] = cipher;
                await client.ExportFile(absoluteSource, bytes.Length, Path.Combine(root,"absolute-source-export.txt"));
                Assert(true, "Absolute receive path accepted only inside the encrypted cache");
                Assert(await RejectAsync(() => client.ExportFile(encrypted, bytes.Length, output)), "Export cannot overwrite an existing file");
                Assert(await RejectAsync(() => client.ExportFile(encrypted, bytes.Length, Path.Combine(profile, "forbidden.txt"))), "Export cannot write into profile directory");
                Assert(await RejectAsync(() => client.EncryptFile(Path.Combine(profile, "chat_chat.db"))), "Sending a profile database is rejected");
                byte[] corrupted = File.ReadAllBytes(cipher); corrupted[^1] ^= 1; File.WriteAllBytes(cipher, corrupted);
                string rejected = Path.Combine(root, "tampered.txt");
                Assert(await RejectAsync(() => client.ExportFile(encrypted, bytes.Length, rejected)) && !File.Exists(rejected), "Tampered ciphertext is rejected before any plaintext output exists");
                string empty = Path.Combine(root, "empty.txt"); File.WriteAllBytes(empty, []);
                var emptySource = await client.EncryptFile(empty);
                string emptyOutput = Path.Combine(root, "empty-export.txt");
                await client.ExportFile(emptySource, 0, emptyOutput);
                Assert(new FileInfo(emptyOutput).Length == 0, "Empty-file encrypted round trip succeeds");
                string tooLarge = Path.Combine(root, "large.txt"); using (var sparse = File.Create(tooLarge)) sparse.SetLength(FileTransfer.MaximumBytes + 1);
                Assert(await RejectAsync(() => client.EncryptFile(tooLarge)), "Worker independently enforces 25 MB send limit");
            }
            if (network) await Network(root, password, input, bytes, marker, Assert);
            File.WriteAllText(report, new JsonObject { ["status"] = "passed", ["networkRequested"] = network, ["checks"] = new JsonArray(checks.Select(c => JsonValue.Create(c)).ToArray()), ["syntheticRoot"] = root }.ToJsonString(new() { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            string reason = ex is CoreException ce ? ce.Diagnostic : ex.Message;
            File.WriteAllText(report, new JsonObject { ["status"] = "incomplete", ["reason"] = reason, ["checks"] = new JsonArray(checks.Select(c => JsonValue.Create(c)).ToArray()), ["syntheticRoot"] = root }.ToJsonString(new() { WriteIndented = true }));
            Console.WriteLine("INCOMPLETE " + reason); return 1;
        }
    }
    private static bool Reject(Action action) { try { action(); return false; } catch (IOException) { return true; } }
    private static async Task<bool> RejectAsync(Func<Task<JsonNode>> action) { try { await action(); return false; } catch (IOException) { return true; } catch (ObjectDisposedException) { return true; } }
    private static async Task Wait(Func<Task<bool>> condition, int seconds, string reason)
    { var end = DateTime.UtcNow.AddSeconds(seconds); do { if (await condition()) return; await Task.Delay(1000); } while (DateTime.UtcNow < end); throw new Exception(reason); }
    private static async Task<long> Contact(CoreClient client) => (await client.Result("/_get chats 1 pcc=on"))["chats"]!.AsArray().Select(c => c?["chatInfo"]?["contact"]).FirstOrDefault(c => c?["activeConn"]?["connStatus"]?["type"]?.ToString() == "ready")?["contactId"]?.GetValue<long>() ?? 0;
    private static async Task<JsonNode?> FileItem(CoreClient client, long contact) => (await client.Result($"/_get chat @{contact} count=100"))["chat"]?["chatItems"]?.AsArray().LastOrDefault(c => c?["file"] != null);
    private static async Task Network(string root, string password, string input, byte[] original, string marker, Action<bool,string> assert)
    {
        using var tor = new TorService();
        tor.Changed += n => Console.WriteLine("File test Tor " + n + "%");
        tor.Start(Path.Combine(RuntimeSecurity.Base, "qa", "file-network-cache"));
        await Wait(() => Task.FromResult(tor.Ready), 180, "Tor bootstrap incomplete");
        string aliceRoot = Path.Combine(root, "alice"), bobRoot = Path.Combine(root, "bob");
        Directory.CreateDirectory(aliceRoot); Directory.CreateDirectory(bobRoot);
        using var alice = new CoreClient(); using var bob = new CoreClient();
        tor.Guard(alice); tor.Guard(bob);
        foreach (var (client, profile) in new[] { (alice,aliceRoot),(bob,bobRoot) })
        {
            await client.Init(Path.Combine(profile, "chat"), password);
            await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            var cfg = PrivacyPolicy.Configure((await client.Result("/network"))["networkConfig"]!, tor.Port); cfg["hostMode"] = "public";
            await client.Result("/_network " + cfg.ToJsonString());
            await FileTransfer.Configure(client, profile);
            await client.Result("/_start main=on snd_files=on");
            await client.Result("/_set receipts contacts 1 off clear_overrides=on");
            await client.Result("/_set accept member contacts 1 off");
            assert(PrivacyPolicy.IsStrict((await client.Result("/network"))["networkConfig"]!,tor.Port), "File endpoint retains mandatory Tor and transport isolation");
        }
        var relays = await RelayTests.NetworkChecks(alice,1,tor.Port,assert);
        await RelaySettings.Save(bob,1,relays,tor.Port,()=>true);
        var invite = await alice.Result("/_connect 1 incognito=on");
        await bob.Result("/_connect 1 incognito=on " + invite["connLinkInvitation"]!["connFullLink"]!.ToString());
        long a=0,b=0;
        await Wait(async()=>{a=await Contact(alice);b=await Contact(bob);return a>0&&b>0;},180,"File test contact connection incomplete");
        var ac=(await alice.Result($"/_get code @{a}"))["connectionCode"]!.ToString(); var bc=(await bob.Result($"/_get code @{b}"))["connectionCode"]!.ToString();
        assert(ac==bc,"File endpoints agree on connection safety code");
        await alice.Result($"/_verify code @{a} {bc}"); await bob.Result($"/_verify code @{b} {ac}");
        Console.WriteLine("File test uploading synthetic ciphertext");
        var source=await alice.EncryptFile(input);
        string sendName = FileTransfer.NewTextName();
        await alice.SendFile(a,source,sendName);
        JsonNode? received=null;
        await Wait(async()=>{received=await FileItem(bob,b);return received!=null;},180,"File invitation not received");
        await Task.Delay(3000);
        var file=await FileTransfer.FreshFile(bob,b,received!["meta"]!["itemId"]!.GetValue<long>());
        assert(FileTransfer.Status(file)=="rcvInvitation" && !Directory.EnumerateFiles(FileTransfer.Cache(bobRoot)).Any(),"Incoming file remains unaccepted without a user action");
        assert(FileTransfer.DisplayName(received)==sendName && !received.ToJsonString().Contains(Path.GetFileName(input)),"Recipient gets the confirmed anonymous name without the original filename");
        await FileTransfer.Receive(bob,bobRoot,file);
        await Wait(async()=>{received=await FileItem(bob,b);return FileTransfer.Status(received!["file"]!)=="rcvComplete";},240,"XFTP download incomplete");
        file=received!["file"]!;
        assert(FileTransfer.Exportable(file),"Received XFTP file has encrypted local source");
        string output=Path.Combine(root,FileTransfer.DisplayName(received));
        Console.WriteLine("Synthetic receive path form: " + file["fileSource"]!["filePath"]!.ToString());
        await bob.ExportFile(file["fileSource"]!,original.Length,output);
        assert(SHA256.HashData(File.ReadAllBytes(output)).SequenceEqual(SHA256.HashData(original)),"Tor XFTP transfer and authenticated export match original SHA-256");
        assert((await bob.CacheInfo())["reserved"]!.GetValue<long>()==0,"Completed real download releases reserved space");
        SecurityTests.ScanSyntheticFiles(aliceRoot,new[]{marker,password},assert); SecurityTests.ScanSyntheticFiles(bobRoot,new[]{marker,password},assert);
        // Opposite direction, with recipient offline until the file upload finishes.
        await alice.Result("/_stop");
        string reverseName = FileTransfer.NewTextName();
        var reverse=await bob.EncryptFile(input); await bob.SendFile(b,reverse,reverseName);
        await Wait(async()=>FileTransfer.Status((await FileItem(bob,b))!["file"]!)=="sndComplete",240,"Offline file upload incomplete");
        await alice.Result("/_start main=on snd_files=on");
        JsonNode? offline=null;
        await Wait(async()=>{offline=await FileItem(alice,a);return offline?["chatDir"]?["type"]?.ToString()=="directRcv";},180,"Offline file invitation missing after reconnect");
        assert(reverseName!=sendName && FileTransfer.DisplayName(offline!)==reverseName,"Resending the same source gives a different name that survives offline delivery");
        await FileTransfer.Receive(alice,aliceRoot,offline!["file"]!);
        await Wait(async()=>{offline=await FileItem(alice,a);return FileTransfer.Status(offline!["file"]!)=="rcvComplete";},240,"Offline file download incomplete");
        string reverseOutput=Path.Combine(root,"reverse-export.txt");
        await alice.ExportFile(offline!["file"]!["fileSource"]!,original.Length,reverseOutput);
        assert(SHA256.HashData(File.ReadAllBytes(reverseOutput)).SequenceEqual(SHA256.HashData(original)),"Reverse-direction file received after offline/reconnect and matches hash");
        var cancelledSource = await alice.EncryptFile(input);
        await alice.SendFile(a,cancelledSource,FileTransfer.NewTextName());
        var cancelled = (await FileItem(alice,a))!;
        await alice.Result($"/_verify code @{a}");
        assert(!PrivacyPolicy.VerifiedContact((await alice.Result($"/_get code @{a}"))["contact"]),"Synthetic contact verification cleared for cancellation regression");
        var savedNetwork=(await alice.Result("/network"))["networkConfig"]!.DeepClone();
        using var blockedProxy=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
        blockedProxy.Start(); // Accepts no SOCKS traffic and provides no network forwarding.
        var blockedNetwork=PrivacyPolicy.Configure(savedNetwork,((System.Net.IPEndPoint)blockedProxy.LocalEndpoint).Port);
        await alice.Result("/_network "+blockedNetwork.ToJsonString());
        await CancelFromUi(alice,aliceRoot,a,cancelled["meta"]!["itemId"]!.GetValue<long>());
        var cancelledState = await FileTransfer.FreshFile(alice,a,cancelled["meta"]!["itemId"]!.GetValue<long>());
        assert(FileTransfer.Status(cancelledState)=="sndCancelled" && !FileTransfer.Cancellable(cancelledState),"Real UI cancellation works with unverified, not-ready UI state and unusable SOCKS route");
        assert((await alice.CacheInfo())["reserved"]!.GetValue<long>()==0,"Cancelled real upload releases reservation");
        await alice.Result("/_network "+savedNetwork.ToJsonString());
        await alice.Result($"/_verify code @{a} {bc}");
        // A different/older client can declare arbitrary bytes as TXT. Test the recipient boundary.
        // This synthetic malicious peer intentionally bypasses TXT validation;
        // keep its fixture inside the directory granted to that peer's sandbox.
        string disguised = Path.Combine(aliceRoot,"malformed-peer.txt");
        byte[] malformed = Encoding.UTF8.GetBytes("SYNTHETIC-BINARY\0NOT-TEXT"); File.WriteAllBytes(disguised,malformed);
        await alice.Result($"/_send @{a} live=off ttl=default sign=off json " + new JsonArray(new JsonObject { ["fileSource"] = new JsonObject { ["filePath"] = disguised }, ["msgContent"] = new JsonObject { ["type"] = "file", ["text"] = "malformed-peer.txt" }, ["mentions"] = new JsonObject() }).ToJsonString());
        JsonNode? attack=null;
        await Wait(async()=>{attack=await FileItem(bob,b);return attack!=null && FileTransfer.DisplayName(attack)=="malformed-peer.txt";},180,"Synthetic peer file offer missing");
        await FileTransfer.Receive(bob,bobRoot,attack!["file"]!);
        await Wait(async()=>{attack=await FileItem(bob,b);return FileTransfer.Status(attack!["file"]!)=="rcvComplete";},240,"Synthetic peer file download incomplete");
        string forbidden=Path.Combine(root,"must-not-export.txt");
        assert(await RejectAsync(()=>bob.ExportFile(attack!["file"]!["fileSource"]!,malformed.Length,forbidden)) && !File.Exists(forbidden),"Authenticated peer attachment containing binary bytes cannot be exported as text");
        // A queued send uses no direct fallback when the SOCKS endpoint disappears.
        using(var process=System.Diagnostics.Process.GetProcessById(tor.ProcessId)) process.Kill(true);
        await Task.Delay(700);
        assert(await RejectAsync(()=>alice.Command("/u")) && await RejectAsync(()=>bob.Command("/u")),"Tor failure terminates both file-capable core workers");
    }
    private static Task CancelFromUi(CoreClient core,string root,long contact,long item)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async()=>
            {
                MainWindow? window=null;
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                try
                {
                    window=new MainWindow(root);
                    typeof(MainWindow).GetField("core",flags)!.SetValue(window,core);
                    typeof(MainWindow).GetField("selected",flags)!.SetValue(window,new ContactRow(contact,"Synthetic","Unverified/offline",false,false));
                    await (Task)typeof(MainWindow).GetMethod("FileAction",flags)!.Invoke(window,new object[]{"cancel",contact,item,"synthetic.txt"})!;
                    done.TrySetResult();
                }
                catch(Exception ex){done.TrySetException(ex);}
                finally
                {
                    if(window!=null){typeof(MainWindow).GetField("core",flags)!.SetValue(window,null);window.Close();}
                    dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
                }
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
