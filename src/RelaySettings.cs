using System.IO;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal sealed class RelayPolicyException : Exception { }

internal static class RelaySettings
{
    public const int MaxServers = 8;
    public static bool IsSafeAddress(string value) => value.Length is > 6 and <= 4096 &&
        value.StartsWith("smp://", StringComparison.Ordinal) && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    public static string[] Lines(string text)
    {
        if (text.Length > 32768) throw new ArgumentException("地址列表过长。");
        var lines = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (lines.Length is 0 or > MaxServers) throw new ArgumentException("请填写 1 至 8 个完整 SMP 地址，每行一个。");
        if (lines.Any(x => !IsSafeAddress(x))) throw new ArgumentException("请填写 smp:// 开头的完整地址，地址内部不能包含空格或换行。");
        if (lines.Distinct(StringComparer.Ordinal).Count() != lines.Length) throw new ArgumentException("存在重复地址，请删除重复行。");
        return lines;
    }
    public static IEnumerable<JsonObject> Servers(JsonArray groups) => groups.SelectMany(g => g?["smpServers"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>();
    public static string[] Enabled(JsonArray groups) => groups
        .Where(g => g?["operator"] == null || g["operator"]?["enabled"]?.GetValue<bool>() == true)
        .SelectMany(g => g?["smpServers"]?.AsArray() ?? new JsonArray())
        .Where(s => s?["enabled"]?.GetValue<bool>() == true && s["deleted"]?.GetValue<bool>() != true)
        .Select(s => s!["server"]!.ToString()).Order(StringComparer.Ordinal).ToArray();
    public static bool IsPresetConfiguration(JsonArray groups) => Enabled(groups).Length > 0 && Servers(groups)
        .Where(s => s["enabled"]?.GetValue<bool>() == true && s["deleted"]?.GetValue<bool>() != true)
        .All(s => s["preset"]?.GetValue<bool>() == true);
    public static async Task<JsonArray> Read(CoreClient core, long user) =>
        (await core.Result($"/_servers {user}"))["userServers"]?.AsArray() ?? throw new IOException("Missing server configuration");
    public static async Task CheckPolicy(CoreClient core, int port)
    {
        var network = (await core.Result("/network"))["networkConfig"];
        if (network == null || !PrivacyPolicy.IsStrict(network, port)) throw new RelayPolicyException();
    }
    public static async Task Parse(CoreClient core, string address)
    {
        if (!IsSafeAddress(address) || (await core.ParseServer(address))["serverAddress"] == null)
            throw new ArgumentException("中继地址格式无效，请复制服务器提供的完整 SMP 地址，包含证书指纹。");
    }
    public static async Task<bool> Test(CoreClient core, long user, string address, int port)
    {
        await Parse(core,address);
        await CheckPolicy(core,port);
        var result = await core.Result($"/_server test {user} {address}");
        await CheckPolicy(core,port);
        return result["type"]?.ToString() == "serverTestResult" && result["testFailure"] == null;
    }
    // Preserve XFTP, operator and chat-relay fields. This screen configures SMP only.
    public static JsonArray Build(JsonArray current, string[]? addresses)
    {
        var result = (JsonArray)current.DeepClone();
        var remaining = new HashSet<string>(addresses ?? [], StringComparer.Ordinal);
        foreach(var server in Servers(result))
        {
            bool preset = server["preset"]?.GetValue<bool>() == true;
            bool selected = addresses == null ? preset : remaining.Remove(server["server"]!.ToString());
            server["enabled"] = selected;
            server["deleted"] = !preset && !selected;
            if (selected) server["roles"] = addresses == null ? new JsonObject() : new JsonObject { ["storage"] = true, ["proxy"] = true, ["names"] = false };
        }
        var custom = result.OfType<JsonObject>().FirstOrDefault(g => g["operator"] == null);
        if (custom == null) { custom = new JsonObject { ["operator"] = null, ["smpServers"] = new JsonArray(), ["xftpServers"] = new JsonArray(), ["chatRelays"] = new JsonArray() }; result.Add(custom); }
        foreach(string address in remaining) custom["smpServers"]!.AsArray().Add(new JsonObject {
            ["server"] = address, ["preset"] = false, ["enabled"] = true, ["deleted"] = false,
            ["roles"] = new JsonObject { ["storage"] = true, ["proxy"] = true, ["names"] = false }
        });
        return result;
    }
    public static async Task<JsonArray> Save(CoreClient core, long user, string[]? addresses, int port, Func<bool> stillActive)
    {
        await CheckPolicy(core,port);
        if (addresses != null) foreach(var address in addresses) await Parse(core,address);
        var current = await Read(core,user);
        var proposed = Build(current,addresses);
        if (Enabled(proposed).Length == 0) throw new ArgumentException("至少需要一个已启用的消息中继。");
        var validation = await core.Result($"/_validate_servers {user} " + proposed.ToJsonString());
        if (validation["serverErrors"] is not JsonArray errors || errors.Count != 0)
            throw new ArgumentException("中继配置未通过核心校验，请检查重复服务器、运营者设置和完整地址。");
        if (!stillActive()) throw new OperationCanceledException();
        await CheckPolicy(core,port);
        if (!stillActive()) throw new OperationCanceledException();
        await core.Result($"/_servers {user} " + proposed.ToJsonString());
        var saved = await Read(core,user);
        if (!Enabled(saved).SequenceEqual(Enabled(proposed))) throw new IOException("Server readback mismatch");
        await CheckPolicy(core,port);
        return saved;
    }
}
