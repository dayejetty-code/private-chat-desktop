using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PrivateChat;

// RFC 7914 scrypt from the already pinned OpenSSL runtime, not a custom KDF.
// Fixed work factors are authenticated by the PCKEY002 format identifier.
internal static class PasswordKdf
{
    internal const ulong Cost = 131072, BlockSize = 8, Parallelism = 1;
    private const ulong MaximumMemory = 192UL * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Lazy<DeriveFunction> Native = new(Load);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFunction(ulong options, IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeriveFunction([In] byte[] password, nuint passwordLength, [In] byte[] salt, nuint saltLength, ulong n, ulong r, ulong p, ulong maximumMemory, [Out] byte[] result, nuint resultLength);
    private static DeriveFunction Load()
    {
        IntPtr library = RuntimeSecurity.LoadCrypto(); // Absolute path, pinned hashes, restricted dependency search.
        var init = Marshal.GetDelegateForFunctionPointer<InitFunction>(NativeLibrary.GetExport(library, "OPENSSL_init_crypto"));
        // Do not load an OPENSSL_CONF file or providers requested by that file.
        if (init(0x80, IntPtr.Zero) != 1) throw new IOException("Cannot initialize password protection");
        return Marshal.GetDelegateForFunctionPointer<DeriveFunction>(NativeLibrary.GetExport(library, "EVP_PBE_scrypt"));
    }
    internal static byte[] Derive(string password, ReadOnlySpan<byte> salt) => DeriveCore(password, salt, Cost, BlockSize, Parallelism, 32);
    private static byte[] DeriveCore(string password, ReadOnlySpan<byte> salt, ulong n, ulong r, ulong p, int length)
    {
        byte[] encoded = new UTF8Encoding(false, true).GetBytes(password), saltBytes = salt.ToArray(), key = new byte[length];
        try
        {
            // Bound simultaneous memory use within this process. No weaker fallback.
            lock (Gate)
                if (Native.Value(encoded, (nuint)encoded.Length, saltBytes, (nuint)saltBytes.Length, n, r, p, MaximumMemory, key, (nuint)key.Length) != 1)
                    throw new CryptographicException("Memory-hard key derivation failed");
            return key;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
        finally { CryptographicOperations.ZeroMemory(encoded); CryptographicOperations.ZeroMemory(saltBytes); }
    }
#if ENABLE_QA
    internal static byte[] TestVector(string password, byte[] salt, ulong n, ulong r, ulong p, int length) => DeriveCore(password, salt, n, r, p, length);
#endif
}
