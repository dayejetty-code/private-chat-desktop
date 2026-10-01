using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal sealed class CoreClient : IDisposable
{
    private readonly Process process;
    private readonly ChildJob job = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> pending = new();
    private long sequence;
    private int disposeState;
    private bool disposed => Volatile.Read(ref disposeState) != 0;
    private string startupFault = "";
    public event Action<JsonNode>? Event;
    public event Action? Exited;
    public int ProcessId => process.Id;
    public CoreClient()
    {
        process = new Process { StartInfo = RuntimeSecurity.WorkerStart(), EnableRaisingEvents = true };
        process.Exited += (_, _) => Stop(unexpected: true);
        process.Start();
        job.Add(process);
        _ = ReadLoop();
        // Drain and discard. Native errors must never become a chat-content log.
        _ = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync() is string line) { if (line.StartsWith("worker-fault:", StringComparison.Ordinal)) startupFault = line; } } catch { } });
    }
    public Task<JsonNode> Init(string path, string password) => Request(new JsonObject { ["op"] = "init", ["path"] = path, ["password"] = password });
    public Task<JsonNode> Command(string command) => Request(new JsonObject { ["op"] = "cmd", ["command"] = command });
    public Task<JsonNode> ParseServer(string server) => Request(new JsonObject { ["op"] = "parse-server", ["server"] = server });
    public async Task<JsonNode> Result(string command)
    {
        var raw = await Command(command);
        if (raw["error"] is JsonNode error) throw new CoreException(error);
        return raw["result"] ?? throw new IOException("Invalid core response");
    }
    private async Task<JsonNode> Request(JsonObject data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long id = Interlocked.Increment(ref sequence);
        data["id"] = id;
        var waiter = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            await writeGate.WaitAsync();
            try { ObjectDisposedException.ThrowIf(disposed, this); await process.StandardInput.WriteLineAsync(data.ToJsonString()); await process.StandardInput.FlushAsync(); }
            finally { writeGate.Release(); data.Clear(); }
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(100));
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                JsonNode? value;
                try { value = JsonNode.Parse(line); } catch { continue; }
                if (value?["event"] is JsonNode evt) { Event?.Invoke(evt); continue; }
                if (value?["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id)) continue;
                if (!pending.TryRemove(id, out var waiter)) continue;
                if (value["data"] is JsonNode result) waiter.TrySetResult(result);
                else waiter.TrySetException(new IOException("Core rejected operation"));
            }
        }
        catch { }
        // Without the response pipe the parent cannot supervise the unlocked core.
        finally { Stop(unexpected: true); }
    }
    private void FailPending() { foreach (var task in pending.Values) task.TrySetException(new IOException("Core disconnected " + startupFault)); }
    public void Dispose() => Stop(unexpected: false);
    private void Stop(bool unexpected)
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
        job.Dispose();
        try { if (!process.HasExited) process.Kill(true); } catch { }
        FailPending();
        process.Dispose();
        if (unexpected) Exited?.Invoke();
    }
}

internal sealed class CoreException(JsonNode error) : Exception("Core command failed")
{
    public string Category { get; } = error["errorType"]?["type"]?.ToString() ?? error["type"]?.ToString() ?? "unknown";
    // Only the synthetic acceptance runner uses this; the UI never persists it.
#if ENABLE_QA
    public string Diagnostic { get; } = error.ToJsonString();
#endif
}

internal static class PrivacyPolicy
{
    public static JsonObject Configure(JsonNode original, int socksPort)
    {
        var cfg = original.DeepClone().AsObject();
        cfg["socksProxy"] = "127.0.0.1:" + socksPort;
        cfg["socksMode"] = "always";
        cfg["hostMode"] = "onionViaSocks";
        cfg["requiredHostMode"] = false; // public relay hostnames still go through Tor SOCKS DNS
        cfg["sessionMode"] = "entity";
        cfg["smpProxyMode"] = "always";
        cfg["smpProxyFallback"] = "prohibit";
        cfg["logTLSErrors"] = false;
        return cfg;
    }
    public static bool IsStrict(JsonNode cfg, int port) =>
        port is > 0 and <= 65535 &&
        cfg["socksProxy"]?.ToString() == "127.0.0.1:" + port && cfg["socksMode"]?.ToString() == "always" &&
        cfg["sessionMode"]?.ToString() == "entity" && cfg["smpProxyMode"]?.ToString() == "always" &&
        cfg["smpProxyFallback"]?.ToString() == "prohibit" && cfg["logTLSErrors"]?.ToString() == "false" &&
        cfg["hostMode"]?.ToString() is "onionViaSocks" or "public";
    public static bool VerifiedContact(JsonNode? contact) => contact?["activeConn"]?["connectionCode"] != null;
    public static string Send(long contactId, string text) => $"/_send @{contactId} live=off ttl=default sign=off json " +
        new JsonArray(new JsonObject { ["msgContent"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["mentions"] = new JsonObject() }).ToJsonString();
    public static bool ValidInvite(string value) => value.Length <= 32768 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) &&
        (value.StartsWith("simplex:/invitation#", StringComparison.Ordinal) || value.StartsWith("https://simplex.chat/invitation#", StringComparison.Ordinal));
}
