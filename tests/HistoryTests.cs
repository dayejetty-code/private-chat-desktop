using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace PrivateChat;

internal static class HistoryTests
{
    private static JsonNode Item(long id) => new JsonObject { ["meta"] = new JsonObject { ["itemId"] = id } };
    private static JsonArray Items(int first, int count) => new(Enumerable.Range(first, count).Select(i => Item(i)).ToArray());
    public static void StateChecks(Action<bool, string> check)
    {
        var page = new HistoryPage();
        var latest = page.Apply(Items(125,101), HistoryDirection.Latest);
        check(latest.Length == 100 && page.First == 126 && page.Last == 225 && page.HasOlder && !page.HasNewer, "History latest page uses one extra row to detect older items");
        check(page.Query(HistoryDirection.Older) == "before=126 count=101", "History cursor queries older than the first displayed item");
        var older = page.Apply(Items(25,101), HistoryDirection.Older);
        check(older.Length == 100 && page.First == 26 && page.Last == 125 && !page.IsLatest, "Older page excludes both cursor and sentinel without a gap");
        page.Apply(Items(1,25), HistoryDirection.Older);
        check(!page.HasOlder && page.HasNewer && page.First == 1, "Oldest page disables older navigation");
        page.Apply(Items(26,101), HistoryDirection.Newer);
        check(page.First == 26 && page.Last == 125 && page.HasNewer, "Forward pagination trims the newest sentinel instead of skipping items");
        page.Apply(Items(126,100), HistoryDirection.Newer);
        check(page.IsLatest && !page.HasNewer && page.Last == 225, "Forward pagination detects return to latest page");
        page.Apply(new JsonArray(), HistoryDirection.Newer);
        check(page.First == 126 && page.Count == 100, "Empty navigation result keeps current page intact");
        page.Reset();
        check(page.First == null && page.Last == null && page.Count == 0 && page.IsLatest, "History reset removes all conversation cursors");
        page.Apply(new JsonArray(), HistoryDirection.Latest);
        check(!page.HasOlder && !page.HasNewer && page.Count == 0, "Empty conversation has no history navigation");
    }
    public static void ScrollChecks(Action<bool,string> check)
    {
        var panel = new StackPanel();
        void Fill(int first, int count) { panel.Children.Clear(); for (int i=first;i<first+count;i++) panel.Children.Add(new Border { Tag=(long)i, Height=35, Child=new TextBlock { Text="Synthetic " + i } }); }
        var scroll = new ScrollViewer { Content=panel, Width=400, Height=200 };
        void Layout() { scroll.Measure(new Size(400,200)); scroll.Arrange(new Rect(0,0,400,200)); scroll.UpdateLayout(); }
        Fill(1,30); Layout(); scroll.ScrollToVerticalOffset(350); Layout();
        var anchor = MessageViewport.Capture(panel,scroll);
        check(anchor != null && scroll.VerticalOffset > 0, "Synthetic WPF history viewport is scrolled above latest");
        Fill(2,30); Layout();
        MessageViewport.Restore(panel,scroll,anchor,350); Layout();
        var restored = MessageViewport.Capture(panel,scroll);
        check(restored?.Id == anchor!.Id && Math.Abs(restored.Y-anchor.Y) < 1, "Message anchor preserves reading position when latest-page boundary moves");
        scroll.Content = null;
    }
    public static async Task NativeChecks(CoreClient core, Action<bool,string> check)
    {
        var messages = new JsonArray(Enumerable.Range(1,225).Select(i => (JsonNode)new JsonObject {
            ["msgContent"] = new JsonObject { ["type"]="text", ["text"]=$"Synthetic history {i:D3}" }, ["mentions"]=new JsonObject()
        }).ToArray());
        // Local notes use the same native pagination API, without sending hundreds of test messages to public relays.
        await core.Result("/_create *1 json " + messages.ToJsonString());
        var expected = (await core.Result("/_get chat *1 count=300"))["chat"]!["chatItems"]!.AsArray();
        check(expected.Count == 225, "Native encrypted test store contains 225 synthetic history items");
        var pager = new HistoryPage(); var seen = new List<long>();
        var direction = HistoryDirection.Latest;
        do
        {
            var result = await core.Result("/_get chat *1 " + pager.Query(direction));
            var page = pager.Apply(result["chat"]!["chatItems"]!.AsArray(),direction);
            seen.InsertRange(0,page.Select(i=>i["meta"]!["itemId"]!.GetValue<long>()));
            direction = HistoryDirection.Older;
        } while(pager.HasOlder);
        var ids = expected.Select(i=>i!["meta"]!["itemId"]!.GetValue<long>()).ToArray();
        check(seen.SequenceEqual(ids) && seen.Distinct().Count()==225, "Native backward pagination visits all 225 items once in order");
        var forward = new List<long>(seen.Take(pager.Count));
        while(pager.HasNewer)
        {
            var result=await core.Result("/_get chat *1 " + pager.Query(HistoryDirection.Newer));
            forward.AddRange(pager.Apply(result["chat"]!["chatItems"]!.AsArray(),HistoryDirection.Newer).Select(i=>i["meta"]!["itemId"]!.GetValue<long>()));
        }
        check(forward.SequenceEqual(ids), "Native forward pagination returns to latest without gaps or duplicate items");
    }
}
