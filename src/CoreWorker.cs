using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PrivateChat;

// No listening socket, HTTP API, passwords in arguments, or persisted diagnostics.
internal static class CoreWorker
{
    private static IntPtr controller;
    private static readonly object outputGate = new();
    private static volatile bool closing;
    private static string dataRoot = "";
    public static int Run()
    {
        // A hidden WinExe has redirected pipes but no console code page to change.
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        try
        {
            NetworkSandbox.RequireRestrictedWorker();
            RuntimeSecurity.Verify("core");
            NativeLibrary.SetDllImportResolver(typeof(CoreWorker).Assembly, (name, _, _) => name == "libsimplex" ? RuntimeSecurity.LoadCore() : IntPtr.Zero);
            hs_init(IntPtr.Zero, IntPtr.Zero);
            string? line;
            Task? events = null;
            while ((line = Console.ReadLine()) != null)
            {
                JsonNode? request = null;
                try
                {
                    request = JsonNode.Parse(line);
                    long id = request!["id"]!.GetValue<long>();
                    var operation = request["op"]!.GetValue<string>();
                    JsonNode? response;
                    if (operation == "init" && controller == IntPtr.Zero)
                    {
                        string path = request["path"]!.GetValue<string>();
                        dataRoot = Path.GetDirectoryName(Path.GetFullPath(path))!;
                        string passphrase = request["password"]!.GetValue<string>();
                        if (passphrase.Length < 12 || passphrase.Contains('\0')) throw new InvalidOperationException("password");
                        using var key = new SensitiveUtf8(passphrase);
                        response = JsonNode.Parse(Read(chat_migrate_init(path, key.Pointer, "yesUp", out controller)));
                        request["password"] = null;
                        if (response?["type"]?.ToString() == "ok")
                            events = Task.Run(EventLoop);
                    }
                    else if (operation == "parse-server" && controller != IntPtr.Zero)
                    {
                        string server = request["server"]!.GetValue<string>();
                        if (!RelaySettings.IsSafeAddress(server)) throw new InvalidOperationException("server");
                        using var address = new SensitiveUtf8(server);
                        response = JsonNode.Parse(Read(chat_parse_server(address.Pointer)));
                    }
                    else if (operation == "encrypt-bytes" && controller != IntPtr.Zero)
                        response = EncryptTextBytes(request["bytes"]!.GetValue<string>());
                    else if (operation == "export-bytes" && controller != IntPtr.Zero)
                        response = ExportTextBytes(request["source"]!, request["size"]!.GetValue<long>());
                    else if (operation is "cache-info" or "cache-plan" && controller != IntPtr.Zero)
                    {
                        // Scan first: a file newly created by the core cannot be mistaken for an old orphan.
                        var entries = FileCache.Scan(dataRoot);
                        response = FileCache.Describe(dataRoot, entries, FileCache.Read(NativeResult), operation == "cache-plan");
                    }
                    else if (operation == "receive-file" && controller != IntPtr.Zero)
                    {
                        long fileId = request["fileId"]!.GetValue<long>();
                        var records = FileCache.Read(NativeResult);
                        var record = records.Single(r => r.Id == fileId);
                        if (record.State != "rcv_invitation" || record.Size is < 0 or > FileTransfer.MaximumBytes) throw new IOException("File is not receivable");
                        FileCache.RequireRoom(dataRoot, records, FileCache.Budget(record.Size));
                        string target = FileTransfer.CachedPath(dataRoot, Guid.NewGuid().ToString("N") + ".bin");
                        response = NativeResult($"/freceive {fileId} approved_relays=off encrypt=on inline=off {target}");
                    }
                    else if (operation == "send-file" && controller != IntPtr.Zero)
                    {
                        var source = request["source"]!;
                        string path = FileTransfer.CachedPath(dataRoot, source["filePath"]!.GetValue<string>());
                        long length = new FileInfo(path).Length;
                        if (length > FileTransfer.MaximumBytes + 1024) throw new IOException("Invalid attachment");
                        FileCache.RequireRoom(dataRoot, FileCache.Read(NativeResult), FileCache.Budget(length));
                        response = NativeResult(FileTransfer.SendCommand(request["contact"]!.GetValue<long>(), source, request["name"]!.GetValue<string>()));
                    }
                    else if (operation == "message-send" && controller != IntPtr.Zero)
                        response = new MessageRecovery(NativeResult).Send(request["contact"]!.GetValue<long>(), request["token"]!.GetValue<string>(), request["text"]?.GetValue<string>(), request["source"]?.GetValue<long>());
                    else if (operation == "message-status" && controller != IntPtr.Zero)
                        response = new MessageRecovery(NativeResult).Status(request["contact"]!.GetValue<long>());
                    else if (operation == "message-review" && controller != IntPtr.Zero)
                        response = new MessageRecovery(NativeResult).Review(request["contact"]!.GetValue<long>(), request["tokens"]!.AsArray());
                    else if (operation == "connection-repair" && controller != IntPtr.Zero)
                        response = new MessageRecovery(NativeResult).Repair(request["contact"]!.GetValue<long>());
                    else if (operation == "close-store" && controller != IntPtr.Zero)
                    {
                        closing = true;
                        if (events != null && !events.Wait(5000)) throw new IOException("Event reader did not stop");
                        Read(chat_close_store(controller)); controller = IntPtr.Zero;
                        response = new JsonObject { ["type"] = "ok" };
                    }
                    else if (operation == "cmd" && controller != IntPtr.Zero)
                    {
                        string command = request["command"]!.GetValue<string>();
                        if (command.Contains('\0')) throw new InvalidOperationException("command");
                        using var cmd = new SensitiveUtf8(command);
                        response = JsonNode.Parse(Read(chat_send_cmd(controller, cmd.Pointer)));
                    }
                    else throw new InvalidOperationException("state");
                    Write(new JsonObject { ["id"] = id, ["data"] = response });
                }
                catch (TextDocumentException ex) { Write(new JsonObject { ["id"] = request?["id"]?.DeepClone(), ["fault"] = ex.Code }); }
                catch (CacheCapacityException) { Write(new JsonObject { ["id"] = request?["id"]?.DeepClone(), ["fault"] = "cache-full" }); }
                catch (Exception ex)
                {
#if ENABLE_QA
                    Write(new JsonObject { ["id"] = request?["id"]?.DeepClone(), ["fault"] = ex.ToString() });
#else
                    _ = ex;
                    Write(new JsonObject { ["id"] = request?["id"]?.DeepClone(), ["fault"] = "core-operation-failed" });
#endif
                }
                finally { request?.AsObject().Clear(); line = null; }
            }
            closing = true;
            events?.Wait(1500);
            if (controller != IntPtr.Zero) Read(chat_close_store(controller));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("worker-fault:" + ex.GetType().Name + ":" + ex.HResult); return 2; }
    }
    private static void EventLoop()
    {
        while (!closing)
        {
            var text = Read(chat_recv_msg_wait(controller, 500000));
            if (text.Length == 0) continue;
            try { Write(new JsonObject { ["event"] = JsonNode.Parse(text) }); }
            catch { closing = true; }
        }
    }
    private static void Write(JsonObject value) { lock (outputGate) { Console.WriteLine(value.ToJsonString()); Console.Out.Flush(); } }
    private static JsonNode NativeResult(string command)
    {
        using var cmd = new SensitiveUtf8(command);
        var response = JsonNode.Parse(Read(chat_send_cmd(controller, cmd.Pointer)))!;
        if (response["error"] != null || response["result"] == null) throw new IOException("Cache operation rejected by core");
        return response["result"]!.DeepClone();
    }
    private static unsafe JsonNode EncryptTextBytes(string encoded)
    {
        if (encoded.Length > (FileTransfer.MaximumBytes + 2) / 3 * 4) throw new IOException("Invalid text length");
        byte[] raw = Convert.FromBase64String(encoded), text;
        try { text = TextDocument.Normalize(raw); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw); }
        string? target = null; bool complete = false;
        try
        {
            FileCache.RequireRoom(dataRoot, FileCache.Read(NativeResult), text.Length + 1024L);
            string name = Guid.NewGuid().ToString("N") + ".bin";
            target = FileTransfer.CachedPath(dataRoot, name);
            using var to = new SensitiveUtf8(target);
            JsonNode result;
            fixed (byte* data = text)
                result = JsonNode.Parse(Read(chat_write_file(controller, to.Pointer, (IntPtr)data, text.Length)))!;
            if (result["type"]?.ToString() != "result" || result["cryptoArgs"] == null)
                throw new IOException("Encryption failed");
            var source = new JsonObject { ["filePath"] = name, ["cryptoArgs"] = result["cryptoArgs"]!.DeepClone() };
            complete = true;
            return source;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(text);
            if (!complete && target != null) { try { File.Delete(target); } catch { } }
        }
    }
    private static unsafe JsonNode ExportTextBytes(JsonNode source, long expectedSize)
    {
        string fromPath = FileTransfer.CachedPath(dataRoot, source["filePath"]!.GetValue<string>());
        if (expectedSize is < 0 or > FileTransfer.MaximumBytes || new FileInfo(fromPath).Length > FileTransfer.MaximumBytes + 1024) throw new IOException("Invalid export");
        using var from = new SensitiveUtf8(fromPath);
        using var key = new SensitiveUtf8(source["cryptoArgs"]!["fileKey"]!.GetValue<string>());
        using var nonce = new SensitiveUtf8(source["cryptoArgs"]!["fileNonce"]!.GetValue<string>());
        // Native read authenticates the entire encrypted file before returning bytes.
        // This avoids writing a partially decrypted file before checking its tag.
        IntPtr buffer = chat_read_file(from.Pointer, key.Pointer, nonce.Pointer);
        if (buffer == IntPtr.Zero) throw new IOException("File authentication failed");
        if (Marshal.ReadByte(buffer) != 0) { Read(buffer); throw new IOException("File authentication failed"); }
        uint length = unchecked((uint)Marshal.ReadInt32(buffer, 1));
        byte[]? text = null;
        try
        {
            if (length != expectedSize || length > FileTransfer.MaximumBytes) throw new IOException("File size mismatch");
            text = TextDocument.Normalize(new ReadOnlySpan<byte>((byte*)buffer + 5, (int)length));
            return new JsonObject { ["type"] = "ok", ["bytes"] = Convert.ToBase64String(text) };
        }
        finally { if (text != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(text); if (length <= FileTransfer.MaximumBytes) System.Security.Cryptography.CryptographicOperations.ZeroMemory(new Span<byte>((byte*)buffer + 5, (int)length)); free(buffer); }
    }
    private static unsafe string Read(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        int length = 0;
        while (Marshal.ReadByte(ptr, length) != 0) length++;
        try { return Marshal.PtrToStringUTF8(ptr) ?? ""; }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(new Span<byte>((void*)ptr, length)); free(ptr); }
    }
    // Wipe the explicit native argument buffer. Managed/native-internal copies may remain.
    private sealed class SensitiveUtf8 : IDisposable
    {
        public IntPtr Pointer { get; }
        private readonly int length;
        public SensitiveUtf8(string value) { length = Encoding.UTF8.GetByteCount(value) + 1; Pointer = Marshal.StringToCoTaskMemUTF8(value); }
        public unsafe void Dispose() { System.Security.Cryptography.CryptographicOperations.ZeroMemory(new Span<byte>((void*)Pointer, length)); Marshal.FreeCoTaskMem(Pointer); }
    }
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern void hs_init(IntPtr argc, IntPtr argv);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_migrate_init([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr key, [MarshalAs(UnmanagedType.LPUTF8Str)] string confirm, out IntPtr ctrl);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_send_cmd(IntPtr ctrl, IntPtr command);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_recv_msg_wait(IntPtr ctrl, int microseconds);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_close_store(IntPtr ctrl);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_parse_server(IntPtr server);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_write_file(IntPtr ctrl, IntPtr target, IntPtr bytes, int length);
    [DllImport("libsimplex", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr chat_read_file(IntPtr source, IntPtr key, IntPtr nonce);
    // The pinned DLL imports api-ms-win-crt-heap (UCRT), not legacy msvcrt.
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void free(IntPtr ptr);
}
