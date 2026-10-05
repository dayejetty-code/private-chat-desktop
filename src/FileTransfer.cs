using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PrivateChat;

// Files are encrypted by the pinned core. Only user-requested exports are plaintext.
internal static class FileTransfer
{
    public const long MaximumBytes = 25 * 1024 * 1024;
    public const long CacheLimit = 512 * 1024 * 1024;
    public static string Cache(string root) => Path.Combine(root, "files");
    public static string Status(JsonNode file) => file["fileStatus"]?["type"]?.ToString() ?? "invalid";
    public static long Id(JsonNode file) => file["fileId"]?.GetValue<long>() ?? 0;
    public static long Size(JsonNode file) => file["fileSize"]?.GetValue<long>() ?? -1;
    public static bool Supported(JsonNode file) => Id(file) > 0 && Size(file) is >= 0 and <= MaximumBytes && file["fileProtocol"]?.ToString() == "xftp";
    public static bool Receivable(JsonNode file) => Supported(file) && Status(file) == "rcvInvitation";
    public static bool Cancellable(JsonNode file) => Id(file) > 0 && Status(file) is "sndStored" or "sndTransfer" or "sndWarning" or "rcvAccepted" or "rcvTransfer" or "rcvWarning";
    public static bool Exportable(JsonNode file) => Supported(file) && Status(file) == "rcvComplete" && file["fileSource"]?["cryptoArgs"] != null;
    public static string SizeLabel(long bytes) => bytes < 0 ? "大小未知" : bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB" : (bytes / 1048576d).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    public static string StatusLabel(JsonNode file) => Status(file) switch
    {
        "sndStored" => "等待上传", "sndTransfer" => Progress(file, "snd", "正在上传"), "sndComplete" => "已上传 · 等待对方接收",
        "sndCancelled" => "已停止发送", "sndError" => "上传失败 · 可重新发送", "sndWarning" => "上传暂遇问题",
        "rcvInvitation" => "等待你确认接收", "rcvAccepted" => "准备下载", "rcvTransfer" => Progress(file, "rcv", "正在下载"),
        "rcvComplete" => "已接收 · 加密保存在本机", "rcvAborted" => "下载已中止 · 请对方重新发送", "rcvCancelled" => "传输已取消",
        "rcvError" => "接收失败 · 请对方重新发送", "rcvWarning" => "下载暂遇问题", _ => "文件状态未知"
    };
    private static string Progress(JsonNode file, string prefix, string label)
    {
        var state = file["fileStatus"]!;
        long done = state[prefix + "Progress"]?.GetValue<long>() ?? 0, total = state[prefix + "Total"]?.GetValue<long>() ?? 0;
        return total > 0 ? label + " · " + Math.Clamp((int)(100d * done / total), 0, 100) + "%" : label;
    }
    public static string SafeName(string name)
    {
        // Treat even directory separators and bidirectional overrides as untrusted text.
        var invalid = Path.GetInvalidFileNameChars();
        if (name.Length > 180)
        {
            string suffix = Path.GetExtension(name);
            if (suffix.Length > 16) suffix = "";
            int end = 180 - suffix.Length;
            if (char.IsHighSurrogate(name[end - 1])) --end;
            name = name[..end] + suffix;
        }
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ? '_' : c).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(clean) || IsDeviceName(clean)) clean = "文件_" + clean;
        return clean;
    }
    private static bool IsDeviceName(string name)
    {
        // Treat a padded device stem conservatively, including before an extension.
        string stem = name.Split('.')[0].TrimEnd(' ');
        return new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[0-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
    public static bool Within(string root, string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':' || path[2] != '\\' || path[3..].Contains(':') || path.Any(char.IsControl)) throw new IOException("Choose a local file path");
        string full = Path.GetFullPath(path);
        if (full[3..].Split('\\', '/').Any(IsDeviceName)) throw new IOException("Device names are not allowed");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) throw new IOException("Remote paths are not allowed");
        for (string? item = full; item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are not allowed");
        return full;
    }
    public static string CachedPath(string root, string relative)
    {
        if (!Path.IsPathRooted(relative) && relative.Split('/', '\\').Any(p => p is ".." or "." or "")) throw new IOException("Invalid cache path");
        string full = LocalPath(Path.IsPathRooted(relative) ? relative : Path.Combine(Cache(root), relative));
        if (!Within(Cache(root), full)) throw new IOException("File escaped cache");
        return full;
    }
    public static void CheckRoom(string root, long bytes)
    {
        if (bytes is < 0 or > MaximumBytes) throw new IOException("Maximum file size is 25 MB");
        FileCache.RequireRoom(root, [], FileCache.Budget(bytes));
    }
    public static async Task Configure(CoreClient core, string root)
    {
        string cache = LocalPath(Cache(root)), temp = LocalPath(Path.Combine(root, "file-temp"));
        await core.Result("/set file paths " + new JsonObject { ["appFilesFolder"] = cache, ["appTempFolder"] = temp, ["appAssetsFolder"] = LocalPath(Path.Combine(root, "file-assets")) }.ToJsonString());
        await core.Result("/_files_encrypt on");
    }
    // Generate once per send, before confirmation. Never derive this label from the source.
    public static string NewTextName() => "文档-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant() + ".txt";
    public static string SendCommand(long contact, JsonNode source, string name)
    {
        TextDocument.RequireTextExtension(name);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\A文档-[0-9a-f]{24}\.txt\z")) throw new IOException("Outgoing text filename must be anonymous");
        return $"/_send @{contact} live=off ttl=default sign=off json " +
            new JsonArray(new JsonObject { ["fileSource"] = source.DeepClone(), ["msgContent"] = new JsonObject { ["type"] = "file", ["text"] = name }, ["mentions"] = new JsonObject() }).ToJsonString();
    }
    public static string DisplayName(JsonNode item)
    {
        string name = item["file"]?["fileName"]?.ToString() ?? "文件";
        string text = item["content"]?["msgContent"]?["text"]?.ToString() ?? "";
        // New sends carry an anonymous label; older messages may still carry their original name.
        if (System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-f0-9]{32}\\.bin$") && text.Length is > 0 and <= 180) name = text;
        return SafeName(name);
    }
    public static bool IsTextItem(JsonNode item) => TextDocument.HasTextExtension(DisplayName(item));
    public static async Task<JsonNode> FreshFile(CoreClient core, long contact, long itemId) => (await FreshItem(core, contact, itemId))["file"]!;
    public static async Task<JsonNode> FreshItem(CoreClient core, long contact, long itemId)
    {
        var result = await core.Result($"/_get item info @{contact} {itemId}");
        var item = result["chatItem"]?["chatItem"];
        if (item?["meta"]?["itemId"]?.GetValue<long>() != itemId || item["file"] == null) throw new IOException("File message no longer available");
        return item;
    }
    public static async Task Receive(CoreClient core, string root, JsonNode file)
    {
        if (!Receivable(file)) throw new IOException("File cannot be received");
        // The serialized worker reads all active tasks from the encrypted store before admitting this one.
        await core.ReceiveFile(Id(file));
    }
}
