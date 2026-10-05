using System.IO;
using System.Text.Json.Nodes;

namespace PrivateChat;

// The worker serializes these operations. The intent is committed to the existing
// SQLCipher store BEFORE calling send. A lost reply is never permission to resend.
// This is local duplicate suppression, not a claim of exactly-once delivery.
internal sealed class MessageRecovery(Func<string, JsonNode> command)
{
    private void Sql(string sql) => command("/sql chat " + sql);
    private void Schema() => Sql("CREATE TABLE IF NOT EXISTS privatechat_send_attempts (token TEXT PRIMARY KEY, contact_id INTEGER NOT NULL, source_id INTEGER UNIQUE, state TEXT NOT NULL, result_id INTEGER)");
    private JsonArray Rows(string where)
    {
        Schema();
        var result = command("/sql chat SELECT json_object('token',token,'contact',contact_id,'source',source_id,'state',state,'item',result_id) AS attempt FROM privatechat_send_attempts WHERE " + where);
        if (result["type"]?.ToString() != "sQLResult" || result["rows"] is not JsonArray rows || (rows.Count > 0 && rows[0]?.ToString() != "attempt")) throw new IOException("Cannot read send intent");
        return new JsonArray(rows.Skip(1).Select(r => JsonNode.Parse(r!.GetValue<string>())).ToArray());
    }
    public JsonNode Status(long contact)
    {
        RequireContact(contact);
        return new JsonObject { ["attempts"] = Rows($"contact_id={contact} AND (state='pending' OR source_id IS NOT NULL)") };
    }
    public JsonNode Review(long contact, JsonArray tokens)
    {
        RequireContact(contact);
        if (tokens.Count is < 1 or > 100) throw new IOException("Invalid review");
        var values = tokens.Select(t => Token(t!.GetValue<string>())).ToArray(); Schema();
        // Acknowledges ONLY the displayed intents. Newer unresolved sends are retained.
        Sql($"UPDATE privatechat_send_attempts SET state='reviewed' WHERE contact_id={contact} AND state='pending' AND token IN ({string.Join(',', values.Select(t => "'" + t + "'"))})");
        return Status(contact);
    }
    public JsonNode Send(long contact, string token, string? text, long? source)
    {
        RequireContact(contact); token = Token(token);
        if (source is <= 0 || (source == null && (string.IsNullOrWhiteSpace(text) || text.Length > 12000 || text.Contains('\0')))) throw new IOException("Invalid message");
        var previous = Rows($"token='{token}' OR " + (source.HasValue ? $"source_id={source.Value}" : "0"));
        if (previous.Count > 0)
        {
            var row = previous[0]!;
            if (row["contact"]!.GetValue<long>() != contact || row["source"]?.GetValue<long>() != source) throw new IOException("Intent mismatch");
            return row.DeepClone();
        }
        if (Rows($"contact_id={contact} AND state='pending'").Count > 0) return Refused("review");
        RequireNetwork();
        var code = command($"/_get code @{contact}");
        if (!PrivacyPolicy.VerifiedContact(code["contact"])) return Refused("verify");
        var info = command($"/_info @{contact}");
        string state = SyncState(info);
        if (info["contact"]?["activeConn"]?["connStatus"]?["type"]?.ToString() != "ready" || state is not ("ok" or "allowed")) return Refused("connection");
        if (source.HasValue)
        {
            var item = command($"/_get item info @{contact} {source.Value}")["chatItem"]?["chatItem"];
            if (item?["meta"]?["itemId"]?.GetValue<long>() != source || !CanRetry(item)) return Refused("state");
            text = item!["content"]!["msgContent"]!["text"]!.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 12000 || text.Contains('\0')) return Refused("state");
        }
        Sql($"INSERT INTO privatechat_send_attempts(token,contact_id,source_id,state) VALUES('{token}',{contact},{(source.HasValue ? source.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")},'pending')");
        try
        {
            var sent = command(PrivacyPolicy.Send(contact, text!));
            long? id = sent["chatItems"]?.AsArray().SingleOrDefault()?["chatItem"]?["meta"]?["itemId"]?.GetValue<long>();
            if (id is > 0)
                Sql($"UPDATE privatechat_send_attempts SET state='submitted',result_id={id} WHERE token='{token}'");
        }
        catch { /* A command error or lost reply may follow a committed send. Retain pending. */ }
        return Rows($"token='{token}'").Single()!.DeepClone();
    }
    public JsonNode Repair(long contact)
    {
        RequireContact(contact); RequireNetwork();
        var info = command($"/_info @{contact}");
        if (!CanRepair(info)) return Refused("state");
        return command($"/_sync @{contact}"); // never force, recreate, delete or silently verify
    }
    private void RequireNetwork()
    {
        var cfg = command("/network")["networkConfig"]!;
        string proxy = cfg["socksProxy"]?.ToString() ?? "";
        if (!proxy.StartsWith("127.0.0.1:", StringComparison.Ordinal) || !int.TryParse(proxy[10..], out int port) || !PrivacyPolicy.IsStrict(cfg, port)) throw new IOException("Tor policy not applied");
    }
    public static bool CanRetry(JsonNode? item) => item?["chatDir"]?["type"]?.ToString() == "directSnd" && item["file"] == null &&
        item["meta"]?["itemDeleted"] == null && item["content"]?["type"]?.ToString() == "sndMsgContent" && item["content"]?["msgContent"]?["type"]?.ToString() == "text" &&
        item["meta"]?["itemStatus"]?["type"]?.ToString() is "sndErrorAuth" or "sndError";
    public static string SyncState(JsonNode info) => info["connectionStats_"]?["ratchetSyncState"]?.ToString() ?? "unknown";
    public static bool CanRepair(JsonNode info) => info["connectionStats_"]?["ratchetSyncSupported"]?.GetValue<bool>() == true && SyncState(info) is "allowed" or "required";
    private static JsonObject Refused(string reason) => new() { ["state"] = "refused", ["reason"] = reason };
    private static void RequireContact(long contact) { if (contact <= 0) throw new IOException("Invalid contact"); }
    private static string Token(string token) => token.Length == 32 && token.All(char.IsAsciiHexDigit) ? token : throw new IOException("Invalid intent");
}
