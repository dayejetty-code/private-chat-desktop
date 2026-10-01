using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PrivateChat;

internal static class SecurityTests
{
    public static int RunUi()
    {
        var checks = new List<string>();
        void Assert(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        var app = new Application();
        string uiRoot=Path.Combine(RuntimeSecurity.Base,"qa","ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uiRoot);
        var window = new MainWindow(uiRoot);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"ui-security-qa.json"), "{\"status\":\"running\"}");
        try
        {
            HistoryTests.StateChecks(Assert);
            RelayTests.InputChecks(Assert);
            HistoryTests.ScrollChecks(Assert);
            var requests = new ConversationState();
            long earlier = requests.Begin(), latest = requests.Begin();
            Assert(!requests.IsCurrent(earlier) && requests.IsCurrent(latest), "Late response cannot replace a newer conversation response");
            requests.Invalidate();
            Assert(!requests.IsCurrent(latest), "Contact switch or lock invalidates pending conversation response");
            foreach (string status in new[] { "sndErrorAuth", "sndError", "sndWarning", "invalid", "futureUnknown" })
                Assert(!ConversationState.DeliveryLabel(new JsonObject { ["type"] = status }).StartsWith("已"), "Non-success delivery status is not presented as success: " + status);
            Assert(ConversationState.DeliveryLabel(new JsonObject { ["type"] = "sndRcvd", ["msgRcptStatus"] = "badMsgHash" }).Contains("异常"), "Bad message hash receipt is not displayed as delivered");
            Assert(ConversationState.DeliveryLabel(new JsonObject { ["type"] = "sndRcvd", ["msgRcptStatus"] = "ok", ["sndProgress"] = "partial" }).StartsWith("部分"), "Partial receipt remains distinct from completed delivery");
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Assert(GetWindowDisplayAffinity(handle, out uint affinity) && affinity == 0x11, "Window capture exclusion API enabled and read back");
            Assert(WerGetFlags(new IntPtr(-1), out uint wer) == 0 && (wer & 1) != 0, "WER no-heap flag read back");
            foreach (string name in new[] { "MessageInput", "ModalInput", "RelayInput" })
            {
                var box = (TextBox)window.FindName(name);
                Assert(!box.IsUndoEnabled && !box.AllowDrop, name + " undo and incoming drag/drop disabled");
                var e = new DataObjectCopyingEventArgs(new DataObject(DataFormats.UnicodeText, "SYNTHETIC-COPY"), false);
                box.RaiseEvent(e);
                foreach (string format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                    Assert(e.DataObject.GetData(format) is MemoryStream ms && ms.ToArray().SequenceEqual(new byte[4]), name + " standard Copy/Cut supplies zero DWORD " + format);
                var drag = new DataObjectCopyingEventArgs(new DataObject(DataFormats.UnicodeText, "SYNTHETIC-DRAG"), true);
                box.RaiseEvent(drag);
                Assert(drag.CommandCancelled, name + " outgoing text drag cancelled");
                box.AppendText("SYNTHETIC-CLEARED"); box.Clear(); box.Undo();
                Assert(box.Text.Length == 0, name + " cleared text unavailable through undo");
            }
            var list = (ListBox)window.FindName("ContactsList");
            var input = (TextBox)window.FindName("MessageInput");
            list.ItemsSource = new[] { new ContactRow(1,"Synthetic A","",true,true), new ContactRow(2,"Synthetic B","",false,true) };
            list.SelectedIndex = 0; input.Text = "SYNTHETIC-DRAFT-FOR-A";
            var messages = (StackPanel)window.FindName("MessagesPanel");
            messages.Children.Add(new TextBlock { Text = "SYNTHETIC-MESSAGE-FOR-A" });
            var historyState = Field<HistoryPage>(window, "history");
            historyState.Apply(new JsonArray(new JsonObject { ["meta"]=new JsonObject { ["itemId"]=500L } }),HistoryDirection.Older);
            var historyRefresh = (Task)typeof(MainWindow).GetMethod("RefreshMessages",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,Array.Empty<object>())!;
            Assert(historyRefresh.IsCompletedSuccessfully && historyState.First == 500 && messages.Children.Count == 1, "Automatic refresh preserves the historical page being read");
            list.SelectedIndex = 1;
            Assert(historyState.First == null && historyState.IsLatest, "Switching contact clears actual window pagination state");
            Assert(messages.Children.Count == 0, "Switching contact immediately removes previous conversation before loading");
            Assert(input.Text.Length == 0, "Switching contact clears previous recipient draft");
            input.Text = "SYNTHETIC-LOCK"; ((TextBox)window.FindName("ModalInput")).Text = "SYNTHETIC-INVITE";
            ((TextBox)window.FindName("RelayInput")).Text="smp://synthetic:SECRET@relay.invalid";
            window.EmergencyLock(); input.Undo();
            Assert(((TextBox)window.FindName("RelayInput")).Text.Length==0 && ((UIElement)window.FindName("RelayPanel")).Visibility==Visibility.Collapsed,"Lock clears relay addresses and hides settings");
            Assert(input.Text.Length == 0 && ((TextBox)window.FindName("ModalInput")).Text.Length == 0 && list.ItemsSource == null, "Lock clears visible messages drafts invitation and contacts");
            Assert(((Button)window.FindName("SendButton")).IsEnabled == false, "Locked window cannot send");
            Assert(((UIElement)window.FindName("HistoryBar")).Visibility == Visibility.Collapsed && !((Button)window.FindName("OlderButton")).IsEnabled, "Locked window hides history and disables pagination");
            input.Text = "SYNTHETIC-SESSION-LOCK";
            typeof(MainWindow).GetMethod("SessionChanged",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{window,new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)});
            var frame=new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
            Assert(input.Text.Length==0,"Windows session-lock event clears UI without restarting process");
            string password=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            void Unlock()
            {
                ((PasswordBox)window.FindName("Password")).Password=password;
                ((PasswordBox)window.FindName("ConfirmPassword")).Password=password;
                ((Button)window.FindName("UnlockButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>Field<CoreClient?>(window,"core")!=null && !Field<bool>(window,"busy"),20);
                Assert(((UIElement)window.FindName("ChatView")).Visibility==Visibility.Visible,"Real UI unlocks isolated encrypted profile");
            }
            Unlock();
            ((Button)window.FindName("RelaySettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            typeof(MainWindow).GetMethod("CloseRelays",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,Array.Empty<object>());
            PumpUntil(()=>!Field<bool>(window,"busy"),10);
            Assert(((TextBox)window.FindName("RelayInput")).Text.Length==0 && ((UIElement)window.FindName("RelayPanel")).Visibility==Visibility.Collapsed,"Closing relay settings while loading prevents late repopulation");
            ((Button)window.FindName("RelaySettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>Field<bool>(window,"relayLoaded") && !Field<bool>(window,"busy"),10);
            Assert(((TextBox)window.FindName("RelayInput")).Text.StartsWith("smp://") && !((Button)window.FindName("RelaySaveButton")).IsEnabled,"Relay UI reads encrypted configuration and requires testing before enabling");
            typeof(MainWindow).GetField("testedRelayFingerprint",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,"synthetic-test-proof");
            ((TextBox)window.FindName("RelayInput")).AppendText("\nsmp://modified@relay.invalid");
            Assert(Field<string?>(window,"testedRelayFingerprint")==null && !((Button)window.FindName("RelaySaveButton")).IsEnabled,"Editing a tested relay list invalidates its test result");
            int sessionCore=Field<CoreClient>(window,"core").ProcessId;
            typeof(MainWindow).GetMethod("SessionChanged",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{window,new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)});
            PumpUntil(()=>Field<CoreClient?>(window,"core")==null && !Alive(sessionCore),10);
            Assert(!Alive(sessionCore),"Windows session-lock handler terminates actual UI core");
            Unlock();
            int idleCore=Field<CoreClient>(window,"core").ProcessId;
            typeof(MainWindow).GetField("lastInput",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,DateTime.UtcNow.AddMinutes(-6));
            PumpUntil(()=>Field<CoreClient?>(window,"core")==null && !Alive(idleCore),10);
            Assert(!Alive(idleCore),"Five-minute idle threshold terminates actual UI core");
            Unlock();
            int guardedCore=Field<CoreClient>(window,"core").ProcessId;
            using(var p=Process.GetProcessById(Field<TorService>(window,"tor").ProcessId)) p.Kill(true);
            PumpUntil(()=>Field<CoreClient?>(window,"core")==null && !Alive(guardedCore),10);
            Assert(((UIElement)window.FindName("UnlockView")).Visibility==Visibility.Visible,"Unexpected Tor kill locks real UI and ends its core");
            AuditRegressionTests.Run(window, Unlock, Assert);
            ScanSyntheticFiles(uiRoot,new[]{password},Assert);
            File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"ui-security-qa.json"), new JsonObject { ["status"]="passed",["checks"]=new JsonArray(checks.Select(s=>JsonValue.Create(s)).ToArray()),["clipboardScope"]="Copy/Cut routed-event data only; user's real clipboard not overwritten",["captureScope"]="API flag readback; not every capture program" }.ToJsonString(new(){WriteIndented=true}));
            return 0;
        }
        catch(Exception e) { File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"ui-security-qa.json"), new JsonObject { ["status"]="failed",["reason"]=e.Message }.ToJsonString()); return 1; }
        finally { window.Close(); }
    }

    private static T Field<T>(object value,string name)=>(T)typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static void PumpUntil(Func<bool> condition,int seconds)
    {
        var frame=new DispatcherFrame();var until=DateTime.UtcNow.AddSeconds(seconds);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
        timer.Tick+=(_,_)=>{if(condition()||DateTime.UtcNow>=until)frame.Continue=false;};
        timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        if(!condition())throw new TimeoutException("UI lifecycle did not reach expected state");
    }

    public static void PolicyChecks(Action<bool,string> assert)
    {
        var cfg = PrivacyPolicy.Configure(new JsonObject(), 59000);
        assert(PrivacyPolicy.IsStrict(cfg,59000), "Strict policy accepts expected configuration");
        foreach(var (key,value) in new[] { ("socksProxy","127.0.0.1:59001"),("socksMode","onion"),("sessionMode","user"),("smpProxyMode","never"),("smpProxyFallback","allow"),("hostMode","onion") })
        {
            var changed=cfg.DeepClone(); changed[key]=value;
            assert(!PrivacyPolicy.IsStrict(changed,59000),"Policy rejects weakening " + key);
        }
        var logs=cfg.DeepClone(); logs["logTLSErrors"]=true;
        assert(!PrivacyPolicy.IsStrict(logs,59000),"Policy rejects TLS error content logging");
        assert(!PrivacyPolicy.VerifiedContact(JsonNode.Parse("{\"activeConn\":{}}")),"Missing contact verification rejected");
        string manifest=Path.Combine(RuntimeSecurity.Base,"runtime-manifest.json");
        byte[] original=File.ReadAllBytes(manifest);
        try
        {
            File.WriteAllText(manifest,"{\"core\":{},\"tor\":{}}"); bool rejected=false;
            try { RuntimeSecurity.Verify("core"); } catch(IOException) { rejected=true; }
            assert(rejected,"Empty/replaced disk manifest cannot bypass embedded pins");
        }
        finally { File.WriteAllBytes(manifest,original); }
        string library=Path.Combine(RuntimeSecurity.Base,"runtime","core","libcrypto-3-x64.dll");
        byte originalByte;
        using(var file=File.Open(library,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { originalByte=(byte)file.ReadByte(); file.Position=0; file.WriteByte((byte)(originalByte^1)); }
        try
        {
            bool rejected=false; try { RuntimeSecurity.Verify("core"); } catch(IOException) { rejected=true; }
            assert(rejected,"Modified native dependency rejected before loading");
        }
        finally { using var file=File.Open(library,FileMode.Open,FileAccess.Write,FileShare.None); file.WriteByte(originalByte); }
    }

    public static async Task TorExitCheck(string root, Action<bool,string> assert)
    {
        using var core=new CoreClient(); using var tor=new TorService();
        int coreId=core.ProcessId; tor.Guard(core); tor.Start(Path.Combine(root,"tor-exit-test"));
        using(var process=Process.GetProcessById(tor.ProcessId)) process.Kill(true);
        var until=DateTime.UtcNow.AddSeconds(8);
        while(Alive(coreId) && DateTime.UtcNow<until) await Task.Delay(50);
        assert(!Alive(coreId),"Unexpected Tor process kill terminates guarded core within 8 seconds");
        assert(!tor.Ready,"Unexpected Tor kill revokes readiness");
    }
    public static int RunSupervisionProbe(string marker)
    {
        using var core=new CoreClient(); using var tor=new TorService();
        tor.Start(Path.Combine(Path.GetDirectoryName(marker)!,"parent-crash-tor"));
        File.WriteAllText(marker,new JsonObject{["core"]=core.ProcessId,["tor"]=tor.ProcessId}.ToJsonString());
        Thread.Sleep(Timeout.Infinite); return 0;
    }
    public static async Task ParentCrashCheck(string root,Action<bool,string> assert)
    {
        var marker=Path.Combine(root,"child-pids.json");
        var info=new ProcessStartInfo(Path.Combine(RuntimeSecurity.Base,"PrivateChat.exe")){UseShellExecute=false,CreateNoWindow=true};
        info.ArgumentList.Add("--supervision-probe");info.ArgumentList.Add(marker);
        using var parent=Process.Start(info)!;
        int coreId=0,torId=0;
        try
        {
            var until=DateTime.UtcNow.AddSeconds(12);
            while(!File.Exists(marker) && !parent.HasExited && DateTime.UtcNow<until) await Task.Delay(100);
            var pids=JsonNode.Parse(File.ReadAllText(marker))!;
            coreId=pids["core"]!.GetValue<int>();torId=pids["tor"]!.GetValue<int>();
            parent.Kill(false); // Deliberately do not ask .NET to kill descendants; the OS job must do it.
            await parent.WaitForExitAsync();
            until=DateTime.UtcNow.AddSeconds(8);
            while((Alive(coreId)||Alive(torId))&&DateTime.UtcNow<until) await Task.Delay(50);
            assert(!Alive(coreId)&&!Alive(torId),"Abrupt parent kill closes OS jobs and ends both core and Tor");
        }
        finally
        {
            if(!parent.HasExited) parent.Kill(true);
            foreach(int id in new[]{coreId,torId}) if(id>0&&Alive(id)) { using var p=Process.GetProcessById(id); p.Kill(true); }
        }
    }
    public static void ScanSyntheticFiles(string root, IEnumerable<string> secrets, Action<bool,string> assert)
    {
        var needles=secrets.SelectMany(s=>new[]{Encoding.UTF8.GetBytes(s),Encoding.Unicode.GetBytes(s)}).ToArray();
        int count=0;
        foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
        {
            // Read shared DB/WAL snapshots without copying plaintext artifacts elsewhere.
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var bytes=new MemoryStream(); stream.CopyTo(bytes); var data=bytes.ToArray();
            if(needles.Any(n=>Contains(data,n))) throw new Exception("Synthetic plaintext marker found in test profile storage");
            count++;
        }
        assert(count>0,"Synthetic plaintext absent from " + count + " profile files in UTF-8 and UTF-16");
    }
    private static bool Contains(byte[] data, byte[] needle)=>data.AsSpan().IndexOf(needle)>=0;
    private static bool Alive(int id) { try { using var p=Process.GetProcessById(id); return !p.HasExited; } catch { return false; } }
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window,out uint affinity);
    [DllImport("kernel32.dll")] private static extern int WerGetFlags(IntPtr process,out uint flags);
}
