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
    public static int Run()
    {
        // A hidden WinExe has redirected pipes but no console code page to change.
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        try
        {
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
                catch { Write(new JsonObject { ["id"] = request?["id"]?.DeepClone(), ["fault"] = "core-operation-failed" }); }
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
    // The pinned DLL imports api-ms-win-crt-heap (UCRT), not legacy msvcrt.
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void free(IntPtr ptr);
}
