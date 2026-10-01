using System.Text.Json.Nodes;

namespace PrivateChat;

internal enum HistoryDirection { Latest, Older, Newer }

// Cursor metadata only: decrypted message bodies stay in the current view/request.
internal sealed class HistoryPage
{
    public const int Size = 100;
    public long? First { get; private set; }
    public long? Last { get; private set; }
    public bool HasOlder { get; private set; }
    public bool HasNewer { get; private set; }
    public bool IsLatest { get; private set; } = true;
    public int Count { get; private set; }
    public void Reset() { First = Last = null; HasOlder = HasNewer = false; IsLatest = true; Count = 0; }
    public string Query(HistoryDirection direction) => direction switch
    {
        HistoryDirection.Older when First != null => $"before={First} count={Size + 1}",
        HistoryDirection.Newer when Last != null => $"after={Last} count={Size + 1}",
        HistoryDirection.Latest => $"count={Size + 1}",
        _ => throw new InvalidOperationException("No history cursor")
    };
    public JsonNode[] Apply(JsonArray data, HistoryDirection direction)
    {
        var page = data.Where(x => x != null).Cast<JsonNode>().ToArray();
        // Do not erase the current view if an empty navigation page is returned.
        if (page.Length == 0 && direction != HistoryDirection.Latest)
        {
            if (direction == HistoryDirection.Older) HasOlder = false;
            else { HasNewer = false; IsLatest = true; }
            return [];
        }
        bool extra = page.Length > Size;
        page = direction == HistoryDirection.Newer ? page.Take(Size).ToArray() : page.TakeLast(Size).ToArray();
        First = page.FirstOrDefault()?["meta"]?["itemId"]?.GetValue<long>();
        Last = page.LastOrDefault()?["meta"]?["itemId"]?.GetValue<long>();
        Count = page.Length;
        HasOlder = direction == HistoryDirection.Newer || extra;
        HasNewer = direction == HistoryDirection.Older || (direction == HistoryDirection.Newer && extra);
        IsLatest = !HasNewer;
        return page;
    }
}
