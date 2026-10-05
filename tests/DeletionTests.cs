using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PrivateChat;

internal static class DeletionTests
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
    public static async Task<int> Run()
    {
        string root=Path.Combine(RuntimeSecurity.Base,"qa","deletion-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string profile=Path.Combine(root,"profile"), backups=Path.Combine(root,"backups"); Directory.CreateDirectory(profile); Directory.CreateDirectory(backups);
        string password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var checks=new List<string>(); CoreClient? core=null;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add(text);Save("running");}
        void Save(string status,string? reason=null)=>File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"deletion-qa.json"),JsonSerializer.Serialize(new{status,reason,checks},new JsonSerializerOptions{WriteIndented=true}));
        Dictionary<string,string> Hashes(string folder)=>Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(folder,p),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count && a.All(p=>b.TryGetValue(p.Key,out string? v)&&v==p.Value);
        async Task<bool> Refuses(Func<Task> run){try{await run();return false;}catch{return true;}}
        try
        {
            Save("running");core=new CoreClient();Check((await core.Init(Path.Combine(profile,"chat"),password))["type"]?.ToString()=="ok","Synthetic encrypted identity opens");
            await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}"); await FileTransfer.Configure(core,profile);
            await core.Result("/sql chat CREATE TABLE qa_delete_messages(body TEXT NOT NULL)");
            await core.Result("/sql chat INSERT INTO qa_delete_messages VALUES ('SYNTHETIC-DELETE-NOTES')");
            await core.CloseStore();core.Dispose();core=null;await ProfileBackup.WaitForProfile(profile,CancellationToken.None);
            string first=Path.Combine(backups,"selected.pcbackup"), other=Path.Combine(backups,"keep.pcbackup");
            await ProfileBackup.Create(profile,first,password,CancellationToken.None);File.Copy(first,other);
            foreach(string folder in new[]{"files","file-temp","file-assets","tor",".restore-history/old/previous",".restore-transaction/previous",".backup-work-"+new string('a',32)})
            {string path=Path.Combine(profile,folder);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"synthetic.bin"),"SYNTHETIC-APP-DATA");}
            File.WriteAllText(Path.Combine(profile,"exported.txt"),"SYNTHETIC-USER-EXPORT");File.Copy(first,Path.Combine(profile,"manual-backup.pcbackup"));
            string unrelated=Path.Combine(root,"unrelated.txt");File.WriteAllText(unrelated,"KEEP");
            var before=Hashes(profile);var backupBefore=Hashes(backups);
            Check(await Refuses(()=>ProfileDeletion.PrepareProfile(profile,password+"wrong",CancellationToken.None)),"Wrong password cannot prepare account destruction");
            Check(Same(before,Hashes(profile))&&!ProfileDeletion.Pending(profile),"Wrong password leaves all synthetic profile bytes intact");
            var plan=await ProfileDeletion.PrepareProfile(profile,password,CancellationToken.None);
            var afterReview=Hashes(profile);
            Check(before.Count==afterReview.Count && before.All(p=>afterReview.ContainsKey(p.Key)&&(p.Key is "chat_chat.db" or "chat_agent.db" || afterReview[p.Key]==p.Value)),"Offline password verification preserves every file and all non-database bytes");
            core=new CoreClient();await core.Init(Path.Combine(profile,"chat"),password);
            Check((await core.Result("/sql chat SELECT body FROM qa_delete_messages"))["rows"]?[1]?.ToString()=="SYNTHETIC-DELETE-NOTES","Password verification preserves logical encrypted message content");
            await core.CloseStore();core.Dispose();core=null;await ProfileBackup.WaitForProfile(profile,CancellationToken.None);
            plan=await ProfileDeletion.PrepareProfile(profile,password,CancellationToken.None); before=Hashes(profile);
            Check(plan.Entries.Any(e=>e.Path.EndsWith("chat_chat.db"))&&plan.Entries.Any(e=>e.Path.Contains(".restore-history"))&&plan.Entries.Any(e=>e.Path.Contains(".restore-transaction"))&&plan.Entries.Any(e=>e.Path.Contains("\\tor\\")),"One profile plan includes identity databases, Tor and old internal recovery copies");
            Check(!plan.Entries.Any(e=>e.Path.EndsWith(".pcbackup")||e.Path.EndsWith("exported.txt"))&&plan.UnrecognizedRoots==2,"Separately saved backups and exported TXT are outside automatic account deletion");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(plan,"wrong",true,CancellationToken.None))),"Wrong confirmation phrase cannot delete");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(plan,plan.Phrase,false,CancellationToken.None))),"Unchecked irreversible-action acknowledgement cannot delete");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(plan,plan.Phrase,true,cancel.Token))),"Cancellation before commit cannot delete");}
            Check(Same(before,Hashes(profile))&&!ProfileDeletion.Pending(profile),"All pre-confirmation refusals preserve profile and do not leave a pending marker");
            string late=Path.Combine(profile,"files","new-after-review.bin");File.WriteAllText(late,"NEW");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(plan,plan.Phrase,true,CancellationToken.None)))&&File.Exists(late)&&!ProfileDeletion.Pending(profile),"New files invalidate the reviewed profile plan before any deletion");
            plan=await ProfileDeletion.PrepareProfile(profile,password,CancellationToken.None);
            string blocked=Path.Combine(profile,"files","synthetic.bin");File.SetAttributes(blocked,FileAttributes.ReadOnly);
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(plan,plan.Phrase,true,CancellationToken.None)))&&ProfileDeletion.Pending(profile)&&File.Exists(blocked),"Read-only deletion failure keeps an incomplete marker and never reports success");
            Check(await Refuses(()=>Task.Run(()=>ProfileRestore.Startup(profile))),"Startup recovery is blocked during incomplete destruction");
            Check(Same(backupBefore,Hashes(backups))&&File.ReadAllText(unrelated)=="KEEP","Partial profile deletion never touches external backups or unrelated files");
            File.SetAttributes(blocked,FileAttributes.Normal);
            var resumed=await ProfileDeletion.PrepareProfile(profile,"",CancellationToken.None);
            Check(resumed.Entries.Count>0,"Previously confirmed partial deletion can be reviewed without a now-missing password database");
            using(var instance=new FileStream(Path.Combine(profile,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                ProfileDeletion.Execute(resumed,resumed.Phrase,true,CancellationToken.None);
            Check(!ProfileDeletion.Pending(profile)&&!Directory.GetFileSystemEntries(profile).Any(p=>ProfileDeletion.Managed(Path.GetFileName(p))),"Confirmed resume removes all managed identity and chat data, including old copies");
            Check(File.Exists(Path.Combine(profile,"exported.txt"))&&File.Exists(Path.Combine(profile,"manual-backup.pcbackup"))&&File.Exists(Path.Combine(profile,"instance.lock")),"Instance lock, manual backups and user export remain outside deletion scope");
            Check(Same(backupBefore,Hashes(backups)),"External backup bytes remain unchanged after full account destruction");
            core=new CoreClient();Check((await core.Init(Path.Combine(profile,"chat"),password+"new"))["type"]?.ToString()=="ok","Deleted profile can initialize a fresh encrypted store with a new password");
            await core.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            Check((await core.Result("/sql chat SELECT COUNT(*) FROM sqlite_master WHERE name='qa_delete_messages'"))["rows"]?[1]?.ToString()=="0","Fresh identity does not retain the deleted encrypted message fixture");
            await core.CloseStore();core.Dispose();core=null;await ProfileBackup.WaitForProfile(profile,CancellationToken.None);
            var newProfile=Hashes(profile);
            var selection=ProfileDeletion.PrepareBackups(profile,new[]{first,first.ToUpperInvariant()});
            Check(selection.Files==1&&selection.Profile==null,"Manual backup selection deduplicates exact Windows paths");
            using(var held=new FileStream(first,FileMode.Open,FileAccess.Read,FileShare.None))
                Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(selection,selection.Phrase,true,CancellationToken.None))),"In-use backup cannot be reported deleted");
            Check(File.Exists(first)&&Same(newProfile,Hashes(profile)),"Blocked backup deletion preserves current identity and file");
            ProfileDeletion.Execute(selection,selection.Phrase,true,CancellationToken.None);
            Check(!File.Exists(first)&&File.Exists(other)&&Same(newProfile,Hashes(profile)),"Manual backup deletion removes only selected archive and preserves new identity");
            string changed=Path.Combine(backups,"changed.pcbackup");File.Copy(other,changed);var changedPlan=ProfileDeletion.PrepareBackups(profile,[changed]);
            string saved=changed+".saved";File.Move(changed,saved);File.Copy(other,changed);
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.Execute(changedPlan,changedPlan.Phrase,true,CancellationToken.None)))&&File.Exists(changed)&&File.Exists(saved),"Same-name archive replacement invalidates reviewed file identity");
            string falseBackup=Path.Combine(backups,"unrecognized.pcbackup");File.WriteAllText(falseBackup,"Not an archive");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.PrepareBackups(profile,[falseBackup])))&&File.Exists(falseBackup),"Renamed non-backup file is refused and preserved");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.PrepareBackups(profile,[backups]))),"Directory cannot be selected as a backup to delete");
            string hard=Path.Combine(backups,"hardlink.pcbackup");Check(CreateHardLinkW(hard,other,IntPtr.Zero),"Synthetic hard-link fixture created");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.PrepareBackups(profile,[hard])))&&File.Exists(other),"Hard-linked backup is refused without touching its other name");
            string target=Path.Combine(root,"outside"), junction=Path.Combine(profile,"files");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"keep.txt"),"OUTSIDE");
            var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in new[]{"/c","mklink","/J",junction,target})start.ArgumentList.Add(arg);
            using(var process=Process.Start(start)!){await process.WaitForExitAsync();Check(process.ExitCode==0,"Synthetic junction fixture created");}
            Check(await Refuses(()=>ProfileDeletion.PrepareProfile(profile,password+"new",CancellationToken.None))&&File.ReadAllText(Path.Combine(target,"keep.txt"))=="OUTSIDE","Profile junction is refused and outside directory content is preserved");
            Check(await Refuses(()=>Task.Run(()=>ProfileDeletion.PrepareBackups(profile,[@"\\server\share\archive.pcbackup"]))),"Network backup path is rejected without contacting a share");
            Save("passed");return 0;
        }
        catch(Exception ex){Save("failed",ex is CoreException ce ? ce.Diagnostic : ex.ToString());return 1;}
        finally{core?.Dispose();}
    }
    public static int RunUi()
    {
        var app=new Application();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        string root=Path.Combine(RuntimeSecurity.Base,"qa","deletion-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var window=new MainWindow(root);bool windowClosed=false;window.Closed+=(_,_)=>windowClosed=true;((Grid)window.Content).Background=window.Background;var checks=new List<string>();
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        T UI<T>(string name)=>(T)window.FindName(name);
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add(text);}
        void Invoke(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
        void Set(string name,object value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
        void Save(string status,string? reason=null)=>File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"deletion-ui-qa.json"),JsonSerializer.Serialize(new{status,reason,checks,screenshots=root},new JsonSerializerOptions{WriteIndented=true}));
        try
        {
            Set("captureProtected",true);Invoke("Deletion_Click",window,new RoutedEventArgs());
            Check(UI<Grid>("DeletionPanel").Visibility==Visibility.Visible&&!UI<Button>("DeletionExecuteButton").IsEnabled,"Opening destruction settings cannot delete anything");
            Check(window.FindName("DeletionChooseButton")==null&&typeof(MainWindow).GetMethod("DeletionChoose_Click",flags)==null,"Backup deletion button and handler are removed");
            string keep=Path.Combine(root,"keep.pcbackup");File.WriteAllText(keep,"PCBACK01KEEP");
            string managed=Path.Combine(root,"files","synthetic.bin");Directory.CreateDirectory(Path.GetDirectoryName(managed)!);File.WriteAllText(managed,"SYNTHETIC-ATTACHMENT");
            var plan=ProfileDeletion.InspectProfile(root);Set("deletionPlan",plan);
            UI<StackPanel>("DeletionConfirmArea").Visibility=Visibility.Visible;UI<ListBox>("DeletionEntries").ItemsSource=plan.Entries.Select(e=>e.Path);
            UI<TextBlock>("DeletionSummary").Text="本机资料目录：\n"+root+"\n共 1 个文件。";UI<TextBlock>("DeletionPhraseHint").Text="输入“销毁账号与聊天数据”确认：";UI<Button>("DeletionExecuteButton").Content=plan.Phrase;
            UI<TextBox>("DeletionPhrase").Text=plan.Phrase;
            Check(!UI<Button>("DeletionExecuteButton").IsEnabled,"Confirmation text alone does not authorize destruction");
            UI<CheckBox>("DeletionAcknowledged").IsChecked=true;Check(UI<Button>("DeletionExecuteButton").IsEnabled,"Reviewed selection, phrase and acknowledgement enable the explicit action");
            UI<TextBox>("DeletionPhrase").Text="wrong";Invoke("DeletionExecute_Click",window,new RoutedEventArgs());Check(File.Exists(managed),"Wrong phrase is rejected by the click handler, not only button styling");
            UI<TextBox>("DeletionPhrase").Text=plan.Phrase;
            foreach(var(width,height)in new[]{(940,620),(1440,900),(2560,1440)})
            {
                window.Width=width;window.Height=height;var surface=(FrameworkElement)window.Content;surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();
                var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(root,"deletion-"+width+".png")))encoder.Save(file);
                Check(((Border)UI<Grid>("DeletionPanel").Children[0]).ActualWidth<=670.5,"Deletion dialog remains bounded at "+width);
            }
            var archivePlan=ProfileDeletion.PrepareBackups(root,[keep]);Set("deletionPlan",archivePlan);UI<TextBox>("DeletionPhrase").Text=archivePlan.Phrase;
            Invoke("DeletionExecute_Click",window,new RoutedEventArgs());
            Check(File.Exists(keep)&&!windowClosed&&!(bool)typeof(MainWindow).GetField("deletionBusy",flags)!.GetValue(window)!,"UI refuses an injected legacy backup-only plan even with matching confirmation");
            UI<PasswordBox>("DeletionPassword").Password="synthetic-secret";UI<TextBox>("DeletionPhrase").Text="synthetic";window.EmergencyLock();
            Check(UI<PasswordBox>("DeletionPassword").Password==""&&UI<TextBox>("DeletionPhrase").Text==""&&UI<ListBox>("DeletionEntries").ItemsSource==null&&UI<Grid>("DeletionPanel").Visibility==Visibility.Collapsed,"Lock clears deletion password, file paths and confirmation");
            File.WriteAllText(Path.Combine(root,ProfileDeletion.PendingName),"confirmed synthetic incomplete deletion");Invoke("SetUnlockText");Invoke("UpdateActions");
            Check(!UI<Button>("UnlockButton").IsEnabled&&window.FindName("BackupButton")==null&&UI<Button>("DeletionButton").IsEnabled,"Incomplete deletion blocks unlock and permits reviewed cleanup; backup entry is absent");
            Invoke("Unlock_Click",window,new RoutedEventArgs());Check(typeof(MainWindow).GetField("core",flags)!.GetValue(window)==null,"Unlock handler cannot start a core while deletion is incomplete");
            Set("deletionBusy",true);window.EmergencyLock();Check(!UI<Button>("UnlockButton").IsEnabled&&!UI<Button>("DeletionButton").IsEnabled,"Lock cannot permit overlapping destruction or unlock during deletion");Set("deletionBusy",false);
            Check(UI<TextBlock>("DeletionLimits").Text.Contains("SSD")&&UI<TextBlock>("DeletionLimits").Text.Contains("消息中继")&&UI<TextBlock>("DeletionLimits").Text.Contains("另存的备份"),"UI explains storage, remote-copy and manual-backup boundaries");
            Check(UI<TextBlock>("DeletionLimits").Text.Contains("旧身份与记录仍可能恢复")&&UI<TextBlock>("DeletionLimits").Text.Contains("TXT 仍可读取"),"Confirmation explicitly explains that retained backups can restore identity and TXT remains readable");
            // Exercise the actual account-destruction UI with a separate, newly created identity.
            File.Delete(Path.Combine(root,ProfileDeletion.PendingName));
            var client=new CoreClient();
            try
            {
                var opened=client.Init(Path.Combine(root,"chat"),"synthetic-profile-password");PumpUntil(()=>opened.IsCompleted,20);opened.GetAwaiter().GetResult();
                var created=client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");PumpUntil(()=>created.IsCompleted,20);created.GetAwaiter().GetResult();
                int oldPid=client.ProcessId;Set("core",client);Invoke("Deletion_Click",window,new RoutedEventArgs());
                Check(UI<Button>("KeyEnableButton").IsEnabled,"Legacy identity exposes explicit key-protection migration");
                UI<PasswordBox>("DeletionPassword").Password="synthetic-profile-password";
                Invoke("KeyEnable_Click",window,new RoutedEventArgs());PumpUntil(()=>!(bool)typeof(MainWindow).GetField("deletionBusy",flags)!.GetValue(window)!,60);
                Check(ProfileKeys.Exists(root)&&File.Exists(Path.Combine(root,"chat_chat.db")),"Actual UI key migration preserves identity databases and creates a key envelope");
                Check(!UI<Button>("KeyEnableButton").IsEnabled&&UI<TextBlock>("DeletionStatus").Text.Contains("密钥保护已启用"),"Completed key migration disables duplicate conversion and explains historical limits");
                UI<PasswordBox>("DeletionPassword").Password="synthetic-profile-password";
                var prepare=(Task)typeof(MainWindow).GetMethod("PrepareDeletionOperation",flags)!.Invoke(window,new object[]{false})!;PumpUntil(()=>prepare.IsCompleted,30);prepare.GetAwaiter().GetResult();
                var profilePlan=(DeletionPlan?)typeof(MainWindow).GetField("deletionPlan",flags)!.GetValue(window);
                Check(profilePlan?.Profile==root&&File.Exists(Path.Combine(root,"chat_chat.db")),"Account-destruction UI prepares a real profile without deleting it");
                Check(profilePlan?.Keys==1&&UI<TextBlock>("DeletionSummary").Text.Contains("首先销毁"),"Deletion review explicitly lists key-first destruction");
                bool oldGone;try{using var old=Process.GetProcessById(oldPid);oldGone=old.HasExited;}catch(ArgumentException){oldGone=true;}
                Check(oldGone&&typeof(MainWindow).GetField("core",flags)!.GetValue(window)==null,"UI stops the unlocked identity before offline password verification and review");
                UI<TextBox>("DeletionPhrase").Text=profilePlan!.Phrase;UI<CheckBox>("DeletionAcknowledged").IsChecked=true;
                Invoke("DeletionExecute_Click",window,new RoutedEventArgs());PumpUntil(()=>!(bool)typeof(MainWindow).GetField("deletionBusy",flags)!.GetValue(window)!,20);
                Check(!File.Exists(Path.Combine(root,"chat_chat.db"))&&!File.Exists(Path.Combine(root,"chat_agent.db"))&&!ProfileKeys.Exists(root)&&!ProfileDeletion.Pending(root),"Actual account-destruction handler destroys the key and both identity databases");
                Check(File.Exists(keep)&&windowClosed,"Account destruction preserves manual backups and closes the application window");
            }
            finally{client.Dispose();}
            Save("passed");return 0;
        }
        catch(Exception ex){Save("failed",ex.ToString());return 1;}
        finally{Set("deletionBusy",false);window.Close();}
    }
    private static void PumpUntil(Func<bool> done,int seconds)
    {
        var timer=Stopwatch.StartNew();while(!done()){if(timer.Elapsed.TotalSeconds>seconds)throw new TimeoutException("UI deletion");var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(10);}
    }
}
