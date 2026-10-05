using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrivateChat;

internal static class ConversationTests
{
    private static JsonNode FailedItem(string state = "sndErrorAuth") => JsonNode.Parse("{\"chatDir\":{\"type\":\"directSnd\"},\"meta\":{\"itemId\":8,\"itemStatus\":{\"type\":\"" + state + "\"}},\"content\":{\"type\":\"sndMsgContent\",\"msgContent\":{\"type\":\"text\",\"text\":\"SYNTHETIC-RETRY\"}}}")!;
    public static async Task<int> Run(bool network)
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "conversation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var checks = new List<string>(); CoreClient? db = null;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); Save("running"); }
        void Save(string status, string? reason = null) => File.WriteAllText(Path.Combine(RuntimeSecurity.Base, network ? "conversation-network-qa.json" : "conversation-qa.json"), new JsonObject { ["status"] = status, ["reason"] = reason, ["checks"] = new JsonArray(checks.Select(c => JsonValue.Create(c)).ToArray()) }.ToJsonString(new() { WriteIndented = true }));
        try
        {
            Save("running");
            if (network) { await Network(root, password, Check); Save("passed"); return 0; }
            db = new CoreClient(); Check((await db.Init(Path.Combine(root, "chat"), password))["type"]?.ToString() == "ok", "Isolated encrypted store opens");
            await db.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            int sends = 0, syncs = 0; bool verified = true, loseReply = false, loseCommit = false, direct = false;
            string itemState = "sndErrorAuth", syncState = "ok";
            JsonNode Command(string cmd)
            {
                if (cmd == "/network") { var cfg = PrivacyPolicy.Configure(new JsonObject(), 9150); if (direct) cfg["socksMode"] = "never"; return new JsonObject { ["networkConfig"] = cfg }; }
                if (cmd.StartsWith("/_get code")) return new JsonObject { ["contact"] = new JsonObject { ["activeConn"] = new JsonObject { ["connectionCode"] = verified ? new JsonObject() : null } } };
                if (cmd.StartsWith("/_info")) return new JsonObject { ["connectionStats_"] = new JsonObject { ["ratchetSyncSupported"] = true, ["ratchetSyncState"] = syncState }, ["contact"] = new JsonObject { ["activeConn"] = new JsonObject { ["connStatus"] = new JsonObject { ["type"] = "ready" } } } };
                if (cmd.StartsWith("/_get item info")) return new JsonObject { ["chatItem"] = new JsonObject { ["chatItem"] = FailedItem(itemState) } };
                if (cmd.StartsWith("/_send")) { sends++; if (loseReply) throw new IOException("Synthetic reply loss after submission"); return new JsonObject { ["chatItems"] = new JsonArray(new JsonObject { ["chatItem"] = new JsonObject { ["meta"] = new JsonObject { ["itemId"] = (long)(100 + sends) } } }) }; }
                if (cmd.StartsWith("/_sync")) { syncs++; if (cmd.Contains("force")) throw new Exception("Unexpected forced repair"); return new JsonObject { ["type"] = "contactRatchetSyncStarted" }; }
                if (loseCommit && cmd.StartsWith("/sql chat UPDATE privatechat_send_attempts SET state='submitted'")) throw new IOException("Synthetic ledger commit failure");
                return db.Result(cmd).GetAwaiter().GetResult();
            }
            var recovery = new MessageRecovery(Command); string token = Guid.NewGuid().ToString("N");
            Check(recovery.Send(1, token, "synthetic", null)["state"]?.ToString() == "submitted" && sends == 1, "Accepted send records resulting item ID");
            Check(recovery.Send(1, token, "synthetic", null)["state"]?.ToString() == "submitted" && sends == 1, "Duplicate request token never sends twice");
            verified = false; Check(recovery.Send(1, Guid.NewGuid().ToString("N"), "test", null)["reason"]?.ToString() == "verify" && sends == 1, "Fresh safety code check rejects stale verification"); verified = true;
            foreach (string state in new[] { "sndNew", "sndSent", "sndRcvd", "sndWarning", "rcvNew", "unknown" })
            {
                itemState = state;
                Check(!MessageRecovery.CanRetry(FailedItem(state)) && recovery.Send(1, Guid.NewGuid().ToString("N"), null, 8)["state"]?.ToString() == "refused" && sends == 1, "No fresh send for current item status " + state);
            }
            itemState = "sndErrorAuth";
            Check(recovery.Send(1, Guid.NewGuid().ToString("N"), null, 8)["state"]?.ToString() == "submitted" && sends == 2, "Terminal failed text is retried from fresh core content");
            Check(recovery.Send(1, Guid.NewGuid().ToString("N"), null, 8)["state"]?.ToString() == "submitted" && sends == 2, "Second retry click with a new token cannot duplicate the same source");
            var file = FailedItem(); file["file"] = new JsonObject(); Check(!MessageRecovery.CanRetry(file), "Attachment is excluded from text resend path");
            var deleted = FailedItem(); deleted["meta"]!["itemDeleted"] = new JsonObject(); Check(!MessageRecovery.CanRetry(deleted), "Deleted content is not resurrected by retry");
            loseReply = true; string unknown = Guid.NewGuid().ToString("N");
            Check(recovery.Send(1, unknown, "test", null)["state"]?.ToString() == "pending" && sends == 3, "Reply loss retains a durable unresolved intent");
            recovery = new MessageRecovery(Command);
            Check(recovery.Send(1, unknown, "test", null)["state"]?.ToString() == "pending" && sends == 3, "Recreating retry service cannot resend unknown outcome");
            Check(recovery.Send(1, Guid.NewGuid().ToString("N"), "test", null)["reason"]?.ToString() == "review" && sends == 3, "New sends wait for review when an earlier result is unknown");
            await db.CloseStore(); db.Dispose(); db = null; await ProfileBackup.WaitForProfile(root, CancellationToken.None);
            db = new CoreClient(); await db.Init(Path.Combine(root, "chat"), password);
            Check((await db.MessageStatus(1))["attempts"]!.AsArray().Any(a => a?["token"]?.ToString() == unknown && a?["state"]?.ToString() == "pending"), "Unresolved state survives a real core process restart");
            await db.ReviewMessages(1, [unknown]);
            Check((await db.MessageStatus(1))["attempts"]!.AsArray().All(a => a?["state"]?.ToString() != "pending") && sends == 3, "Review changes only local state without sending");
            loseReply = false; loseCommit = true;
            Check(recovery.Send(1, Guid.NewGuid().ToString("N"), "test", null)["state"]?.ToString() == "pending" && sends == 4, "Failure after actual submission remains unresolved, never automatically retried");
            var pending = recovery.Status(1)["attempts"]!.AsArray().Where(a => a?["state"]?.ToString() == "pending").Select(a => a!["token"]!.ToString()).ToArray();
            await db.ReviewMessages(1, pending); loseCommit = false;
            direct = true; bool denied = false; try { recovery.Send(1, Guid.NewGuid().ToString("N"), "test", null); } catch (IOException) { denied = true; }
            Check(denied && sends == 4, "Non-Tor configuration is rejected before submission"); direct = false;
            foreach (string state in new[] { "ok", "started", "agreed", "unknown" }) { syncState = state; Check(recovery.Repair(1)["state"]?.ToString() == "refused" && syncs == 0, "Repair not forced for state " + state); }
            foreach (string state in new[] { "allowed", "required" }) { syncState = state; Check(recovery.Repair(1)["type"]?.ToString() == "contactRatchetSyncStarted", "Core-permitted repair for state " + state); }
            Check(syncs == 2, "Exactly one non-forced sync command per allowed repair");
            await db.CloseStore(); db.Dispose(); db = null; await ProfileBackup.WaitForProfile(root, CancellationToken.None);
            string archive = root + ".pcbackup", restored = root + "-restored"; Directory.CreateDirectory(restored);
            await ProfileBackup.Create(root,archive,password,CancellationToken.None);
            await ProfileBackup.Restore(restored,archive,password,CancellationToken.None);
            db = new CoreClient(); await db.Init(Path.Combine(restored,"chat"),password);
            Check((await db.MessageStatus(1))["attempts"]!.AsArray().Any(a=>a?["source"]?.GetValue<long>()==8 && a?["state"]?.ToString()=="submitted"), "Encrypted local backup and restore preserve retry suppression history");
            Save("passed"); return 0;
        }
        catch (Exception e) { Save("failed", e is CoreException ce ? ce.Diagnostic : e.ToString()); return 1; }
        finally { db?.Dispose(); }
    }
    private static async Task Network(string root, string password, Action<bool, string> check)
    {
        using var tor = new TorService();
        tor.Start(Path.Combine(RuntimeSecurity.Base, "qa", "backup-tor-cache")); await Until(() => Task.FromResult(tor.Ready), 300, "Tor bootstrap");
        CoreClient? a = null, b = null;
        string ar = Path.Combine(root, "a"), br = Path.Combine(root, "b"); Directory.CreateDirectory(ar); Directory.CreateDirectory(br);
        try
        {
            a = await Endpoint(ar, true); b = await Endpoint(br, true);
            string link = (await a.Result("/_connect 1 incognito=on"))["connLinkInvitation"]!["connFullLink"]!.ToString(); await b.Result("/_connect 1 incognito=on " + link);
            long ac = 0, bc = 0;
            await Until(async () => { ac = await Contact(a); bc = await Contact(b); return ac > 0 && bc > 0; }, 180, "Contacts ready");
            string code = (await a.Result($"/_get code @{ac}"))["connectionCode"]!.ToString();
            check(code == (await b.Result($"/_get code @{bc}"))["connectionCode"]!.ToString(), "Real Tor contacts have matching safety codes");
            await a.Result($"/_verify code @{ac} {code}"); await b.Result($"/_verify code @{bc} {code}");
            string one = "SYNTHETIC-UNREAD-ONE-" + Guid.NewGuid().ToString("N"), two = "SYNTHETIC-UNREAD-TWO-" + Guid.NewGuid().ToString("N");
            string token = Guid.NewGuid().ToString("N"); var first = await a.SendText(ac, token, one);
            check(first["state"]?.ToString() == "submitted", "Production guarded-send RPC submits through real Tor");
            await a.SendText(ac, token, one);
            check((await a.SendText(ac, Guid.NewGuid().ToString("N"), two))["state"]?.ToString() == "submitted", "Second distinct guarded send accepted");
            await Until(async () => (await Items(b, bc)).Count(i => i?["meta"]?["itemText"]?.ToString() is string text && (text == one || text == two)) == 2, 120, "Receive synthetic messages");
            check((await Items(a, ac)).Count(i => i?["meta"]?["itemText"]?.ToString() == one) == 1, "Duplicate RPC creates only one outgoing chat item");
            var items = await Items(b, bc); long firstId = items.Single(i => i?["meta"]?["itemText"]?.ToString() == one)!["meta"]!["itemId"]!.GetValue<long>();
            long secondId = items.Single(i => i?["meta"]?["itemText"]?.ToString() == two)!["meta"]!["itemId"]!.GetValue<long>();
            check(items.Where(i => i?["meta"]?["itemId"]?.GetValue<long>() is long id && (id == firstId || id == secondId)).All(i => i?["meta"]?["itemStatus"]?["type"]?.ToString() == "rcvNew"), "Received messages start unread in the native encrypted store");
            int before = await Unread(b, bc); await b.Result($"/_read chat items @{bc} {firstId}");
            check(await Unread(b, bc) == before - 1, "Exact-item read decrements the native unread count by one");
            items = await Items(b, bc);
            check(items.Single(i => i?["meta"]?["itemId"]?.GetValue<long>() == secondId)?["meta"]?["itemStatus"]?["type"]?.ToString() == "rcvNew", "Other message remains unread");
            await b.Result("/_stop"); await b.CloseStore(); b.Dispose(); b = null; await ProfileBackup.WaitForProfile(br, CancellationToken.None);
            b = await Endpoint(br, false);
            check(await Unread(b, bc) == before - 1, "Unread badge data survives receiver restart");
            check((await a.RepairConnection(ac))["state"]?.ToString() == "refused", "Production worker refuses to force-repair a healthy real connection");
            // A synthetic SQL fixture changes only the local status of our own test item.
            // This tests retry mechanics, not a claim that a real authentication fault occurred.
            long source = first["item"]!.GetValue<long>();
            await a.Result($"/sql chat UPDATE chat_items SET item_status='snd_error_auth' WHERE chat_item_id={source}");
            var fixture = (await a.Result($"/_get item info @{ac} {source}"))["chatItem"]!["chatItem"]!;
            check(MessageRecovery.CanRetry(fixture), "Synthetic terminal-error fixture is parsed by the native core");
            var retry = await a.SendText(ac, Guid.NewGuid().ToString("N"), source: source);
            check(retry["state"]?.ToString() == "submitted", "Production retry re-reads and submits failed text over Tor");
            var repeat = await a.SendText(ac, Guid.NewGuid().ToString("N"), source: source);
            check(repeat["item"]?.GetValue<long>() == retry["item"]?.GetValue<long>(), "Repeated retry resolves to the same new native item");
            await Until(async () => (await Items(b, bc)).Count(i => i?["meta"]?["itemText"]?.ToString() == one) == 2, 120, "Retried delivery");
            check((await Items(a, ac)).Count(i => i?["meta"]?["itemText"]?.ToString() == one) == 2, "Exactly one new message for a terminal-error retry fixture");
            check(PrivacyPolicy.IsStrict((await a.Result("/network"))["networkConfig"]!, tor.Port), "Mandatory Tor configuration survives sends, retry and repair checks");
            // Synthetic eligibility fixture, isolated from real profiles. Exercise the
            // actual native ratchet exchange without claiming to reproduce key loss.
            await a.Result("/sql agent UPDATE connections SET ratchet_sync_state='required' WHERE ratchet_sync_state='ok'");
            check(MessageRecovery.CanRepair(await a.Result($"/_info @{ac}")), "Synthetic required-sync fixture is visible through native connection info");
            check((await a.RepairConnection(ac))["type"]?.ToString()=="contactRatchetSyncStarted", "Production guarded repair starts native synchronization over Tor");
            await Until(async()=>MessageRecovery.SyncState(await a.Result($"/_info @{ac}"))=="ok" && MessageRecovery.SyncState(await b.Result($"/_info @{bc}"))=="ok",120,"Ratchet synchronization");
            check(true,"Both actual endpoints finish native ratchet synchronization");
            string newCode=(await a.Result($"/_get code @{ac}"))["connectionCode"]!.ToString();
            check(newCode==(await b.Result($"/_get code @{bc}"))["connectionCode"]!.ToString(),"Safety codes agree after connection repair");
            await a.Result($"/_verify code @{ac} {newCode}"); await b.Result($"/_verify code @{bc} {newCode}");
            string repaired="SYNTHETIC-AFTER-REPAIR-"+Guid.NewGuid().ToString("N");
            check((await a.SendText(ac,Guid.NewGuid().ToString("N"),repaired))["state"]?.ToString()=="submitted","Sending resumes after repair and explicit safety-code verification");
            await Until(async()=>(await Items(b,bc)).Any(i=>i?["meta"]?["itemText"]?.ToString()==repaired),120,"Post-repair delivery");
            check(true,"Peer receives a real encrypted message after native repair");
        }
        finally { a?.Dispose(); b?.Dispose(); }
        async Task<CoreClient> Endpoint(string path, bool create)
        {
            var core = new CoreClient();
            try
            {
                if ((await core.Init(Path.Combine(path, "chat"), password))["type"]?.ToString() != "ok") throw new IOException("Profile open failed");
                if (create) await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
                var cfg = PrivacyPolicy.Configure((await core.Result("/network"))["networkConfig"]!, tor.Port); cfg["hostMode"] = "public";
                await core.Result("/_network " + cfg.ToJsonString()); await FileTransfer.Configure(core, path);
                await core.Result("/_start main=on snd_files=off"); await core.Result("/_set receipts contacts 1 off clear_overrides=on"); return core;
            }
            catch { core.Dispose(); throw; }
        }
    }
    private static async Task<JsonArray> Items(CoreClient core, long contact) => (await core.Result($"/_get chat @{contact} count=100"))["chat"]!["chatItems"]!.AsArray();
    private static async Task<int> Unread(CoreClient core, long contact) => (await core.Result("/_get chats 1 pcc=on"))["chats"]!.AsArray().Single(c => c?["chatInfo"]?["contact"]?["contactId"]?.GetValue<long>() == contact)!["chatStats"]!["unreadCount"]!.GetValue<int>();
    private static async Task<long> Contact(CoreClient core) => (await core.Result("/_get chats 1 pcc=on"))["chats"]!.AsArray().Select(c => c?["chatInfo"]?["contact"]).FirstOrDefault(c => c?["activeConn"]?["connStatus"]?["type"]?.ToString() == "ready")?["contactId"]?.GetValue<long>() ?? 0;
    private static async Task Until(Func<Task<bool>> condition, int seconds, string reason) { var time = Stopwatch.StartNew(); while (time.Elapsed.TotalSeconds < seconds) { if (await condition()) return; await Task.Delay(1000); } throw new TimeoutException(reason); }

    public static int RunUi()
    {
        var app = new Application(); string root = Path.Combine(RuntimeSecurity.Base, "qa", "conversation-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var w = new MainWindow(root); var checks = new List<string>();
        ((Grid)w.Content).Background = w.Background;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks.Add(text); }
        T UI<T>(string name) => (T)w.FindName(name);
        void Layout(int width, int height) { w.Width = width; w.Height = height; var surface = (FrameworkElement)w.Content; surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout(); }
        void Screenshot(string name, int width, int height) { Layout(width,height); var image = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); image.Render((Visual)w.Content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(root,name+".png")); encoder.Save(stream); }
        try
        {
            Check(new ContactRow(1,"test","",true,true,125).UnreadLabel == "99+" && new ContactRow(1,"test","",true,true).UnreadVisibility == Visibility.Collapsed, "Unread badge is bounded and zero is hidden");
            Check(MainWindow.ReadAllowed(true,false,true,false,false,false),"Foreground unobscured conversation is eligible to mark visible items read");
            Check(!MainWindow.ReadAllowed(false,false,true,false,false,false),"Inactive window preserves unread");
            Check(!MainWindow.ReadAllowed(true,true,true,false,false,false),"Minimized window preserves unread");
            Check(!MainWindow.ReadAllowed(true,false,false,false,false,false),"Hidden conversation preserves unread");
            Check(!MainWindow.ReadAllowed(true,false,true,true,false,false),"Busy operation preserves unread");
            Check(!MainWindow.ReadAllowed(true,false,true,false,true,false),"Unfinished history render preserves unread");
            Check(!MainWindow.ReadAllowed(true,false,true,false,false,true),"Dialog covering a conversation preserves unread");
            var panel = UI<StackPanel>("MessagesPanel"); var scroll = UI<ScrollViewer>("MessageScroll");
            var contact = new ContactRow(1,"虚构测试联系人","已核验安全码",true,true,125);
            typeof(MainWindow).GetField("selected",flags)!.SetValue(w,contact);
            UI<ListBox>("ContactsList").ItemsSource = new[] { contact, new ContactRow(2,"另一位测试联系人","安全码待核验",false,true,3) };
            UI<Grid>("ChatView").Visibility = Visibility.Visible; UI<Grid>("UnlockView").Visibility = Visibility.Collapsed; UI<StackPanel>("EmptyConversation").Visibility = Visibility.Collapsed;
            UI<TextBlock>("NoContacts").Visibility = Visibility.Collapsed;
            UI<TextBlock>("ChatTitle").Text = contact.Name; UI<TextBlock>("ChatDetail").Text = "已核验安全码 · Ctrl+Enter 发送";
            UI<Button>("VerifyButton").Visibility = Visibility.Visible; UI<DockPanel>("HistoryBar").Visibility = Visibility.Visible;
            for(int i=1;i<=20;i++) panel.Children.Add(new Border { Tag=(long)i, Height=85, Child=new TextBlock { Text="仅用于界面检查的虚构消息 " + i,Margin=new Thickness(16) } });
            var bubble = (Border)typeof(MainWindow).GetMethod("CreateMessageBubble",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{"这条虚构消息发送失败。","12:30 · 发送失败",true,8L})!;
            typeof(MainWindow).GetMethod("AddRetryAction",flags)!.Invoke(w,new object[]{bubble,FailedItem(),1L}); panel.Children.Add(bubble);
            Check(((StackPanel)bubble.Child).Children.OfType<Button>().Single().Content.ToString() == "重试发送", "Failed text card exposes a manual retry action");
            typeof(MainWindow).GetMethod("UpdateActions",flags)!.Invoke(w,null);
            Check(!((StackPanel)bubble.Child).Children.OfType<Button>().Single().IsEnabled && !UI<Button>("RepairSyncButton").IsEnabled, "Offline UI disables retry and repair");
            foreach(var (width,height) in new[]{(960,660),(1440,900),(2560,1440)})
            {
                Layout(width,height); scroll.ScrollToTop(); Layout(width,height);
                var ids = MainWindow.VisibleUnread(panel,scroll,new HashSet<long>(Enumerable.Range(1,20).Select(i=>(long)i)));
                Check(ids.Length > 0 && ids.Length < 20 && !ids.Contains(20), "Only on-screen unread items selected at " + width);
                scroll.ScrollToEnd(); Screenshot("conversation-"+width,width,height);
                var column=UI<Grid>("ReadingColumn"); Check(column.ActualWidth<=840.5,"Reading width remains bounded at "+width);
            }
            Check(!(bool)typeof(MainWindow).GetMethod("CanReadVisible",flags)!.Invoke(w,null)!, "Window without an unlocked core cannot clear unread messages");
            typeof(MainWindow).GetMethod("ApplyDeliveryStatus",flags)!.Invoke(w,new object[]{new JsonObject{["attempts"]=new JsonArray(new JsonObject{["token"]=new string('a',32),["state"]="pending"})}});
            Check(UI<Border>("PendingSendNotice").Visibility==Visibility.Visible && !UI<Button>("SendButton").IsEnabled,"Unknown send outcome blocks composer and displays review guidance");
            UI<Grid>("RepairPanel").Visibility=Visibility.Visible; UI<TextBlock>("RepairStatus").Text="Tor 通道已建立。\n\n加密连接状态正常。消息仍等待时，可重连 Tor 或等待对方上线。";
            Screenshot("repair",960,660);
            w.EmergencyLock();
            Check(UI<Grid>("RepairPanel").Visibility==Visibility.Collapsed && UI<TextBlock>("RepairStatus").Text=="" && UI<Border>("PendingSendNotice").Visibility==Visibility.Collapsed && UI<ListBox>("ContactsList").ItemsSource==null,"Lock clears unread display, pending banner and repair details");
            Save("passed");return 0;
        }
        catch(Exception ex){Save("failed",ex.ToString());return 1;}
        finally { w.Close(); }
        void Save(string status,string? reason=null)=>File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"conversation-ui-qa.json"),new JsonObject{["status"]=status,["reason"]=reason,["screenshots"]=root,["checks"]=new JsonArray(checks.Select(c=>JsonValue.Create(c)).ToArray())}.ToJsonString(new(){WriteIndented=true}));
    }
}
