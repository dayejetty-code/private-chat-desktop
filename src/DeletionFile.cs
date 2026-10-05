using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PrivateChat;

// Delete an explicitly reviewed directory entry through its verified handle.
// File-level overwrite/read-back is not a storage-device sanitization guarantee.
internal static class DeletionFile
{
    internal sealed record Stamp(string Path, bool Directory, uint Volume, ulong Id, long Bytes, long Written);
    [StructLayout(LayoutKind.Sequential)] private struct Info
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, IntPtr buffer, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte value, uint length);
    private static SafeFileHandle Open(string path, uint access, uint share, bool writeThrough = false)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, 3, 0x02200000 | (writeThrough ? 0x80000000u : 0), IntPtr.Zero); // OPEN_EXISTING, OPEN_REPARSE_POINT, BACKUP_SEMANTICS
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open reviewed entry", new Win32Exception(Marshal.GetLastWin32Error())); }
        return handle;
    }
    private static Stamp Read(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot identify reviewed entry");
        if ((info.Attributes & 0x400) != 0 || info.Links > 1) throw new IOException("Linked entries are not supported");
        return new(path, (info.Attributes & 0x10) != 0, info.Volume, ((ulong)info.IdHigh << 32) | info.IdLow,
            ((long)info.SizeHigh << 32) | info.SizeLow, ((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime);
    }
    internal sealed class Pins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        internal Pins(string path, bool includeSelf)
        {
            path = FileTransfer.LocalPath(path);
            var ancestors = new Stack<string>();
            for (string? item = includeSelf ? path : Path.GetDirectoryName(path); item != null; item = Path.GetDirectoryName(item)) ancestors.Push(item);
            try
            {
                foreach (string ancestor in ancestors)
                {
                    var handle = Open(ancestor, 0x80, 3); handles.Add(handle); // No SHARE_DELETE: pin ancestor against rename/reparse replacement.
                    if (!Read(handle, ancestor).Directory) throw new IOException("Invalid parent directory");
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; --i) handles[i].Dispose(); handles.Clear(); }
    }
    internal static Stamp Inspect(string path, bool backup = false)
    {
        path = FileTransfer.LocalPath(path);
        using var pins = new Pins(path, false);
        using var handle = Open(path, backup ? 0x80000000 : 0x80, 1);
        var stamp = Read(handle, path);
        if (backup)
        {
            if (stamp.Directory || !path.EndsWith(".pcbackup", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a Private Chat backup file");
            using var input = new FileStream(handle, FileAccess.Read);
            Span<byte> magic = stackalloc byte[8]; input.ReadExactly(magic);
            if (!magic.SequenceEqual("PCBACK01"u8) && !magic.SequenceEqual("PCBACK02"u8)) throw new IOException("Not a recognized Private Chat backup");
        }
        return stamp;
    }
    internal static bool Same(Stamp a, Stamp b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Directory == b.Directory && a.Volume == b.Volume && a.Id == b.Id &&
        (a.Directory || (a.Bytes == b.Bytes && a.Written == b.Written));
    internal static byte[] ReadFixed(Stamp reviewed, int length)
    {
        if (reviewed.Directory || reviewed.Bytes != length || length is < 0 or > 4096) throw new IOException("Invalid fixed file size");
        using var pins = new Pins(reviewed.Path, false);
        using var handle = Open(FileTransfer.LocalPath(reviewed.Path), 0x80000000, 1);
        if (!Same(reviewed, Read(handle, reviewed.Path))) throw new IOException("Key file changed before read");
        using var input = new FileStream(handle, FileAccess.Read);
        byte[] result = new byte[length]; input.ReadExactly(result); return result;
    }
    internal static void Delete(Stamp reviewed)
    {
        using var pins = new Pins(reviewed.Path, false);
        using (var handle = Open(FileTransfer.LocalPath(reviewed.Path), 0x10080, 0)) // DELETE | READ_ATTRIBUTES, exclusive
        {
            if (!Same(reviewed, Read(handle, reviewed.Path))) throw new IOException("Reviewed entry changed");
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new IOException("Entry was not deleted", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (File.Exists(reviewed.Path) || Directory.Exists(reviewed.Path)) throw new IOException("Entry remains present");
    }
    internal static void DestroyKey(Stamp reviewed)
    {
        if (reviewed.Directory || reviewed.Bytes is < 0 or > 4096 || System.IO.Path.GetFileName(reviewed.Path) != ProfileKeys.FileName) throw new IOException("Invalid key entry");
        Erase(reviewed, CancellationToken.None);
    }
    private static void RequirePlainStream(SafeFileHandle handle, long length)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (0x200 | 0x800 | 0x4000)) != 0)
            throw new IOException("Sparse, compressed or EFS files cannot be verified by this eraser");
        // Query streams on the same exclusive handle. Do not claim full clearance
        // if hidden streams are present, or the filesystem cannot enumerate them.
        const int size = 65536;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(handle, 7, buffer, size)) throw new IOException("Cannot verify file streams", new Win32Exception(Marshal.GetLastWin32Error()));
            int next = Marshal.ReadInt32(buffer), nameBytes = Marshal.ReadInt32(buffer, 4);
            if (next != 0 || nameBytes != 14 || Marshal.ReadInt64(buffer, 8) != length || Marshal.PtrToStringUni(buffer + 24, nameBytes / 2) != "::$DATA")
                throw new IOException("Additional or unexpected file streams require separate review");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static void Erase(Stamp reviewed, CancellationToken token, Action<long>? verified = null)
    {
        if (reviewed.Directory || reviewed.Bytes < 0) throw new IOException("File required for overwrite");
        token.ThrowIfCancellationRequested();
        using var pins = new Pins(reviewed.Path, false);
        using (var handle = Open(FileTransfer.LocalPath(reviewed.Path), 0xC0010080, 0, writeThrough: true))
        {
            if (!Same(reviewed, Read(handle, reviewed.Path))) throw new IOException("Reviewed file changed");
            RequirePlainStream(handle, reviewed.Bytes);
            using var file = new FileStream(handle, FileAccess.ReadWrite);
            byte[] zeros = new byte[65536], check = new byte[65536];
            try
            {
                for (long remaining = reviewed.Bytes; remaining > 0;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(remaining, zeros.Length); file.Write(zeros, 0, count); remaining -= count;
                }
                file.Flush(true); file.Position = 0;
                for (long done = 0; done < reviewed.Bytes;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(reviewed.Bytes - done, check.Length); file.ReadExactly(check.AsSpan(0, count));
                    if (!check.AsSpan(0, count).SequenceEqual(zeros.AsSpan(0, count))) throw new IOException("File overwrite could not be verified");
                    done += count; verified?.Invoke(done);
                }
                if (file.Length != reviewed.Bytes) throw new IOException("File length changed during verification");
                token.ThrowIfCancellationRequested();
            }
            finally { CryptographicOperations.ZeroMemory(check); }
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new IOException("Verified file was not deleted");
        }
        if (File.Exists(reviewed.Path)) throw new IOException("Verified file remains present");
        // Read-back checks the OS-visible bytes only. SSD remapping, snapshots,
        // backups, paging and a previously copied key remain outside this guarantee.
    }
}
