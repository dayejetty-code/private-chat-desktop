using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PrivateChat;

public partial class MainWindow
{
    private readonly DispatcherTimer readTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly HashSet<long> unreadItems = new();
    private readonly Dictionary<long, string> retriedItems = new();
    private readonly List<Button> retryButtons = new();
    private string[] unresolvedSends = [];
    private bool readBusy, deliveryUnknown, repairLoading, repairAllowed;
    private int repairVersion;
    private void ShowContactHeading()
    {
        if (selected == null) return;
        ChatTitle.Text = selected.Name;
        ChatDetail.Text = selected.Detail + (selected.Verified ? " · Ctrl+Enter 发送" : " · 核验后才可发送");
    }
    private void ResetConversationTools()
    {
        unreadItems.Clear(); retriedItems.Clear(); retryButtons.Clear(); unresolvedSends = []; deliveryUnknown = false;
        PendingSendNotice.Visibility = Visibility.Collapsed; CloseRepair();
    }
    private void UpdateConversationActions()
    {
        RepairButton.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        RepairButton.IsEnabled = core != null && selected != null && !busy;
        RepairSyncButton.IsEnabled = CanNetwork && !busy && !repairLoading && repairAllowed;
        RepairNetworkButton.IsEnabled = core != null && !busy;
        RepairRefreshButton.IsEnabled = core != null && !busy && !repairLoading;
        ReviewSendButton.IsEnabled = core != null && !busy && unresolvedSends.Length > 0;
        SendButton.IsEnabled &= !deliveryUnknown;
        foreach (var button in retryButtons) button.IsEnabled = CanNetwork && selected is { Ready: true, Verified: true } && !busy && !deliveryUnknown;
    }
    internal static bool ReadAllowed(bool active, bool minimized, bool visible, bool occupied, bool loading, bool covered) => active && !minimized && visible && !occupied && !loading && !covered;
    private bool CanReadVisible() => core != null && selected != null && ReadAllowed(IsActive, WindowState == WindowState.Minimized, ChatView.IsVisible, busy, historyLoading,
        Modal.Visibility == Visibility.Visible || RelayPanel.Visibility == Visibility.Visible || CachePanel.Visibility == Visibility.Visible || RepairPanel.Visibility == Visibility.Visible || DeletionPanel.Visibility == Visibility.Visible);
    internal static long[] VisibleUnread(StackPanel panel, ScrollViewer scroll, ISet<long> unread) => panel.Children.OfType<FrameworkElement>()
        .Where(e => e.Tag is long id && unread.Contains(id) && e.ActualHeight > 0)
        .Where(e => { double y = e.TranslatePoint(new Point(0, 0), scroll).Y; return Math.Min(y + e.ActualHeight, scroll.ActualHeight - scroll.Padding.Bottom) - Math.Max(y, scroll.Padding.Top) >= Math.Min(32, e.ActualHeight); })
        .Select(e => (long)e.Tag).ToArray();
    private async Task MarkVisibleRead()
    {
        if (readBusy || !CanReadVisible()) return;
        long[] ids = VisibleUnread(MessagesPanel, MessageScroll, unreadItems);
        if (ids.Length == 0) return;
        var current = core!; long contact = selected!.Id; int turn = epoch;
        readBusy = true;
        try
        {
            // Exact IDs avoid clearing off-screen history or a concurrently arriving message.
            await current.Result($"/_read chat items @{contact} {string.Join(',', ids)}");
            if (epoch != turn || selected?.Id != contact) return;
            foreach (long id in ids) unreadItems.Remove(id);
            await Refresh(false);
        }
        catch { /* Retain unread on failure; no plaintext diagnostic or external receipt. */ }
        finally { readBusy = false; }
    }
    private void ApplyDeliveryStatus(JsonNode value)
    {
        var attempts = value["attempts"]!.AsArray();
        unresolvedSends = attempts.Where(a => a?["state"]?.ToString() == "pending").Select(a => a!["token"]!.GetValue<string>()).ToArray();
        deliveryUnknown = unresolvedSends.Length > 0;
        retriedItems.Clear();
        foreach (var attempt in attempts.Where(a => a?["source"] != null)) retriedItems[attempt!["source"]!.GetValue<long>()] = attempt["state"]!.ToString();
        PendingSendNotice.Visibility = deliveryUnknown ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }
    private async Task RefreshDelivery(long contact)
    {
        var current = core; int turn = epoch;
        if (current == null) return;
        try { var value = await current.MessageStatus(contact); if (turn == epoch && selected?.Id == contact) ApplyDeliveryStatus(value); }
        catch { if (turn == epoch && selected?.Id == contact) { deliveryUnknown = true; FooterStatus.Text = "无法核对发送结果，请重新连接后查看记录。"; UpdateActions(); } }
    }
    private void AddRetryAction(Border bubble, JsonNode item, long contact)
    {
        if (!MessageRecovery.CanRetry(item)) return;
        long id = item["meta"]!["itemId"]!.GetValue<long>();
        var panel = (StackPanel)bubble.Child;
        if (retriedItems.TryGetValue(id, out string? state))
        {
            panel.Children.Add(new TextBlock { Text = state == "submitted" ? "已提交重试 · 查看后续消息" : "已尝试重发 · 请核对后续记录", Foreground = muted, FontSize = 11, Margin = new Thickness(0,8,0,0) }); return;
        }
        var button = new Button { Content = "重试发送", Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,10,0,0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "核对最新状态后重新发送一条文字消息；不会修改原消息" };
        int turn = epoch;
        button.Click += async (_, _) =>
        {
            if (turn != epoch || selected?.Id != contact || !CanNetwork || busy || deliveryUnknown) return;
            busy = true; UpdateActions();
            try
            {
                var result = await core!.SendText(contact, Guid.NewGuid().ToString("N"), source: id);
                if (turn != epoch) return;
                await RefreshDelivery(contact);
                if (selected?.Id == contact) await LoadMessages(HistoryDirection.Latest, true);
                FooterStatus.Text = DeliveryNotice(result);
            }
            catch { if (turn == epoch) { await RefreshDelivery(contact); FooterStatus.Text = "重试结果尚未确认，请先核对会话记录。"; } }
            finally { if (turn == epoch) { busy = false; UpdateActions(); } }
        };
        retryButtons.Add(button); panel.Children.Add(button);
    }
    private static string DeliveryNotice(JsonNode result) => result["state"]?.ToString() switch
    {
        "submitted" => "已交给加密核心，送达状态以会话中显示为准。",
        "refused" => result["reason"]?.ToString() switch
        {
            "verify" => "安全码未核验或已变化，请先重新核验。",
            "connection" => "联系人连接未就绪，请打开“连接修复”查看状态。",
            "review" => "还有结果未知的发送，请先核对会话记录。",
            _ => "消息状态已变化，未重复发送。请刷新查看最新状态。"
        },
        _ => "发送结果未知，请检查记录或向对方确认；不会自动重复发送。"
    };
    private async void ReviewSend_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || selected == null || busy || unresolvedSends.Length == 0) return;
        var current = core; long contact = selected.Id; int turn = epoch; string[] tokens = unresolvedSends.Take(100).ToArray();
        if (MessageBox.Show(this, "请先查看最新记录，必要时向对方确认是否收到。\n\n继续只会解除发送限制，不会重发任何内容。若记录仍无法确定，再次发送可能重复。", "已核对发送结果", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        if (turn != epoch || selected?.Id != contact || busy) return;
        busy = true; UpdateActions();
        try { var result = await current.ReviewMessages(contact, tokens); if (turn == epoch && selected?.Id == contact) { ApplyDeliveryStatus(result); FooterStatus.Text = "已记录本次核对，没有重发消息。"; } }
        catch { if (turn == epoch) FooterStatus.Text = "未能记录核对结果，请稍后重试。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || selected == null || busy) return;
        RepairPanel.Visibility = Visibility.Visible; RepairStatus.Text = "正在检查连接…"; await RefreshRepair();
    }
    private void CloseRepair() { ++repairVersion; repairLoading = false; repairAllowed = false; RepairPanel.Visibility = Visibility.Collapsed; RepairStatus.Text = ""; }
    private void RepairClose_Click(object sender, RoutedEventArgs e) => CloseRepair();
    private async void RepairRefresh_Click(object sender, RoutedEventArgs e) => await RefreshRepair();
    private async Task RefreshRepair()
    {
        var current = core; var contact = selected; int turn = epoch, version = repairVersion;
        if (current == null || contact == null || repairLoading || RepairPanel.Visibility != Visibility.Visible) return;
        repairLoading = true; repairAllowed = false; UpdateActions();
        try
        {
            var info = await current.Result($"/_info @{contact.Id}");
            if (turn != epoch || version != repairVersion || selected?.Id != contact.Id) return;
            repairAllowed = MessageRecovery.CanRepair(info);
            RepairStatus.Text = (CanNetwork ? "Tor 通道已建立。\n\n" : "Tor 通道尚未就绪。可先重新连接 Tor。\n\n") + (MessageRecovery.SyncState(info) switch
            {
                "ok" => "加密连接状态正常。消息仍等待时，可重连 Tor 或等待对方上线。",
                "allowed" => "核心检测到连接异常，允许尝试修复加密连接。",
                "required" => "加密连接失步，需要修复；完成前无法正常发送。",
                "started" or "agreed" => "连接修复进行中，正在等待对方完成同步。请让双方保持在线。",
                _ => "暂时无法确认加密连接状态，请稍后刷新。"
            });
        }
        catch { if (turn == epoch && version == repairVersion) RepairStatus.Text = "无法读取连接状态，请重连后再检查。"; }
        finally { if (turn == epoch && version == repairVersion) { repairLoading = false; UpdateActions(); } }
    }
    private async void RepairSync_Click(object sender, RoutedEventArgs e)
    {
        var current = core; var contact = selected; int turn = epoch, version = repairVersion;
        if (current == null || contact == null || busy || !CanNetwork || !repairAllowed) return;
        busy = true; UpdateActions();
        try
        {
            var result = await current.RepairConnection(contact.Id);
            if (turn != epoch || version != repairVersion) return;
            RepairStatus.Text = result["state"]?.ToString() == "refused" ? "连接状态已变化，请刷新；没有强制修复。" : "已请求修复，等待对方同步。完成后请再次核对安全码。";
            await current.Result($"/_get code @{contact.Id}"); // Revoke stale verification if the native key changed.
            await Refresh(true);
        }
        catch { if (turn == epoch && version == repairVersion) RepairStatus.Text = "修复尚未确认，请刷新状态；不会重复强制修复。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private void RepairNetwork_Click(object sender, RoutedEventArgs e) { CloseRepair(); Reconnect_Click(sender, e); }
}
