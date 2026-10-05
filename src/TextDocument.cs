using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PrivateChat;

// A text-only attachment policy. This validates bytes, not just the picker filter.
internal static class TextDocument
{
    public const string Filter = "纯文本文件 (*.txt)|*.txt";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, false, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, false, true);
    public static bool HasTextExtension(string name) => Path.GetExtension(name).Equals(".txt", StringComparison.OrdinalIgnoreCase);
    public static void RequireTextExtension(string name)
    {
        if (!HasTextExtension(name)) throw new TextDocumentException("txt-extension");
    }
    public static byte[] Read(Stream input)
    {
        if (input.Length > FileTransfer.MaximumBytes) throw new TextDocumentException("txt-size");
        byte[] original = new byte[checked((int)input.Length)];
        try { input.ReadExactly(original); return Normalize(original); }
        finally { CryptographicOperations.ZeroMemory(original); }
    }
    public static byte[] Normalize(ReadOnlySpan<byte> input)
    {
        if (input.Length > FileTransfer.MaximumBytes) throw new TextDocumentException("txt-size");
        string text;
        try
        {
            // Only deterministic encodings are accepted; guessing legacy encodings can corrupt text.
            if (input.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || input.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
                throw new TextDocumentException("txt-encoding");
            if (input.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) text = Utf8.GetString(input[3..]);
            else if (input.StartsWith(new byte[] { 0xff, 0xfe })) text = Utf16Le.GetString(input[2..]);
            else if (input.StartsWith(new byte[] { 0xfe, 0xff })) text = Utf16Be.GetString(input[2..]);
            else text = Utf8.GetString(input);
        }
        catch (DecoderFallbackException) { throw new TextDocumentException("txt-encoding"); }
        // A second leading BOM would become a new encoding marker on the next pass.
        // Reject that ambiguity so accepted text is stable through staging and export.
        if (text.StartsWith('\uFEFF')) throw new TextDocumentException("txt-encoding");
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')) || text.StartsWith("%PDF-", StringComparison.Ordinal) || text.StartsWith("{\\rtf", StringComparison.Ordinal))
            throw new TextDocumentException("txt-content");
        if (Utf8.GetByteCount(text) > FileTransfer.MaximumBytes) throw new TextDocumentException("txt-size");
        // No temporary plaintext file, metadata container, alternate data stream or BOM is copied.
        // Visible text (including names/addresses), Unicode formatting and line endings are preserved.
        return Utf8.GetBytes(text);
    }
}

internal sealed class TextDocumentException(string code) : IOException("Text attachment rejected")
{
    public string Code { get; } = code;
    public string UserMessage => Code switch
    {
        "txt-extension" => "仅支持 .txt 纯文本文件，不支持 Word、PDF、图片或压缩包。",
        "txt-encoding" => "无法可靠读取文本编码。请用记事本将文件另存为 UTF-8 编码的 .txt 后重试。",
        "txt-content" => "文件含有二进制数据、非文本控制字符，或是改名后的文档格式，已停止处理。",
        "txt-size" => "原文件和转换为 UTF-8 后的文本均须不超过 25 MB。",
        _ => "文本文件检查未通过，已停止处理。"
    };
}
