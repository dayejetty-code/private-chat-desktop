using System.IO;
using System.Security.Cryptography;

namespace PrivateChat;

// Random database key, password-wrapped with authenticated encryption. Erasing
// this envelope is an application-level operation, not certified media erasure.
internal static class ProfileKeys
{
    internal const string FileName = "profile.key";
    internal const int EnvelopeBytes = 100;
    internal const int MaximumEnvelopeBytes = 4104;
    private const int Iterations = 600000;
    internal const int NewPasswordMinimum = 16;
    internal static bool Exists(string root) => File.Exists(Path.Combine(root, FileName)) || Directory.Exists(Path.Combine(root, FileName));
    // Version indication only. Resolve must still authenticate the entire envelope.
    internal static bool IsStrong(string root)
    {
        if (!Exists(root)) return false;
        try
        {
            string path = FileTransfer.LocalPath(Path.Combine(root, FileName));
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[8];
            if (input.Length <= EnvelopeBytes || input.Length > MaximumEnvelopeBytes) return false;
            input.ReadExactly(header); return header.SequenceEqual("PCKEY003"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static bool IsInternalKey(string root, string path)
    {
        if (Path.GetFileName(path) != FileName || !FileTransfer.Within(root, path)) return false;
        string relative = Path.GetRelativePath(root, path);
        return relative == FileName || relative.StartsWith(".restore-history\\", StringComparison.Ordinal) || relative.StartsWith(".restore-transaction\\", StringComparison.Ordinal) || relative.StartsWith(".backup-work-", StringComparison.Ordinal) || relative.StartsWith("key-export-", StringComparison.Ordinal);
    }
    internal static string Resolve(string root, string password)
    {
        CheckPassword(password);
        if (!Exists(root)) return password; // Legacy stores only; a random-key database cannot open with this password.
        string path = FileTransfer.LocalPath(Path.Combine(root, FileName));
        using var pins = new DeletionFile.Pins(root, true);
        var entry = DeletionFile.Inspect(path);
        if (entry.Directory || entry.Bytes < EnvelopeBytes || entry.Bytes > MaximumEnvelopeBytes) throw new InvalidDataException("Invalid local key envelope");
        byte[] envelope = DeletionFile.ReadFixed(entry, checked((int)entry.Bytes));
        byte[] secret = new byte[32]; byte[]? wrapping = null;
        try
        {
            if (envelope.AsSpan(0, 8).SequenceEqual("PCKEY003"u8))
            {
                byte[] inner = UserKeyProtection.Unprotect(envelope.AsSpan(8));
                CryptographicOperations.ZeroMemory(envelope); envelope = inner;
                if (envelope.Length != EnvelopeBytes || !envelope.AsSpan(0, 8).SequenceEqual("PCKEY002"u8)) throw new InvalidDataException("Invalid user-bound key envelope");
            }
            if (envelope.Length != EnvelopeBytes) throw new InvalidDataException("Invalid local key envelope");
            if (envelope.AsSpan(0, 8).SequenceEqual("PCKEY002"u8)) wrapping = PasswordKdf.Derive(password, envelope.AsSpan(8, 32));
            else if (envelope.AsSpan(0, 8).SequenceEqual("PCKEY001"u8)) wrapping = Rfc2898DeriveBytes.Pbkdf2(password, envelope.AsSpan(8, 32), Iterations, HashAlgorithmName.SHA256, 32);
            else throw new InvalidDataException("Unsupported local key envelope");
            using var cipher = new AesGcm(wrapping, 16);
            cipher.Decrypt(envelope.AsSpan(40, 12), envelope.AsSpan(52, 32), envelope.AsSpan(84, 16), secret, envelope.AsSpan(0, 52));
            return Convert.ToHexString(secret);
        }
        catch (CryptographicException) { throw new InvalidDataException("Incorrect password, unavailable Windows user key or damaged key envelope"); }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(envelope); if (wrapping != null) CryptographicOperations.ZeroMemory(wrapping); }
    }
    internal static void CreateNew(string root, string password)
    {
        if (password.Length < NewPasswordMinimum) throw new InvalidDataException("New profiles require a longer password");
        using var pins = new DeletionFile.Pins(root, true);
        if (Exists(root) || ProfileBackup.DatabaseFiles.Any(n => File.Exists(Path.Combine(root, n)))) throw new IOException("Profile already exists");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try { Write(root, password, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal static void Write(string root, string password, ReadOnlySpan<byte> secret)
    {
        CheckPassword(password);
        if (secret.Length != 32) throw new InvalidDataException("Invalid key length");
        using var pins = new DeletionFile.Pins(root, true);
        byte[] envelope = new byte[EnvelopeBytes]; "PCKEY002"u8.CopyTo(envelope); RandomNumberGenerator.Fill(envelope.AsSpan(8, 44));
        byte[] wrapping = PasswordKdf.Derive(password, envelope.AsSpan(8, 32)); byte[]? bound = null;
        try
        {
            using var cipher = new AesGcm(wrapping, 16);
            cipher.Encrypt(envelope.AsSpan(40, 12), secret, envelope.AsSpan(52, 32), envelope.AsSpan(84, 16), envelope.AsSpan(0, 52));
            bound = UserKeyProtection.Protect(envelope);
            using var file = new FileStream(FileTransfer.LocalPath(Path.Combine(root, FileName)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write("PCKEY003"u8); file.Write(bound); file.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(wrapping); CryptographicOperations.ZeroMemory(envelope); if (bound != null) CryptographicOperations.ZeroMemory(bound); }
    }
    private static void CheckPassword(string password)
    { if (password.Length is < 12 or > 256 || password.Contains('\0')) throw new InvalidDataException("Invalid password"); }
}
