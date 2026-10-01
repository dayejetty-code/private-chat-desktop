using System.Text.Json.Nodes;

namespace PrivateChat;

// Only the latest request in the current conversation may update its view.
internal sealed class ConversationState
{
    private long revision;
    public long Begin() => ++revision;
    public void Invalidate() => ++revision;
    public bool IsCurrent(long request) => request == revision;

    public static string DeliveryLabel(JsonNode? status) => status?["type"]?.ToString() switch
    {
        "sndNew" => "等待发送",
        "sndSent" when status?["sndProgress"]?.ToString() == "partial" => "部分连接已发送",
        "sndSent" => "已发送至中继",
        "sndRcvd" when status?["msgRcptStatus"]?.ToString() == "ok" && status?["sndProgress"]?.ToString() == "partial" => "部分连接已送达",
        "sndRcvd" when status?["msgRcptStatus"]?.ToString() == "ok" => "已送达",
        "sndRcvd" => "送达回执异常，请核验连接",
        "sndErrorAuth" => "发送失败：连接认证错误",
        "sndError" => "发送失败，请检查连接",
        "sndWarning" => "发送出现警告，尚未确认送达",
        _ => "发送状态未知"
    };
}
