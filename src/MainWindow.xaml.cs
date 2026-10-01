using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PrivateChat;

public partial class MainWindow : Window
{
    private readonly string dataRoot;
    private CoreClient? core;
    private TorService? tor;
    private long userId;
    private int epoch;
    private bool online, busy, refreshing, closing, updatingContacts;
    private DateTime lastInput = DateTime.UtcNow;
    private DateTime networkStarted;
    private ContactRow? selected;
    private string modalMode = "invite";
    private long? verificationContact;
    private int modalVersion;
    private bool captureProtected;
    private readonly PrivateClipboard privateClipboard = new();
    private readonly ConversationState conversation = new();
    private readonly HistoryPage history = new();
    private bool historyLoading;
    private bool scrollToLatest = true;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Brush muted = new SolidColorBrush(Color.FromRgb(104,110,121));
    private readonly Brush accent = new SolidColorBrush(Color.FromRgb(0,47,167));
    private bool IsNew => !File.Exists(Path.Combine(dataRoot, "chat_chat.db"));

    public MainWindow(string dataRoot)
    {
        InitializeComponent(); this.dataRoot = dataRoot;
        privateClipboard.Attach(MessageInput); privateClipboard.Attach(ModalInput); privateClipboard.Attach(RelayInput);
        SourceInitialized += (_, _) =>
        {
            int value = 0; var handle = new WindowInteropHelper(this).Handle;
            DwmSetWindowAttribute(handle, 20, ref value, 4);
            captureProtected = SetWindowDisplayAffinity(handle, 0x11);
            if (!captureProtected) ShowUnlockError("Windows 屏幕捕获保护未能开启，暂不解锁。请更新系统后重试。");
        };
        PreviewKeyDown += (_, _) => lastInput = DateTime.UtcNow;
        PreviewMouseDown += (_, _) => lastInput = DateTime.UtcNow;
        PreviewMouseMove += (_, _) => lastInput = DateTime.UtcNow;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        timer.Tick += async (_, _) =>
        {
            if (core == null) return;
            if (DateTime.UtcNow - lastInput > TimeSpan.FromMinutes(5)) { Lock(); return; }
            if (!online && tor != null && DateTime.UtcNow - networkStarted > TimeSpan.FromSeconds(90))
                FooterStatus.Text = "Tor 连接仍未完成。可切换网桥后重新连接；不会自动改为直连。";
            if (!busy && online) await Refresh(true);
        };
        timer.Start(); SetUnlockText();
        Closed += (_, _) => { closing = true; timer.Stop(); SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged; Lock(); privateClipboard.Dispose(); };
        Loaded += (_, _) => Password.Focus();
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e) { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff) Dispatcher.BeginInvoke(Lock); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Resume is also handled because Windows may not deliver every suspend event.
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { if (!closing) Lock(); }));
    }
    private void SetUnlockText()
    {
        UnlockHeading.Text = IsNew ? "创建本地资料库" : "解锁本地资料库";
        UnlockButton.Content = IsNew ? "创建并解锁" : "解锁";
        UnlockHint.Text = IsNew ? "设置至少 12 个字符的口令。建议使用多个随机词组成的长口令。" : "输入这台电脑上资料库的口令。聊天记录将在本机解密。";
        ConfirmPanel.Visibility = IsNew ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (busy || core != null || !captureProtected) return;
        string password = Password.Password;
        if (password.Length < 12) { ShowUnlockError("口令至少需要 12 个字符。"); return; }
        if (IsNew && password != ConfirmPassword.Password) { ShowUnlockError("两次输入的口令不一致。"); return; }
        int turn = ++epoch; busy = true; UnlockButton.IsEnabled = false; UnlockError.Visibility = Visibility.Collapsed;
        try
        {
            FooterStatus.Text = "正在打开加密数据库…";
            core = new CoreClient();
            var current = core;
            current.Exited += () => Dispatcher.BeginInvoke(() => { if (epoch == turn) { Lock(); ShowUnlockError("加密核心已停止。请重新解锁。"); } });
            current.Event += value => Dispatcher.BeginInvoke(async () =>
            {
                if (turn != epoch || core == null) return;
                if (value["error"] != null) { FooterStatus.Text = "网络操作尚未完成，消息将等待重连。"; return; }
                string type = value["result"]?["type"]?.ToString() ?? "";
                if (!history.IsLatest && selected != null && type.Contains("chatItem", StringComparison.OrdinalIgnoreCase))
                    HistoryStatus.Text = "会话有更新 · 回到最新查看";
                if (type.Contains("chatItem", StringComparison.OrdinalIgnoreCase) || type.Contains("contact", StringComparison.OrdinalIgnoreCase)) await Refresh(true);
            });
            var migration = await current.Init(Path.Combine(dataRoot, "chat"), password);
            if (turn != epoch) return;
            password = ""; Password.Clear(); ConfirmPassword.Clear();
            if (migration["type"]?.ToString() != "ok") throw new InvalidDataException("unlock");
            var user = await current.Command("/u");
            if (user["error"]?["errorType"]?["type"]?.ToString() == "noActiveUser")
                user = await current.Command("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            if (turn != epoch) return;
            userId = user["result"]?["user"]?["userId"]?.GetValue<long>() ?? throw new IOException("Missing user");
            UnlockView.Visibility = Visibility.Collapsed; ChatView.Visibility = Visibility.Visible; LockButton.Visibility = Visibility.Visible;
            lastInput = DateTime.UtcNow;
            await StartNetwork();
            await Refresh(true);
        }
        catch (InvalidDataException) { if (turn == epoch) { Lock(); ShowUnlockError("口令不正确，或资料库无法打开。已有数据已保留。"); } }
        catch { if (turn == epoch) { Lock(); ShowUnlockError("启动未完成。请确认程序的 runtime 文件夹完整，然后重试。"); } }
        finally { password = ""; if (turn == epoch) { Password.Clear(); ConfirmPassword.Clear(); busy = false; UnlockButton.IsEnabled = true; UpdateActions(); } }
    }
    private void ShowUnlockError(string text) { UnlockError.Text = text; UnlockError.Visibility = Visibility.Visible; }
    private void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Unlock_Click(sender, e); }

    private async Task StartNetwork()
    {
        var current = core ?? throw new InvalidOperationException();
        int turn = epoch;
        online = false; UpdateActions();
        networkStarted = DateTime.UtcNow;
        tor?.Dispose(); tor = new TorService(); var service = tor;
        service.Guard(current);
        var raw = await current.Result("/network");
        var cfg = PrivacyPolicy.Configure(raw["networkConfig"]!, service.Port);
        cfg["hostMode"] = (RelayEntry.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "onionViaSocks";
        await current.Result("/_network " + cfg.ToJsonString());
        var readBack = await current.Result("/network");
        if (!PrivacyPolicy.IsStrict(readBack["networkConfig"]!, service.Port)) throw new IOException("Unsafe network config");
        // Start the core against an as-yet unopened SOCKS port. Apply local privacy
        // preferences before starting Tor, so no relay can be contacted first.
        await current.Result("/_start main=on snd_files=off");
        await current.Result("/_set receipts contacts " + userId + " off clear_overrides=on");
        await current.Result("/_set accept member contacts " + userId + " off");
        await current.Result("/_files_encrypt on");
        service.Changed += progress => Dispatcher.BeginInvoke(async () =>
        {
            if (turn != epoch || tor != service || core != current) return;
            NetworkLabel.Text = "Tor 连接中 · " + progress + "%";
            if (progress == 100 && !online)
            {
                try
                {
                    await current.Result("/_start main=on snd_files=off");
                    if (turn != epoch || tor != service || !service.Ready) return;
                    online = true; NetworkLabel.Text = "Tor 通道已建立"; NetworkLabel.Foreground = accent;
                    ConnectionHelp.Text = "Tor 通道已建立。点击左侧“添加联系人”，分享或粘贴一次性邀请；联系人连接可能仍需等待。";
                    FooterStatus.Text = "Tor 通道已建立 · 联系人连接单独隔离";
                    UpdateActions(); await Refresh(true);
                }
                catch { if (turn == epoch) { Lock(); ShowUnlockError("加密核心启动未完成，已锁定。请重新解锁。"); } }
            }
        });
        service.Exited += () => Dispatcher.BeginInvoke(() =>
        {
            if (turn != epoch || tor != service) return;
            Lock(); ShowUnlockError("Tor 意外停止，聊天核心已结束并锁定。请重新解锁。");
        });
        string mode = (TransportMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "direct";
        NetworkLabel.Text = "Tor 连接中 · 0%"; NetworkLabel.Foreground = muted;
        FooterStatus.Text = cfg["hostMode"]?.ToString() == "public" ? "公共域名仍经 Tor；Tor 出口可看到中继地址。正在连接…" : "正在建立 Tor 通道；邀请超时可切换中继入口后重新连接。";
        await Task.Run(() => service.Start(dataRoot, mode));
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || busy) return;
        int turn = epoch;
        busy = true; online = false; UpdateActions();
        try { await core.Result("/_stop"); await StartNetwork(); }
        catch { if (turn == epoch) { Lock(); ShowUnlockError("重连未完成，已锁定。请重新解锁。"); } }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private void Lock_Click(object sender, RoutedEventArgs e) => Lock();
    internal void EmergencyLock() => Lock();
    private void Lock()
    {
        CloseRelays();
        conversation.Invalidate(); history.Reset(); historyLoading = false; scrollToLatest = true;
        HistoryStatus.Text = "";
        ++epoch; ++modalVersion; verificationContact = null; userId = 0;
        online = false; tor?.Dispose(); tor = null; core?.Dispose(); core = null; busy = false;
        ContactsList.ItemsSource = null; MessagesPanel.Children.Clear(); selected = null; MessageInput.Clear();
        EmptyConversation.Visibility = Visibility.Visible; VerifyButton.Visibility = Visibility.Collapsed;
        ChatTitle.Text = "你的私人会话"; ChatDetail.Text = "选择联系人，开始聊天。";
        Password.Clear(); ConfirmPassword.Clear(); ModalInput.Clear(); ModalStatus.Text = ""; Modal.Visibility = Visibility.Collapsed;
        privateClipboard.ClearOwned(); ChatView.Visibility = Visibility.Collapsed; UnlockView.Visibility = Visibility.Visible; LockButton.Visibility = Visibility.Collapsed;
        NetworkLabel.Text = "已锁定 · 未联网"; NetworkLabel.Foreground = muted; FooterStatus.Text = "已结束聊天核心和 Tor 进程";
        SetUnlockText(); UnlockButton.IsEnabled = true; UpdateActions(); if (!closing) Password.Focus();
    }
    private bool CanNetwork => online && tor?.Ready == true && core != null;
    private void UpdateActions()
    {
        AddButton.IsEnabled = CanNetwork && !busy; SendButton.IsEnabled = CanNetwork && selected is { Ready: true, Verified: true } && !busy;
        MessageInput.IsEnabled = core != null && selected != null; ReconnectButton.IsEnabled = core != null && !busy;
        ModalAction.IsEnabled = !busy && (modalMode is "verify" or "verify-peer" || CanNetwork);
        ModalAux.IsEnabled = !busy && (modalMode == "verify" || modalMode == "generated" || CanNetwork);
        HistoryBar.Visibility = selected != null ? Visibility.Visible : Visibility.Collapsed;
        OlderButton.IsEnabled = core != null && !historyLoading && history.HasOlder;
        NewerButton.IsEnabled = core != null && !historyLoading && history.HasNewer;
        LatestButton.IsEnabled = core != null && selected != null && !historyLoading;
        RelaySettingsButton.IsEnabled = core != null && !busy;
        VerifyButton.IsEnabled = core != null && selected != null && !busy;
        UpdateRelayActions();
    }
    private async Task Refresh(bool messages)
    {
        if (refreshing || core == null) return;
        refreshing = true; int turn = epoch; var current = core;
        try
        {
            var result = await current.Result("/_get chats " + userId + " pcc=on");
            if (turn != epoch) return;
            var rows = new List<ContactRow>();
            int pendingCount = 0;
            foreach (var chat in result["chats"]!.AsArray())
            {
                var info = chat!["chatInfo"]!;
                if (info["type"]?.ToString() == "contactConnection") { pendingCount++; continue; }
                if (info["type"]?.ToString() != "direct") continue;
                var contact = info["contact"]!;
                if (contact["activeConn"] == null) continue; // exclude upstream's unconnected support contact card
                long id = contact["contactId"]!.GetValue<long>();
                string name = contact["profile"]?["displayName"]?.ToString() ?? contact["localDisplayName"]!.ToString();
                bool verified = contact["activeConn"]?["connectionCode"] != null;
                bool ready = contact["activeConn"]?["connStatus"]?["type"]?.ToString() == "ready";
                rows.Add(new ContactRow(id, name, ready ? (verified ? "已核验安全码" : "安全码待核验") : "正在完成连接", verified, ready));
            }
            long? keep = selected?.Id;
            bool changed = ContactsList.ItemsSource is not List<ContactRow> previous || !previous.SequenceEqual(rows);
            if (changed)
            {
                updatingContacts = true;
                try { ContactsList.ItemsSource = rows; ContactsList.SelectedItem = rows.FirstOrDefault(r => r.Id == keep); }
                finally { updatingContacts = false; }
                Contact_Selected(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
            }
            NoContacts.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoContacts.Text = pendingCount > 0 ? $"有 {pendingCount} 个邀请正在等待连接。" : "还没有联系人。\n通过一次性邀请建立连接。";
            if (messages && selected != null) await RefreshMessages();
        }
        catch { if (turn == epoch) FooterStatus.Text = "暂时无法刷新会话，请稍后重试。"; }
        finally { refreshing = false; }
    }
    private async void Contact_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingContacts) return;
        long? previousId = selected?.Id;
        selected = ContactsList.SelectedItem as ContactRow;
        if (previousId != selected?.Id)
        {
            conversation.Invalidate(); history.Reset(); historyLoading = false; scrollToLatest = true;
            HistoryStatus.Text = "正在读取消息…";
            MessagesPanel.Children.Clear(); MessageInput.Clear(); CloseModal();
        }
        if (selected == null) { MessagesPanel.Children.Clear(); ChatTitle.Text = "你的私人会话"; ChatDetail.Text = "选择联系人，开始聊天。"; EmptyConversation.Visibility = Visibility.Visible; VerifyButton.Visibility = Visibility.Collapsed; UpdateActions(); return; }
        ChatTitle.Text = selected.Name; ChatDetail.Text = selected.Detail + (selected.Verified ? " · Ctrl+Enter 发送" : " · 核验后才可发送");
        VerifyButton.Visibility = Visibility.Visible; EmptyConversation.Visibility = Visibility.Collapsed;
        UpdateActions(); await RefreshMessages();
    }
    private async Task RefreshMessages()
    {
        // A refresh may update the latest page; it must never replace history being read.
        if (!history.IsLatest || historyLoading) return;
        await LoadMessages(HistoryDirection.Latest, false);
    }
    private async void Older_Click(object sender, RoutedEventArgs e) { if (history.HasOlder) await LoadMessages(HistoryDirection.Older, true); }
    private async void Newer_Click(object sender, RoutedEventArgs e) { if (history.HasNewer) await LoadMessages(HistoryDirection.Newer, true); }
    private async void Latest_Click(object sender, RoutedEventArgs e) => await LoadMessages(HistoryDirection.Latest, true);
    private async Task LoadMessages(HistoryDirection direction, bool navigate)
    {
        var contact = selected; var current = core; int turn = epoch;
        if (contact == null || current == null || historyLoading) return;
        long request = conversation.Begin();
        historyLoading = true; UpdateActions();
        try
        {
            var result = await current.Result($"/_get chat @{contact.Id} " + history.Query(direction));
            if (turn != epoch || selected?.Id != contact.Id || !conversation.IsCurrent(request)) return;
            var page = history.Apply(result["chat"]?["chatItems"]?.AsArray() ?? new JsonArray(), direction);
            HistoryStatus.Text = history.Count == 0 ? "还没有消息" : $"{(history.IsLatest ? "最新消息" : "历史消息")} · 本页 {history.Count} 条";
            if (page.Length == 0 && direction != HistoryDirection.Latest) return;
            bool followLatest = direction == HistoryDirection.Older || (direction == HistoryDirection.Latest && (navigate || scrollToLatest || MessageScroll.ScrollableHeight - MessageScroll.VerticalOffset < 32));
            double previousOffset = MessageScroll.VerticalOffset;
            scrollToLatest = false;
            var anchor = MessageViewport.Capture(MessagesPanel, MessageScroll);
            MessagesPanel.Children.Clear();
            foreach (var item in page)
            {
                var meta = item!["meta"]!;
                string text = meta["itemText"]?.ToString() ?? "";
                bool sent = item["chatDir"]?["type"]?.ToString() == "directSnd";
                string type = item["content"]?["type"]?.ToString() ?? "";
                if (type is not ("sndMsgContent" or "rcvMsgContent"))
                {
                    MessagesPanel.Children.Add(new TextBlock { Tag = meta["itemId"]?.GetValue<long>(), Text = text, Foreground = muted, FontSize = 11, Margin = new Thickness(0,8,0,14) }); continue;
                }
                string label = sent ? ConversationState.DeliveryLabel(meta["itemStatus"]) : "收到";
                if (DateTime.TryParse(meta["itemTs"]?.ToString(), out var date)) label = date.ToLocalTime().ToString("HH:mm") + " · " + label;
                MessagesPanel.Children.Add(CreateMessageBubble(text, label, sent, meta["itemId"]?.GetValue<long>()));
            }
            MessageScroll.UpdateLayout();
            if (followLatest) MessageScroll.ScrollToEnd();
            else if (direction == HistoryDirection.Newer) MessageScroll.ScrollToTop();
            else MessageViewport.Restore(MessagesPanel, MessageScroll, anchor, previousOffset);
        }
        catch { if (turn == epoch && selected?.Id == contact.Id && conversation.IsCurrent(request)) { HistoryStatus.Text = "读取失败，可重试"; FooterStatus.Text = "暂时无法读取这段会话，已有记录未删除。"; } }
        finally { if (conversation.IsCurrent(request)) { historyLoading = false; UpdateActions(); } }
    }
    private static Border CreateMessageBubble(string text, string label, bool sent, long? itemId)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(40,44,52)), TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 26 });
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(104,110,121)), FontSize = 11, Margin = new Thickness(0,9,0,0) });
        return new Border
        {
            Tag = itemId, Child = panel, MaxWidth = 500, Padding = new Thickness(16,12,16,12),
            Margin = new Thickness(sent ? 40 : 0, 0, sent ? 0 : 40, 16), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(sent ? Color.FromRgb(232,237,247) : Color.FromRgb(241,242,244)),
            HorizontalAlignment = sent ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var current = core; var contact = selected; int turn = epoch; string text = MessageInput.Text;
        if (!CanNetwork || current == null || contact is not { Ready: true, Verified: true } || busy || string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 12000) { FooterStatus.Text = "第一版单条消息最多 12,000 个字符。"; return; }
        busy = true; UpdateActions();
        try
        {
            // Ask the core to recompute the code; it revokes stale verification after key changes.
            var code = await current.Result($"/_get code @{contact.Id}");
            if (turn != epoch || !CanNetwork) return;
            if (!PrivacyPolicy.VerifiedContact(code["contact"])) { FooterStatus.Text = "安全码未核验或已变化，请先重新核验。"; await Refresh(true); return; }
            await current.Result(PrivacyPolicy.Send(contact.Id, text));
            if (turn == epoch) { if (selected?.Id == contact.Id && MessageInput.Text == text) MessageInput.Clear(); await RefreshMessages(); FooterStatus.Text = history.IsLatest ? "消息已交给加密核心；送达状态以会话中显示为准。" : "消息已交给加密核心；点击“回到最新”查看发送状态。"; }
        }
        catch { if (turn == epoch) FooterStatus.Text = "发送尚未确认，请先检查会话记录，避免重复发送。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private void Message_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Send_Click(sender, e); } }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNetwork) return;
        ++modalVersion; verificationContact = null;
        modalMode = "invite"; ModalTitle.Text = "添加联系人";
        ModalHint.Text = "粘贴对方的一次性完整邀请链接，或生成你自己的邀请。连接时会使用独立的随机昵称。";
        ModalInput.Clear(); ModalInput.IsReadOnly = false; ModalInput.Visibility = Visibility.Visible; ModalStatus.Text = "";
        ModalAction.Content = "接受邀请"; ModalAction.Visibility = Visibility.Visible; ModalAux.Content = "生成邀请";
        Modal.Visibility = Visibility.Visible; UpdateActions(); ModalInput.Focus();
    }
    private async void ModalAux_Click(object sender, RoutedEventArgs e)
    {
        if (modalMode is "generated" or "verify") { ModalStatus.Text = privateClipboard.Copy(ModalInput.Text) ? "已复制；30 秒后尝试清除本次复制的内容。" : "复制失败，剪贴板暂不可用，请重试。"; return; }
        if (!CanNetwork || core == null || busy) return;
        int turn = epoch, version = modalVersion; busy = true; UpdateActions(); ModalStatus.Text = "正在通过 Tor 创建邀请…";
        try
        {
            var response = await core.Result($"/_connect {userId} incognito=on");
            if (turn != epoch || version != modalVersion) return;
            string link = response["connLinkInvitation"]?["connFullLink"]?.ToString() ?? throw new IOException("No invitation link");
            modalMode = "generated"; ModalInput.Text = link; ModalInput.IsReadOnly = true;
            ModalAux.Content = "复制邀请"; ModalAction.Visibility = Visibility.Collapsed;
            ModalHint.Text = "将完整链接交给指定联系人。建立连接后，请通过可信渠道核对安全码。";
            ModalStatus.Text = "这是一条真实邀请，等待对方接受。";
        }
        catch { if (turn == epoch && version == modalVersion) ModalStatus.Text = "邀请未能生成。请检查 Tor 与中继连接后重试。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private async void ModalAction_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || busy) return;
        int turn = epoch, version = modalVersion;
        string input = ModalInput.Text.Trim();
        if (modalMode == "invite" && !PrivacyPolicy.ValidInvite(input)) { ModalStatus.Text = "请使用一次性完整邀请链接（simplex:/invitation#…）。"; return; }
        if (modalMode == "verify")
        {
            // Verification requires the independently obtained peer code, never a one-click self-approval.
            ModalInput.Clear(); ModalInput.IsReadOnly = false; modalMode = "verify-peer";
            ModalHint.Text = "粘贴通过见面或其他可信渠道获得的对方安全码，然后核验。";
            ModalAction.Content = "核验"; ModalAux.Visibility = Visibility.Collapsed; ModalStatus.Text = ""; return;
        }
        if (modalMode == "verify-peer" && (input.Length < 20 || input.Any(c => !char.IsAsciiDigit(c) && c != ' '))) { ModalStatus.Text = "请输入对方提供的完整数字安全码。"; return; }
        if (!CanNetwork && modalMode == "invite") return;
        busy = true; UpdateActions();
        try
        {
            if (modalMode == "verify-peer")
            {
                if (verificationContact == null || verificationContact != selected?.Id) { CloseModal(); return; }
                var response = await core.Result($"/_verify code @{verificationContact.Value} {input}");
                if (turn != epoch || version != modalVersion) return;
                ModalStatus.Text = response["verified"]?.GetValue<bool>() == true ? "安全码一致，已完成核验。" : "安全码不一致。请停止发送敏感内容并重新确认对方身份。";
                await Refresh(true);
            }
            else
            {
                await core.Result($"/_connect {userId} incognito=on {input}");
                if (turn != epoch || version != modalVersion) return;
                ModalStatus.Text = "已提交连接请求，等待双方完成握手。"; ModalInput.Clear(); await Refresh(true);
            }
        }
        catch { if (turn == epoch && version == modalVersion) ModalStatus.Text = "操作未完成，请检查输入或网络连接。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || selected == null || busy) return;
        int turn = epoch, version = ++modalVersion; long contactId = selected.Id;
        try
        {
            var response = await core.Result($"/_get code @{contactId}");
            if (turn != epoch || version != modalVersion || selected?.Id != contactId) return;
            verificationContact = contactId;
            modalMode = "verify"; ModalTitle.Text = "核验联系人";
            ModalHint.Text = "这是本端的连接安全码。双方需要通过可信渠道核对同一段数字。";
            ModalInput.Text = response["connectionCode"]!.ToString(); ModalInput.IsReadOnly = true; ModalStatus.Text = "";
            ModalAction.Content = "输入对方安全码"; ModalAction.Visibility = Visibility.Visible;
            ModalAux.Content = "复制本端安全码"; ModalAux.Visibility = Visibility.Visible; Modal.Visibility = Visibility.Visible; UpdateActions();
        }
        catch { if (turn == epoch && version == modalVersion) FooterStatus.Text = "连接尚未就绪，暂时无法读取安全码。"; }
    }
    private void CloseModal() { ++modalVersion; verificationContact = null; Modal.Visibility = Visibility.Collapsed; ModalInput.Clear(); ModalAux.Visibility = Visibility.Visible; }
    private void ModalClose_Click(object sender, RoutedEventArgs e) => CloseModal();
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. 创建本地资料库，设置至少 12 个字符的口令。遗忘口令无法找回。\n\n" +
        "2. 解锁后等待 Tor 通道建立。添加联系人：一方生成邀请，另一方粘贴并接受。\n\n" +
        "3. 通过另一个可信渠道比对双方安全码，核验后才能发送文字。Ctrl+Enter 发送，Enter 换行。\n\n" +
        "4. 邀请持续超时，可选“公共域名（经 Tor）”后重新连接；该选项仍经过 Tor。\n\n" +
        "5. 锁定、Windows 锁屏、睡眠/唤醒或闲置 5 分钟会断开连接，重新解锁后接收待收消息。\n\n" +
        "6. 消息按每页最多 100 条显示。使用“更早消息”和“较新消息”翻页；“回到最新”返回当前对话末尾。浏览历史时，收到消息不会切换当前页。\n\n" +
        "Private Chat 0.8.1 · 本地测试版\n尚未完成跨设备验证与独立安全审计。当前支持一对一文字聊天。",
        "使用帮助", MessageBoxButton.OK, MessageBoxImage.Information);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

public sealed record ContactRow(long Id, string Name, string Detail, bool Verified, bool Ready);
