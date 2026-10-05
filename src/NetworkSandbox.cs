using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PrivateChat;

// The worker has NO network capabilities. Only the pinned Tor process (and its
// transport children) receives internetClient. Windows permits loopback within
// this package, but not a connection to an unrelated local proxy. No exemptions,
// administrator service, global firewall changes, or ordinary-process fallback.
internal sealed class NetworkSandbox
{
    private static readonly object gate = new();
    public string Name { get; }
    public SecurityIdentifier Sid { get; }
    private static string IdentityName(string directory)
    {
        string owner = WindowsIdentity.GetCurrent().User!.Value;
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return "PrivateChat.Net." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner + "|" + directory.ToUpperInvariant())))[..40];
    }
    public NetworkSandbox(string executable)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        Name = IdentityName(directory);
        lock (gate)
        {
            int result = CreateAppContainerProfile(Name, "Private Chat network isolation", "Private Chat restricted core and Tor transport", IntPtr.Zero, 0, out var sid);
            if (result < 0 && result != unchecked((int)0x800700B7)) throw new NetworkIsolationException("profile", result);
            if (sid == IntPtr.Zero && (result = DeriveAppContainerSidFromAppContainerName(Name, out sid)) < 0) throw new NetworkIsolationException("identity", result);
            try { Sid = new SecurityIdentifier(sid); } finally { FreeSid(sid); }
            RequireFirewall(); RequireNoLoopbackExemption();
            GrantDirectory(directory, false);
        }
    }
    public void RequireNoLoopbackExemption()
    {
        uint result = NetworkIsolationGetAppContainerConfig(out uint count, out var entries);
        if (result != 0) throw new NetworkIsolationException("loopback-policy", (int)result);
        try
        {
            for (int i = 0; i < count; i++)
                if (new SecurityIdentifier(Marshal.ReadIntPtr(entries, i * 16)).Equals(Sid)) throw new NetworkIsolationException("loopback-exemption");
        }
        finally
        {
            for (int i = 0; i < count; i++) HeapFree(GetProcessHeap(), 0, Marshal.ReadIntPtr(entries, i * 16));
            if (entries != IntPtr.Zero) HeapFree(GetProcessHeap(), 0, entries);
        }
    }
    public static void RequireFirewall()
    {
        IntPtr manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero) throw new NetworkIsolationException("service-status", Marshal.GetLastWin32Error());
        try
        {
            foreach (string name in new[] { "BFE", "MpsSvc" })
            {
                IntPtr service = OpenServiceW(manager, name, 4);
                if (service == IntPtr.Zero) throw new NetworkIsolationException("service-status", Marshal.GetLastWin32Error());
                try { if (!QueryServiceStatus(service, out var status) || status.state != 4) throw new NetworkIsolationException("filtering-service-stopped"); }
                finally { CloseServiceHandle(service); }
            }
        }
        finally { CloseServiceHandle(manager); }
        object? policy = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic firewall = policy!;
            foreach (int profile in new[] { 1, 2, 4 })
                if (!(bool)firewall.FirewallEnabled[profile]) throw new NetworkIsolationException("firewall-disabled");
        }
        catch (NetworkIsolationException) { throw; }
        catch (Exception ex) { throw new NetworkIsolationException("firewall-status", ex.HResult); }
        finally { if (policy != null) Marshal.FinalReleaseComObject(policy); }
    }
    public void GrantDirectory(string path, bool write)
    {
        path = FileTransfer.LocalPath(path);
        if (write)
        {
            foreach (var reserved in new[] { Path.GetPathRoot(path)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), RuntimeSecurity.Base })
                if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(reserved), StringComparison.OrdinalIgnoreCase)) throw new NetworkIsolationException("data-directory");
        }
        var directory = Directory.CreateDirectory(path);
        // Do not propagate a new ACL through junctions into unrelated folders.
        foreach (var item in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new NetworkIsolationException("linked-directory");
        var acl = directory.GetAccessControl();
        var rights = write ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute;
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        if (acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Equals(Sid) && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & rights) == rights && r.InheritanceFlags == inherit)) return;
        acl.AddAccessRule(new FileSystemAccessRule(Sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    public static void RequireRestrictedWorker()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var app = TokenInfo(identity.Token, 29);
        try { if (Marshal.ReadInt32(app) != 1) throw new NetworkIsolationException("worker-not-isolated"); }
        finally { Marshal.FreeHGlobal(app); }
        var caps = TokenInfo(identity.Token, 30);
        try { if (Marshal.ReadInt32(caps) != 0) throw new NetworkIsolationException("worker-capabilities"); }
        finally { Marshal.FreeHGlobal(caps); }
        var package = TokenInfo(identity.Token, 31);
        IntPtr expected = IntPtr.Zero;
        try
        {
            int hr = DeriveAppContainerSidFromAppContainerName(IdentityName(RuntimeSecurity.Base), out expected);
            if (hr < 0 || !new SecurityIdentifier(Marshal.ReadIntPtr(package)).Equals(new SecurityIdentifier(expected))) throw new NetworkIsolationException("worker-package");
        }
        finally { Marshal.FreeHGlobal(package); if (expected != IntPtr.Zero) FreeSid(expected); }
    }
    private static IntPtr TokenInfo(IntPtr token, int kind)
    {
        GetTokenInformation(token, kind, IntPtr.Zero, 0, out uint size);
        var result = Marshal.AllocHGlobal(checked((int)size));
        if (size == 0 || !GetTokenInformation(token, kind, result, size, out _)) { Marshal.FreeHGlobal(result); throw new NetworkIsolationException("token", Marshal.GetLastWin32Error()); }
        return result;
    }
    public SandboxedProcess Start(ProcessStartInfo info, ChildJob job, bool transport)
    {
        RequireFirewall(); RequireNoLoopbackExemption();
        GrantDirectory(Path.GetDirectoryName(info.FileName)!, false);
        return SandboxedProcess.Start(info, job, Sid, transport);
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr value, uint length, out uint needed);
    [DllImport("Firewallapi.dll")] private static extern uint NetworkIsolationGetAppContainerConfig(out uint count, out IntPtr entries);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcessHeap();
    [DllImport("kernel32.dll")] private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint type, state, accepted, error, specificError, checkpoint, wait; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? server, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}

internal sealed class NetworkIsolationException(string reason, int code = 0) : IOException("Network isolation: " + reason + " (" + code + ")");

internal sealed class SandboxedProcess : IDisposable
{
    public Process Process { get; }
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }
    private SandboxedProcess(Process process, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        Process = process;
        Input = new StreamWriter(new FileStream(input, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
        Output = new StreamReader(new FileStream(output, FileAccess.Read), Encoding.UTF8);
        Error = new StreamReader(new FileStream(error, FileAccess.Read), Encoding.UTF8);
    }
    internal static SandboxedProcess Start(ProcessStartInfo info, ChildJob job, SecurityIdentifier package, bool transport)
    {
        var allocations = new List<IntPtr>(); var handles = new List<IntPtr>();
        IntPtr Allocate(int size) { var p = Marshal.AllocHGlobal(size); allocations.Add(p); return p; }
        IntPtr SidPointer(SecurityIdentifier sid) { byte[] data = new byte[sid.BinaryLength]; sid.GetBinaryForm(data, 0); var p = Allocate(data.Length); Marshal.Copy(data, 0, p, data.Length); return p; }
        var pipeSecurity = new SecurityAttributes { length = Marshal.SizeOf<SecurityAttributes>(), inherit = 1 };
        IntPtr list = IntPtr.Zero, descriptor = IntPtr.Zero; bool initialized = false;
        ProcessInfo pi = default; Process? process = null;
        try
        {
            void Pipe(out IntPtr read, out IntPtr write) { if (!CreatePipe(out read, out write, ref pipeSecurity, 0)) throw new Win32Exception(); handles.Add(read); handles.Add(write); }
            Pipe(out var stdin, out var input); Pipe(out var output, out var stdout); Pipe(out var error, out var stderr);
            foreach (var parent in new[] { input, output, error }) if (!SetHandleInformation(parent, 1, 0)) throw new Win32Exception();
            var inherited = Allocate(IntPtr.Size * 3);
            Marshal.WriteIntPtr(inherited, 0, stdin); Marshal.WriteIntPtr(inherited, 8, stdout); Marshal.WriteIntPtr(inherited, 16, stderr);
            var capability = Allocate(16);
            Marshal.WriteIntPtr(capability, SidPointer(new SecurityIdentifier("S-1-15-3-1"))); Marshal.WriteInt32(capability, 8, 4);
            var caps = Allocate(Marshal.SizeOf<Capabilities>());
            Marshal.StructureToPtr(new Capabilities { sid = SidPointer(package), capabilities = transport ? capability : IntPtr.Zero, count = transport ? 1u : 0 }, caps, false);
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, transport ? 2 : 3, 0, ref size);
            list = Allocate(checked((int)size));
            if (!InitializeProcThreadAttributeList(list, transport ? 2 : 3, 0, ref size)) throw new Win32Exception(); initialized = true;
            void Attribute(int kind, IntPtr value, int bytes) { if (!UpdateProcThreadAttribute(list, 0, (IntPtr)kind, value, (IntPtr)bytes, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(); }
            Attribute(0x20009, caps, Marshal.SizeOf<Capabilities>()); Attribute(0x20002, inherited, 24);
            if (!transport) { var childPolicy = Allocate(4); Marshal.WriteInt32(childPolicy, 1); Attribute(0x2000e, childPolicy, 4); }
            // Package peers must not acquire a handle to the network-capable Tor
            // process and inject code into it. Parent user and SYSTEM keep control.
            string user = WindowsIdentity.GetCurrent().User!.Value;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)(A;;GA;;;" + user + ")", 1, out descriptor, out _)) throw new Win32Exception();
            var security = new SecurityAttributes { length = Marshal.SizeOf<SecurityAttributes>(), descriptor = descriptor };
            var startup = new StartupEx { startup = new Startup { cb = Marshal.SizeOf<StartupEx>(), flags = 0x100, stdin = stdin, stdout = stdout, stderr = stderr }, attributes = list };
            static string Quote(string text)
            {
                var output = new StringBuilder("\""); int slash = 0;
                foreach (char c in text) { if (c == '\\') { slash++; continue; } output.Append('\\', c == '"' ? slash * 2 + 1 : slash); output.Append(c); slash = 0; }
                return output.Append('\\', slash * 2).Append('"').ToString();
            }
            var command = new StringBuilder(Quote(info.FileName) + " " + string.Join(" ", info.ArgumentList.Select(Quote)));
            var environment = new SortedDictionary<string, string?>(info.Environment, StringComparer.OrdinalIgnoreCase) { ["DOTNET_EnableDiagnostics"] = "0" };
            string block = string.Join('\0', environment.Where(x => x.Value != null).Select(x => x.Key + "=" + x.Value)) + "\0\0";
            var env = Allocate((block.Length + 1) * 2); Marshal.Copy(block.ToCharArray(), 0, env, block.Length);
            if (!CreateProcessW(info.FileName, command, ref security, ref security, true, 0x08080404, env, info.WorkingDirectory, ref startup, out pi)) throw new Win32Exception();
            process = Process.GetProcessById(pi.pid);
            job.Add(process); // Still suspended: no code runs before supervision.
            var result = new SandboxedProcess(process, new SafeFileHandle(input, true), new SafeFileHandle(output, true), new SafeFileHandle(error, true));
            handles.Remove(input); handles.Remove(output); handles.Remove(error);
            if (ResumeThread(pi.thread) == uint.MaxValue) { result.Dispose(); throw new Win32Exception(); }
            return result;
        }
        catch { if (pi.process != IntPtr.Zero) TerminateProcess(pi.process, 76); process?.Dispose(); throw; }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            if (pi.thread != IntPtr.Zero) CloseHandle(pi.thread); if (pi.process != IntPtr.Zero) CloseHandle(pi.process);
            foreach (var h in handles) CloseHandle(h);
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }
    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(true); } catch { }
        try { Input.Dispose(); } catch { } try { Output.Dispose(); } catch { } try { Error.Dispose(); } catch { }
        Process.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Capabilities { public IntPtr sid, capabilities; public uint count, reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int length; public IntPtr descriptor; public int inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public int cb; public string? reserved, desktop, title; public uint x, y, width, height, charsX, charsY, fill, flags; public ushort show, reserved2; public IntPtr reservedPtr, stdin, stdout, stderr; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup startup; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string exe, StringBuilder command, ref SecurityAttributes process, ref SecurityAttributes thread, bool inherit, uint flags, IntPtr env, string cwd, ref StartupEx startup, out ProcessInfo info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
