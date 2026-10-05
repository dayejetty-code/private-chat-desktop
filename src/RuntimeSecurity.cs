using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class RuntimeSecurity
{
    public static string Base => AppContext.BaseDirectory;
    public static void Verify(string category)
    {
        using var pinned = typeof(RuntimeSecurity).Assembly.GetManifestResourceStream("PrivateChat.RuntimeManifest") ?? throw new IOException("Missing pinned runtime manifest");
        using var published = File.OpenRead(Path.Combine(Base, "runtime-manifest.json"));
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(pinned), SHA256.HashData(published))) throw new IOException("Runtime manifest was changed");
        pinned.Position = 0;
        var manifest = JsonNode.Parse(pinned)!.AsObject();
        var files = manifest[category]!.AsObject();
        if (files.Count == 0 || category is not ("core" or "tor")) throw new IOException("Missing runtime category");
        foreach (var item in files)
        {
            var path = Path.GetFullPath(Path.Combine(Base, item.Key));
            string allowed = Path.GetFullPath(Path.Combine(Base, "runtime", category)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid runtime path");
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, item.Value!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Runtime integrity check failed");
        }
    }

    public static bool ConfigureProcess() => SetDefaultDllDirectories(0x1000) && WerSetFlags(1) == 0; // WER NOHEAP; not a ban on admin/system dumps
    public static IntPtr LoadCore()
    {
        var library = LoadLibraryEx(Path.Combine(Base, "runtime", "core", "libsimplex.dll"), IntPtr.Zero, 0x100 | 0x800);
        return library != IntPtr.Zero ? library : throw new IOException("Cannot load pinned core");
    }
    public static IntPtr LoadCrypto()
    {
        Verify("core");
        var library = LoadLibraryEx(Path.Combine(Base, "runtime", "core", "libcrypto-3-x64.dll"), IntPtr.Zero, 0x100 | 0x800);
        return library != IntPtr.Zero ? library : throw new IOException("Cannot load pinned cryptography runtime");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    public static ProcessStartInfo WorkerStart()
    {
        string executable = Path.Combine(Base, "PrivateChat.exe");
#if ENABLE_QA
        // QA can exercise the final release worker without shipping test entrypoints in it.
        if (Environment.GetEnvironmentVariable("PRIVATECHAT_QA_WORKER") is string target && Path.IsPathFullyQualified(target)) executable = target;
#endif
        var p = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Base
        };
        p.ArgumentList.Add("--worker");
        p.Environment["DOTNET_EnableDiagnostics"] = "0";
        return p;
    }
}

// Closing the owning process kills its Tor/core children, including transport helpers.
internal sealed class ChildJob : IDisposable
{
    private IntPtr handle;
    public ChildJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        int size = Marshal.SizeOf<ExtendedLimits>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, ptr, false);
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ptr, (uint)size)) throw new IOException("Cannot create private process job");
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public void Add(Process p)
    {
        if (!AssignProcessToJobObject(handle, p.Handle)) { p.Kill(true); throw new IOException("Cannot supervise private process"); }
    }
    public void Dispose() { var owned = Interlocked.Exchange(ref handle, IntPtr.Zero); if (owned != IntPtr.Zero) CloseHandle(owned); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long A, B; public uint LimitFlags; public UIntPtr C, D; public uint E; public UIntPtr F; public uint G, H; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr A, B, C, D; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
