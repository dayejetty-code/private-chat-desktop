using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class Program
{
    private static async Task<JsonObject> Probe(int tcp4, int tcp6, int udp4, int udp6, int tor, int transportPid, string deniedFile)
    {
        var result = new JsonObject();
        async Task Tcp(string label, IPAddress address, int port, bool socks = false)
        {
            using var client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(2));
                if (socks)
                {
                    var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 });
                    byte[] reply = new byte[2]; await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    result[label] = reply.SequenceEqual(new byte[] { 5, 0 }) ? "socks5" : "invalid-socks";
                }
                else result[label] = "connected";
            }
            catch (SocketException ex) { result[label] = ex.SocketErrorCode.ToString(); }
            catch (TimeoutException) { result[label] = "timeout"; }
        }
        await Tcp("foreign-loopback-v4", IPAddress.Loopback, tcp4);
        await Tcp("foreign-loopback-v6", IPAddress.IPv6Loopback, tcp6);
        await Tcp("internet-v4", IPAddress.Parse("1.1.1.1"), 443);
        await Tcp("internet-v6", IPAddress.Parse("2606:4700:4700::1111"), 443);
        if (tor != 0) await Tcp("own-tor-socks", IPAddress.Loopback, tor, true);
        foreach (var pair in new[] { (IPAddress.Loopback, udp4, "udp-v4"), (IPAddress.IPv6Loopback, udp6, "udp-v6") })
        {
            using var udp = new UdpClient(pair.Item1.AddressFamily);
            try { await udp.SendAsync(new byte[] { 71, 82, 68 }, new IPEndPoint(pair.Item1, pair.Item2)); result[pair.Item3] = "submitted-not-proof-of-delivery"; }
            catch (SocketException ex) { result[pair.Item3] = ex.SocketErrorCode.ToString(); }
        }
        int dns = DnsQuery_W("www.microsoft.com.", 1, 0x148, IntPtr.Zero, out var records, IntPtr.Zero);
        if (records != IntPtr.Zero) DnsRecordListFree(records, 1);
        result["system-dns-wire-only"] = dns;
        if (transportPid != 0)
        {
            var handle = OpenProcess(0x2 | 0x8 | 0x20 | 0x40, false, transportPid);
            result["tor-injection-handle"] = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (handle != IntPtr.Zero) CloseHandle(handle);
            try
            {
                using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe")) { UseShellExecute = false, CreateNoWindow = true });
                child!.WaitForExit(); result["child-process"] = "started";
            }
            catch (System.ComponentModel.Win32Exception ex) { result["child-process"] = ex.NativeErrorCode; }
            try { File.ReadAllText(deniedFile); result["ungranted-file"] = "read"; }
            catch (UnauthorizedAccessException) { result["ungranted-file"] = "denied"; }
        }
        return result;
    }
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--installer-broker")
        {
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Environment.SetEnvironmentVariable("PRIVATECHAT_QA_WORKER", Path.GetFullPath(args[1]));
            using var client = new CoreClient();
            Console.WriteLine(new JsonObject { ["workerPid"] = client.ProcessId }.ToJsonString());
            while (Console.ReadLine() is string line)
            {
                var request = JsonNode.Parse(line)!;
                var response = request["op"]!.ToString() switch
                {
                    "init" => await client.Init(request["path"]!.ToString(), request["password"]!.ToString()),
                    "cmd" => await client.Command(request["command"]!.ToString()),
                    _ => throw new IOException("Unsupported installer test operation")
                };
                Console.WriteLine(new JsonObject { ["id"] = request["id"]!.DeepClone(), ["data"] = response.DeepClone() }.ToJsonString());
            }
            return 0;
        }
        if (args.FirstOrDefault() == "--probe")
        {
            var current = ReadToken(Environment.ProcessId);
            if (current["appContainer"]!.GetValue<int>() != 1 || current["capabilities"]!.GetValue<int>() != 0) throw new IOException("Probe is not restricted");
            Console.WriteLine((await Probe(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]), args[7])).ToJsonString());
            return 0;
        }
        string worker = Path.GetFullPath(args[0]); Environment.SetEnvironmentVariable("PRIVATECHAT_QA_WORKER", worker);
        string root = Path.Combine(AppContext.BaseDirectory, "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
        string outside = Path.Combine(Path.GetTempPath(), "PrivateChat-denied-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "SYNTHETIC-UNGRANTED-DATA");
        var report = new JsonObject { ["status"] = "running", ["at"] = DateTime.UtcNow.ToString("O"), ["workerAssemblySha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(worker, ".dll")))) };
        var checks = new JsonArray(); report["checks"] = checks;
        void Check(bool value, string name) { if (!value) throw new IOException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        using var tcp4 = new TcpListener(IPAddress.Loopback, 0); tcp4.Start();
        using var tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0); tcp6.Start();
        using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int t4 = ((IPEndPoint)tcp4.LocalEndpoint).Port, t6 = ((IPEndPoint)tcp6.LocalEndpoint).Port;
        int u4 = ((IPEndPoint)udp4.Client.LocalEndPoint!).Port, u6 = ((IPEndPoint)udp6.Client.LocalEndPoint!).Port;
        try
        {
            Check(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "Tests and application run without elevation");
            var baseline = await Probe(t4, t6, u4, u6, 0, 0, outside); report["baseline"] = baseline;
            Check(baseline["foreign-loopback-v4"]!.ToString() == "connected" && baseline["foreign-loopback-v6"]!.ToString() == "connected", "Unrestricted positive controls reach both local TCP listeners");
            using (await tcp4.AcceptTcpClientAsync()) { } using (await tcp6.AcceptTcpClientAsync()) { }
            Check((await udp4.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer.Length == 3 && (await udp6.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer.Length == 3, "Unrestricted IPv4 and IPv6 UDP datagrams actually arrive");
            Check(baseline["internet-v4"]!.ToString() == "connected", "Unrestricted public IPv4 TCP positive control succeeds");
            Check(baseline["system-dns-wire-only"]!.GetValue<int>() == 0, "Unrestricted uncached system DNS positive control succeeds");
            using var raw = Process.Start(new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, ArgumentList = { "--worker" } })!;
            await raw.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(raw.ExitCode == 2, "Production worker refuses to run outside AppContainer");
            string mode = args.Length > 1 ? args[1] : "direct";
            using var tor = new TorService(); tor.Start(root, mode);
            int targetPid = tor.ProcessId;
            if (mode != "direct")
            {
                for (int attempt = 0; attempt < 100 && targetPid == tor.ProcessId; attempt++)
                {
                    foreach (var candidate in Process.GetProcessesByName("lyrebird")) using (candidate)
                    {
                        try { if (candidate.MainModule?.FileName?.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) == true) { targetPid = candidate.Id; break; } } catch { }
                    }
                    if (targetPid == tor.ProcessId) await Task.Delay(100);
                }
                Check(targetPid != tor.ProcessId, mode + " child transport starts in AppContainer");
            }
            using var core = new CoreClient();
            var token = ReadToken(core.ProcessId); report["productionWorkerToken"] = token;
            Check(token["appContainer"]!.GetValue<int>() == 1 && token["capabilities"]!.GetValue<int>() == 0, "Actual production worker has an AppContainer token with zero capabilities");
            Check(ReadToken(tor.ProcessId)["capabilities"]!.GetValue<int>() == 1, "Tor alone receives internetClient capability");
            await core.Init(Path.Combine(root, "profile", "chat"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            using var job = new ChildJob();
            var sandbox = new NetworkSandbox(worker);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Probe.exe")) { WorkingDirectory = AppContext.BaseDirectory };
            foreach (string value in new[] { "--probe", t4.ToString(), t6.ToString(), u4.ToString(), u6.ToString(), tor.Port.ToString(), targetPid.ToString(), outside }) start.ArgumentList.Add(value);
            using var isolated = sandbox.Start(start, job, false);
            string output = await isolated.Output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(40));
            await isolated.Process.WaitForExitAsync();
            string errors = await isolated.Error.ReadToEndAsync();
            if (isolated.Process.ExitCode != 0) throw new IOException("Probe failed: " + isolated.Process.ExitCode + " " + errors);
            Check(true, "OS boundary probe completes inside production sandbox");
            var restricted = JsonNode.Parse(output)!.AsObject(); report["restricted"] = restricted;
            Check(restricted["internet-v4"]!.ToString() == "AccessDenied", "Windows denies direct IPv4 sockets");
            Check(restricted["internet-v6"]!.ToString() == "AccessDenied", "Windows denies direct IPv6 sockets");
            Check(restricted["system-dns-wire-only"]!.GetValue<int>() == 5, "Windows denies DNS client service queries with cache bypass");
            Check(restricted["foreign-loopback-v4"]!.ToString() != "connected" && !tcp4.Pending(), "Unrelated local IPv4 proxy is unreachable");
            Check(restricted["foreign-loopback-v6"]!.ToString() != "connected" && !tcp6.Pending(), "Unrelated local IPv6 proxy is unreachable");
            await Task.Delay(300);
            Check(udp4.Available == 0 && udp6.Available == 0, "Neither IPv4 nor IPv6 UDP datagram escapes to the external listeners");
            Check(restricted["own-tor-socks"]!.ToString() == "socks5", "Core sandbox can negotiate SOCKS5 with its own Tor");
            Check(restricted["tor-injection-handle"]!.GetValue<int>() == 5, "Core cannot open " + mode + " transport for code injection or handle duplication");
            Check(restricted["child-process"]!.ToString() != "started", "Windows prevents core child process creation");
            Check(restricted["ungranted-file"]!.ToString() == "denied", "Core cannot read an ungranted user file");
            tor.Dispose();
            Check(!Process.GetProcessById(core.ProcessId).HasExited, "Fault scenario deliberately leaves the core alive after stopping Tor");
            using var stoppedJob = new ChildJob();
            start.ArgumentList[6] = "0";
            using var stopped = sandbox.Start(start, stoppedJob, false);
            var stoppedResult = JsonNode.Parse(await stopped.Output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(40)))!.AsObject();
            await stopped.Process.WaitForExitAsync(); report["afterTorStopped"] = stoppedResult;
            Check(stopped.Process.ExitCode == 0 && stoppedResult["own-tor-socks"]!.ToString() != "socks5", "Stopped Tor has no usable SOCKS listener");
            Check(stoppedResult["internet-v4"]!.ToString() == "AccessDenied" && stoppedResult["internet-v6"]!.ToString() == "AccessDenied" && stoppedResult["system-dns-wire-only"]!.GetValue<int>() == 5, "OS still denies direct IPv4, IPv6 and DNS after Tor stops");
            Check(!tcp4.Pending() && !tcp6.Pending() && udp4.Available == 0 && udp6.Available == 0, "No external TCP or UDP listener receives traffic after Tor stops");
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["reason"] = ex.ToString(); Console.WriteLine(ex); }
        finally { File.Delete(outside); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "isolation-qa.json"), report.ToJsonString(new(System.Text.Json.JsonSerializerOptions.Default) { WriteIndented = true })); }
        return report["status"]!.ToString() == "passed" ? 0 : 1;
    }
    private static JsonObject ReadToken(int pid)
    {
        using var process = pid == Environment.ProcessId ? null : Process.GetProcessById(pid);
        if (!OpenProcessToken(process?.Handle ?? new IntPtr(-1), 8, out var token)) throw new System.ComponentModel.Win32Exception();
        try
        {
            int Read(int kind)
            {
                GetTokenInformation(token, kind, IntPtr.Zero, 0, out uint needed); var bytes = Marshal.AllocHGlobal((int)needed);
                try { if (!GetTokenInformation(token, kind, bytes, needed, out _)) throw new System.ComponentModel.Win32Exception(); return Marshal.ReadInt32(bytes); }
                finally { Marshal.FreeHGlobal(bytes); }
            }
            return new JsonObject { ["appContainer"] = Read(29), ["capabilities"] = Read(30) };
        }
        finally { CloseHandle(token); }
    }
    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)] private static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);
    [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr records, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr result, uint length, out uint needed);
}



