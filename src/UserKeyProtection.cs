using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PrivateChat;

// Windows DPAPI CurrentUser. This is account binding, not TPM sealing; roaming
// profiles, recovery credentials and a compromised logged-in user remain outside it.
internal static class UserKeyProtection
{
    private static ReadOnlySpan<byte> Context => "PrivateChat/ProfileKey/PCKEY003"u8;
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(ReadOnlySpan<byte> data) => Transform(data, true);
    internal static byte[] Unprotect(ReadOnlySpan<byte> data) => Transform(data, false);
    private static unsafe byte[] Transform(ReadOnlySpan<byte> data, bool protect)
    {
        if (data.Length is < 1 or > 4096) throw new CryptographicException("Invalid protected key size");
        fixed (byte* inputPointer = data)
        fixed (byte* contextPointer = Context)
        {
            var input = new Blob { Length = data.Length, Data = (IntPtr)inputPointer };
            var entropy = new Blob { Length = Context.Length, Data = (IntPtr)contextPointer };
            Blob output = default;
            try
            {
                // UI_FORBIDDEN, deliberately without LOCAL_MACHINE. Failure must
                // never fall back to password-only storage or create another key.
                bool ok = protect ? CryptProtectData(ref input, "Private Chat local key", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new CryptographicException("Windows user key protection failed", new Win32Exception(Marshal.GetLastWin32Error()));
                if (output.Data == IntPtr.Zero || output.Length is < 1 or > 4096) throw new CryptographicException("Invalid protected key result");
                return new ReadOnlySpan<byte>((void*)output.Data, output.Length).ToArray();
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    if (output.Length is > 0 and <= 4096) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length));
                    LocalFree(output.Data);
                }
            }
        }
    }
}
