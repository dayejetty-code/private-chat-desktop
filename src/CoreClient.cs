using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal sealed class CoreClient : IDisposable
{
    private readonly Process process;
    private readonly SandboxedProcess isolated;
    private readonly NetworkSandbox sandbox;
    private string dataRoot = "";
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
        try
        {
            var start = RuntimeSecurity.WorkerStart();
            sandbox = new NetworkSandbox(start.FileName);
            isolated = sandbox.Start(start, job, transport: false);
            process = isolated.Process;
        }
        catch { job.Dispose(); throw; }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Stop(unexpected: true);
        _ = ReadLoop();
        // Drain and discard. Native errors must never become a chat-content log.
        _ = Task.Run(async () => { try { while (await isolated.Error.ReadLineAsync() is string line) { if (line.StartsWith("worker-fault:", StringComparison.Ordinal)) startupFault = line; } } catch { } });
    }
    public async Task<JsonNode> Init(string path, string password)
    {
        path = FileTransfer.LocalPath(path);
        string key = await Task.Run(() => ProfileKeys.Resolve(Path.GetDirectoryName(path)!, password));
        try { return await InitRaw(path, key); }
        finally { key = ""; password = ""; }
    }
    internal Task<JsonNode> InitRaw(string path, string password)
    {
        path = FileTransfer.LocalPath(path);
        dataRoot = Path.GetDirectoryName(path)!;
        sandbox.GrantDirectory(dataRoot, true);
        return Request(new JsonObject { ["op"] = "init", ["path"] = path, ["password"] = password });
    }
    public void CheckIsolation() { NetworkSandbox.RequireFirewall(); sandbox.RequireNoLoopbackExemption(); }
    public Task<JsonNode> Command(string command) => Request(new JsonObject { ["op"] = "cmd", ["command"] = command });
    public Task<JsonNode> CloseStore() => Request(new JsonObject { ["op"] = "close-store" });
    public Task<JsonNode> MessageStatus(long contact) => Request(new JsonObject { ["op"] = "message-status", ["contact"] = contact });
    public Task<JsonNode> ReviewMessages(long contact, IEnumerable<string> tokens) => Request(new JsonObject { ["op"] = "message-review", ["contact"] = contact, ["tokens"] = new JsonArray(tokens.Select(t => JsonValue.Create(t)).ToArray()) });
    public Task<JsonNode> SendText(long contact, string token, string? text = null, long? source = null)
    {
        CheckIsolation();
        return Request(new JsonObject { ["op"] = "message-send", ["contact"] = contact, ["token"] = token, ["text"] = text, ["source"] = source });
    }
    public Task<JsonNode> RepairConnection(long contact)
    {
        CheckIsolation();
        return Request(new JsonObject { ["op"] = "connection-repair", ["contact"] = contact });
    }
    public Task<JsonNode> ParseServer(string server) => Request(new JsonObject { ["op"] = "parse-server", ["server"] = server });
    public async Task<JsonNode> EncryptFile(string path)
    {
        path = FileTransfer.LocalPath(path); TextDocument.RequireTextExtension(path);
        if (dataRoot.Length == 0 || FileTransfer.Within(dataRoot, path)) throw new IOException("Do not send profile files");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] text = TextDocument.Read(held);
        try { return await Request(new JsonObject { ["op"] = "encrypt-bytes", ["bytes"] = Convert.ToBase64String(text) }); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(text); }
    }
    public Task<JsonNode> ReceiveFile(long id) => Request(new JsonObject { ["op"] = "receive-file", ["fileId"] = id });
    public Task<JsonNode> SendFile(long contact, JsonNode source, string name) => Request(new JsonObject { ["op"] = "send-file", ["contact"] = contact, ["source"] = source.DeepClone(), ["name"] = name });
    public Task<JsonNode> CacheInfo(bool plan = false) => Request(new JsonObject { ["op"] = plan ? "cache-plan" : "cache-info" });
    public async Task<JsonNode> ExportFile(JsonNode source, long size, string target)
    {
        target = FileTransfer.LocalPath(target); TextDocument.RequireTextExtension(target);
        if (dataRoot.Length == 0 || FileTransfer.Within(dataRoot, target) || size is < 0 or > FileTransfer.MaximumBytes) throw new IOException("Invalid export");
        JsonNode result = await Request(new JsonObject { ["op"] = "export-bytes", ["source"] = source.DeepClone(), ["size"] = size });
        byte[]? text = null; bool created = false;
        try
        {
            string encoded = result["bytes"]!.GetValue<string>();
            if (encoded.Length > (FileTransfer.MaximumBytes + 2) / 3 * 4) throw new IOException("Invalid export length");
            text = Convert.FromBase64String(encoded); result.AsObject().Clear();
            // Validation also runs in the broker; a worker cannot make it write arbitrary binary files.
            byte[] normalized = TextDocument.Normalize(text);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(text); text = normalized;
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                File.WriteAllText(target + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", System.Text.Encoding.ASCII);
                output.Write(text); output.Flush(true);
            }
            return new JsonObject { ["type"] = "ok" };
        }
        catch { if (created) { try { File.Delete(target); } catch { } } throw; }
        finally { result.AsObject().Clear(); if (text != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(text); }
    }
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
            try { ObjectDisposedException.ThrowIf(disposed, this); await isolated.Input.WriteLineAsync(data.ToJsonString()); await isolated.Input.FlushAsync(); }
            finally { writeGate.Release(); data.Clear(); }
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(100));
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (await isolated.Output.ReadLineAsync() is string line)
            {
                JsonNode? value;
                try { value = JsonNode.Parse(line); } catch { continue; }
                if (value?["event"] is JsonNode evt) { Event?.Invoke(evt); continue; }
                if (value?["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id)) continue;
                if (!pending.TryRemove(id, out var waiter)) continue;
                if (value["data"] is JsonNode result) waiter.TrySetResult(result);
                else if (value["fault"]?.ToString() == "cache-full") waiter.TrySetException(new CacheCapacityException());
                else if (value["fault"]?.ToString().StartsWith("txt-", StringComparison.Ordinal) == true) waiter.TrySetException(new TextDocumentException(value["fault"]!.ToString()));
                else waiter.TrySetException(new IOException("Core rejected operation"
#if ENABLE_QA
                    + ": " + value["fault"]?.ToString()
#endif
                ));
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
        isolated.Dispose();
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
