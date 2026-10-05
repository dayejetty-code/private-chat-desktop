using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PrivateChat;

internal sealed class TorService : IDisposable
{
    private Process? process;
    private SandboxedProcess? isolated;
    private SandboxedProcess? bridge;
    private ChildJob? job;
    private bool disposed;
    private readonly object lifecycle = new();
    public int Port { get; }
    public int Progress { get; private set; }
    public bool Ready { get { lock (lifecycle) return !disposed && Progress == 100 && process is { HasExited: false }; } }
    public int ProcessId => process?.Id ?? -1;
    public event Action<int>? Changed;
    public event Action? Exited;
    // Terminate native networking synchronously when Tor exits unexpectedly.
    public void Guard(CoreClient client) => Exited += client.Dispose;
    public TorService()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    }
    public void Start(string dataRoot, string mode = "direct")
    {
        lock (lifecycle)
        {
        ObjectDisposedException.ThrowIf(disposed, this);
        if(mode is not ("direct" or "obfs4" or "snowflake"))throw new IOException("Unsupported Tor transport");
        RuntimeSecurity.Verify("tor");
        var torRoot = Path.Combine(RuntimeSecurity.Base, "runtime/tor");
        var privateDir = Path.Combine(dataRoot, "tor");
        Directory.CreateDirectory(privateDir);
        var sandbox = new NetworkSandbox(RuntimeSecurity.WorkerStart().FileName);
        sandbox.GrantDirectory(torRoot, false);
        sandbox.GrantDirectory(privateDir, true);
        job = new ChildJob();
        string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";
        var lines = new List<string>
        {
            "ClientOnly 1", "AvoidDiskWrites 1", "SafeLogging 1", "Log notice stdout",
            "SocksPort 127.0.0.1:" + Port + " IsolateSOCKSAuth",
            "DataDirectory " + Quote(privateDir),
            "GeoIPFile " + Quote(Path.Combine(torRoot, "data/geoip")),
            "GeoIPv6File " + Quote(Path.Combine(torRoot, "data/geoip6")),
            "DormantCanceledByStartup 1"
        };
        if (mode is "obfs4" or "snowflake")
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(torRoot, "tor/pluggable_transports/pt_config.json")))!;
            lines.Add("UseBridges 1");
            // Tor's managed-PT launcher uses global named pipes unavailable in
            // AppContainer. The parent supervises the pinned PT using inherited
            // anonymous pipes and the standard VERSION/CMETHOD handshake instead.
            string endpoint = StartBridge(sandbox, torRoot, privateDir, mode);
            lines.Add("ClientTransportPlugin " + mode + " socks5 " + endpoint);
            foreach (var bridge in config["bridges"]![mode]!.AsArray()) lines.Add("Bridge " + bridge!.ToString());
        }
        var torrc = Path.Combine(privateDir, "torrc");
        File.WriteAllLines(torrc, lines);
        var info = new ProcessStartInfo(Path.Combine(torRoot, "tor/tor.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = torRoot };
        info.ArgumentList.Add("-f"); info.ArgumentList.Add(torrc);
        try { isolated = sandbox.Start(info, job, transport: true); process = isolated.Process; }
        catch { job.Dispose(); job = null; throw; }
        process.EnableRaisingEvents = true;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await isolated.Output.ReadLineAsync() is string line)
                {
#if ENABLE_QA
                    Console.Error.WriteLine("Tor QA: " + line);
#endif
                    var match = Regex.Match(line, @"Bootstrapped (\d+)%");
                    if (match.Success && !disposed) { Progress = int.Parse(match.Groups[1].Value); Changed?.Invoke(Progress); }
                }
            }
            catch { }
        });
        _ = Task.Run(async () => { try { while (await isolated.Error.ReadLineAsync() is string line) {
#if ENABLE_QA
            Console.Error.WriteLine("Tor QA stderr: " + line);
#endif
        } } catch { } });
        process.Exited += (_, _) => TransportStopped();
        if (process.HasExited) throw new IOException("Tor stopped during isolated startup");
        }
    }
    private void TransportStopped()
    {
        Progress = 0;
        if (!disposed) { job?.Dispose(); Exited?.Invoke(); }
    }
    private string StartBridge(NetworkSandbox sandbox, string torRoot, string privateDir, string mode)
    {
        var info = new ProcessStartInfo(Path.Combine(torRoot, "tor/pluggable_transports/lyrebird.exe")) { WorkingDirectory = torRoot };
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("TOR_PT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["TOR_PT_MANAGED_TRANSPORT_VER"] = "1";
        info.Environment["TOR_PT_STATE_LOCATION"] = Path.Combine(privateDir, "pt_state");
        info.Environment["TOR_PT_CLIENT_TRANSPORTS"] = mode;
        info.Environment["TOR_PT_EXIT_ON_STDIN_CLOSE"] = "1";
        bridge = sandbox.Start(info, job!, transport: true);
        bridge.Process.EnableRaisingEvents = true;
        bridge.Process.Exited += (_, _) => TransportStopped();
        var owned = bridge;
        _ = Task.Run(async () => { try { while (await owned.Error.ReadLineAsync() != null) { } } catch { } });
        async Task<string> Negotiate()
        {
            bool version = false; string? address = null;
            for (int count = 0; count < 128; count++)
            {
                string line = await owned.Output.ReadLineAsync() ?? throw new IOException("Bridge closed initialization pipe");
                if (line.Length > 16384) throw new IOException("Invalid bridge initialization");
                if (line == "VERSION 1") version = true;
                else if (line.StartsWith("CMETHOD ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 4 || parts[1] != mode || parts[2] != "socks5" || !IPEndPoint.TryParse(parts[3], out var endpoint) || !endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Port == 0) throw new IOException("Unexpected bridge proxy");
                    address = endpoint.ToString();
                }
                else if (line == "CMETHODS DONE")
                {
                    if (!version || address == null || owned.Process.HasExited) throw new IOException("Bridge did not initialize");
                    return address;
                }
                else if (line.StartsWith("VERSION-ERROR", StringComparison.Ordinal) || line.StartsWith("ENV-ERROR", StringComparison.Ordinal) || line.StartsWith("CMETHOD-ERROR", StringComparison.Ordinal)) throw new IOException("Bridge rejected initialization");
            }
            throw new IOException("Bridge initialization overflow");
        }
        try
        {
            string endpoint = Negotiate().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            _ = Task.Run(async () => { try { while (await owned.Output.ReadLineAsync() != null) { } } catch { } });
            return endpoint;
        }
        catch { job?.Dispose(); owned.Dispose(); throw; }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
        if (disposed) return;
        disposed = true; Progress = 0; job?.Dispose();
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
        isolated?.Dispose();
        bridge?.Dispose();

        }
    }
}
