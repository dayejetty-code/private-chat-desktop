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
        RuntimeSecurity.Verify("tor");
        var torRoot = Path.Combine(RuntimeSecurity.Base, "runtime/tor");
        var privateDir = Path.Combine(dataRoot, "tor");
        Directory.CreateDirectory(privateDir);
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
            lines.Add("ClientTransportPlugin " + mode + " exec " + Quote(Path.Combine(torRoot, "tor/pluggable_transports/lyrebird.exe")));
            foreach (var bridge in config["bridges"]![mode]!.AsArray()) lines.Add("Bridge " + bridge!.ToString());
        }
        var torrc = Path.Combine(privateDir, "torrc");
        File.WriteAllLines(torrc, lines);
        var info = new ProcessStartInfo(Path.Combine(torRoot, "tor/tor.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = torRoot };
        info.ArgumentList.Add("-f"); info.ArgumentList.Add(torrc);
        job = new ChildJob();
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            var match = Regex.Match(e.Data ?? "", @"Bootstrapped (\d+)%");
            if (match.Success && !disposed) { Progress = int.Parse(match.Groups[1].Value); Changed?.Invoke(Progress); }
        };
        process.ErrorDataReceived += (_, _) => { }; // no persistent network logs
        process.Exited += (_, _) => { Progress = 0; if (!disposed) Exited?.Invoke(); };
        process.Start(); job.Add(process); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
        if (disposed) return;
        disposed = true; Progress = 0; job?.Dispose();
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
        process?.Dispose();
        }
    }
}
