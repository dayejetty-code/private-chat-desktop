using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivateChat;

// Version 1: fixed header, authenticated manifest, ordered authenticated file chunks,
// authenticated empty end record. No compression, plaintext archive, or external tool.
internal static class BackupArchive
{
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    internal const int ChunkSize = 65536, Iterations = 600000, MaximumFiles = 50000;
    private static readonly byte[] Magic = "PCBACK01"u8.ToArray(), KeyMagic = "PCBACK02"u8.ToArray();
    internal sealed record Entry(string Name, long Length);
    internal sealed record Manifest(int Version, string ProfilePath, Entry[] Files);

    internal static void Write(string profile, string origin, string target, string password, CancellationToken token)
    {
        CheckPassword(password);
        var entries = ProfileBackup.Inventory(profile).Select(p => new Entry(Path.GetRelativePath(profile, p).Replace('\\', '/'), new FileInfo(p).Length)).ToArray();
        int version = ProfileKeys.Exists(profile) ? 2 : 1;
        var manifest = new Manifest(version, origin, entries); Validate(manifest);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (metadata.Length > 8 * 1024 * 1024) throw new IOException("Backup inventory too large");
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] header = new byte[48]; (version == 2 ? KeyMagic : Magic).CopyTo(header, 0); RandomNumberGenerator.Fill(header.AsSpan(8));
        output.Write(header);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(8, 32), Iterations, HashAlgorithmName.SHA256, 32);
        byte[] buffer = new byte[ChunkSize];
        try
        {
            using var cipher = new AesGcm(key, 16); uint counter = 0;
            // Manifest spans ordinary chunks, preceded by an authenticated fixed-size length record.
            byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, metadata.Length);
            WriteRecord(output, cipher, header, counter++, length);
            for (int offset = 0; offset < metadata.Length; offset += ChunkSize)
            { token.ThrowIfCancellationRequested(); WriteRecord(output, cipher, header, counter++, metadata.AsSpan(offset, Math.Min(ChunkSize, metadata.Length - offset))); }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                using var input = new FileStream(FileTransfer.LocalPath(Path.Combine(profile, entry.Name)), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != entry.Length) throw new IOException("Snapshot changed");
                for (long remaining = entry.Length; remaining > 0;)
                {
                    token.ThrowIfCancellationRequested(); int size = (int)Math.Min(ChunkSize, remaining);
                    input.ReadExactly(buffer.AsSpan(0, size)); WriteRecord(output, cipher, header, counter++, buffer.AsSpan(0, size)); remaining -= size;
                }
                if (input.ReadByte() != -1) throw new IOException("Snapshot changed");
            }
            token.ThrowIfCancellationRequested(); WriteRecord(output, cipher, header, counter, []); output.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(buffer); CryptographicOperations.ZeroMemory(metadata); }
    }

    internal static Manifest Extract(string archive, string stage, string password, CancellationToken token)
    {
        CheckPassword(password);
        using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumBytes + 16 * 1024 * 1024) throw new IOException("Backup too large");
        byte[] header = new byte[48]; input.ReadExactly(header);
        int version = header.AsSpan(0, 8).SequenceEqual(Magic) ? 1 : header.AsSpan(0, 8).SequenceEqual(KeyMagic) ? 2 : 0;
        if (version == 0) throw new InvalidDataException("Unsupported backup format");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(8, 32), Iterations, HashAlgorithmName.SHA256, 32);
        byte[]? metadata = null, plain = null;
        try
        {
            using var cipher = new AesGcm(key, 16); uint counter = 0;
            plain = ReadRecord(input, cipher, header, counter++, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(plain); CryptographicOperations.ZeroMemory(plain);
            if (length is < 1 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid inventory length");
            metadata = new byte[length];
            for (int offset = 0; offset < length; offset += ChunkSize)
            {
                token.ThrowIfCancellationRequested(); plain = ReadRecord(input, cipher, header, counter++, Math.Min(ChunkSize, length - offset));
                plain.CopyTo(metadata, offset); CryptographicOperations.ZeroMemory(plain);
            }
            var manifest = JsonSerializer.Deserialize<Manifest>(metadata, new JsonSerializerOptions { MaxDepth = 8 }) ?? throw new InvalidDataException();
            if (manifest.Version != version) throw new InvalidDataException("Backup version mismatch");
            Validate(manifest); Directory.CreateDirectory(stage);
            foreach (var entry in manifest.Files)
            {
                token.ThrowIfCancellationRequested(); string path = FileTransfer.LocalPath(Path.Combine(stage, entry.Name));
                if (!FileTransfer.Within(stage, path)) throw new InvalidDataException();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                for (long remaining = entry.Length; remaining > 0;)
                {
                    token.ThrowIfCancellationRequested(); int size = (int)Math.Min(ChunkSize, remaining);
                    plain = ReadRecord(input, cipher, header, counter++, size); output.Write(plain); CryptographicOperations.ZeroMemory(plain); remaining -= size;
                }
                output.Flush(true);
            }
            ReadRecord(input, cipher, header, counter, 0);
            if (input.ReadByte() != -1) throw new InvalidDataException("Trailing data");
            return manifest;
        }
        finally { CryptographicOperations.ZeroMemory(key); if (metadata != null) CryptographicOperations.ZeroMemory(metadata); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }

    private static void Validate(Manifest manifest)
    {
        if (manifest.Version is not (1 or 2) || manifest.ProfilePath == null || manifest.ProfilePath.Length > 1024 || manifest.Files == null || manifest.Files.Length is < 2 or > MaximumFiles) throw new InvalidDataException("Invalid inventory");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file == null || !SafeEntry(file.Name) || !names.Add(file.Name) || file.Length < 0 || file.Length > MaximumBytes) throw new InvalidDataException("Invalid entry");
            total = checked(total + file.Length); if (total > MaximumBytes) throw new InvalidDataException("Backup too large");
        }
        if (!names.Contains("chat_chat.db") || !names.Contains("chat_agent.db")) throw new InvalidDataException("Missing databases");
        if (names.Contains(ProfileKeys.FileName) != (manifest.Version == 2) || manifest.Files.Any(f => f.Name == ProfileKeys.FileName && (f.Length < ProfileKeys.EnvelopeBytes || f.Length > ProfileKeys.MaximumEnvelopeBytes))) throw new InvalidDataException("Invalid backup key envelope");
        foreach (string name in names)
            for (int separator = name.IndexOf('/'); separator >= 0; separator = name.IndexOf('/', separator + 1))
                if (names.Contains(name[..separator])) throw new InvalidDataException("File/directory collision");
    }
    internal static bool SafeEntry(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Contains('\\')) return false;
        string[] parts = name.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p != FileTransfer.SafeName(p) || p.EndsWith(' ') || p.EndsWith('.'))) return false;
        return parts.Length == 1 ? ProfileBackup.DatabaseFiles.Contains(name, StringComparer.Ordinal) || name == ProfileKeys.FileName : ProfileBackup.Folders.Contains(parts[0], StringComparer.Ordinal);
    }
    private static void CheckPassword(string password)
    { if (password.Length is < 12 or > 256 || password.Contains('\0')) throw new InvalidDataException("Invalid password"); }
    private static byte[] Aad(byte[] header, uint sequence, int size)
    {
        byte[] aad = new byte[56]; header.CopyTo(aad, 0); BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(48), sequence); BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(52), size); return aad;
    }
    private static byte[] Nonce(byte[] header, uint sequence)
    {
        byte[] nonce = new byte[12]; header.AsSpan(40, 8).CopyTo(nonce); BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), sequence); return nonce;
    }
    private static void WriteRecord(Stream output, AesGcm cipher, byte[] header, uint sequence, ReadOnlySpan<byte> plain)
    {
        byte[] encrypted = new byte[plain.Length], tag = new byte[16];
        cipher.Encrypt(Nonce(header, sequence), plain, encrypted, tag, Aad(header, sequence, plain.Length));
        output.Write(tag); output.Write(encrypted);
    }
    private static byte[] ReadRecord(Stream input, AesGcm cipher, byte[] header, uint sequence, int size)
    {
        byte[] tag = new byte[16], encrypted = new byte[size], plain = new byte[size];
        input.ReadExactly(tag); input.ReadExactly(encrypted);
        try { cipher.Decrypt(Nonce(header, sequence), encrypted, tag, plain, Aad(header, sequence, size)); return plain; }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }
}
