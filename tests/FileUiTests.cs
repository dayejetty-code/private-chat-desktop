using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrivateChat;

internal static class FileUiTests
{
    public static int Run()
    {
        var app = new Application();
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "file-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var window = new MainWindow(root);
        ((Grid)window.Content).Background = window.Background;
        var checks = new List<string>();
        void Assert(bool ok,string text) { if(!ok) throw new Exception(text);checks.Add(text); }
        try
        {
            var selected = typeof(MainWindow).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic)!;
            selected.SetValue(window,new ContactRow(1,"虚构测试联系人","已核验安全码",true,true));
            var create = typeof(MainWindow).GetMethod("CreateFileBubble",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var panel = (StackPanel)window.FindName("MessagesPanel");
            Directory.CreateDirectory(FileTransfer.Cache(root));
            File.WriteAllBytes(Path.Combine(FileTransfer.Cache(root),"synthetic.bin"),new byte[16]);
            Assert(((Button)window.FindName("FileButton")).Content.ToString()=="发送 TXT", "Composer names the text-only attachment policy");
            foreach(var (status,sent) in new[]{("sndTransfer",true),("rcvInvitation",false),("rcvComplete",false),("rcvError",false)})
            {
                var item = JsonNode.Parse("{\"meta\":{\"itemId\":1},\"content\":{\"msgContent\":{\"text\":\"虚构测试文件.txt\"}},\"file\":{\"fileId\":1,\"fileName\":\"0123456789abcdef0123456789abcdef.bin\",\"fileSize\":524288,\"fileProtocol\":\"xftp\",\"fileSource\":{\"filePath\":\"synthetic.bin\",\"cryptoArgs\":{}},\"fileStatus\":{\"type\":\"rcvInvitation\"}}}")!;
                string anonymousName = FileTransfer.NewTextName(); item["content"]!["msgContent"]!["text"] = anonymousName;
                item["file"]!["fileStatus"] = JsonNode.Parse("{\"type\":\""+status+"\",\"sndProgress\":1,\"sndTotal\":2}");
                var bubble=(Border)create.Invoke(window,new object[]{item,"12:30 · 界面示意",sent,1L})!;
                var contents=(StackPanel)bubble.Child;
                Assert(contents.Children.OfType<TextBlock>().Any(t=>t.Text==anonymousName),"File card displays the anonymous name for "+status);
                Assert(!contents.Children.OfType<Image>().Any(),"No automatic preview for "+status);
                Assert(contents.Children.OfType<Button>().All(b=>b.Content.ToString()!="打开"),"No file execution action for "+status);
                Assert(contents.Children.OfType<Button>().Select(b=>b.Content.ToString()).SequenceEqual(status switch{"rcvInvitation"=>new[]{"接收文件"},"rcvComplete"=>new[]{"另存为…"},"sndTransfer"=>new[]{"取消传输"},_=>Array.Empty<string>()}),"File action matches "+status);
                panel.Children.Add(bubble);
            }
            foreach (string status in new[]{"rcvInvitation","rcvComplete","sndTransfer"})
            {
                var item = JsonNode.Parse("{\"meta\":{\"itemId\":2},\"file\":{\"fileId\":2,\"fileName\":\"legacy.pdf\",\"fileSize\":100,\"fileProtocol\":\"xftp\",\"fileSource\":{\"cryptoArgs\":{}},\"fileStatus\":{\"type\":\"rcvInvitation\"}}}")!;
                item["file"]!["fileStatus"]!["type"] = status;
                var bubble=(Border)create.Invoke(window,new object[]{item,"旧版附件",status=="sndTransfer",2L})!;
                var buttons=((StackPanel)bubble.Child).Children.OfType<Button>().Select(b=>b.Content.ToString());
                Assert(buttons.SequenceEqual(status=="sndTransfer" ? new[]{"取消传输"} : Array.Empty<string>()),"Non-text attachment cannot be received/exported, but pending legacy transfer can be cancelled: "+status);
            }
            var missing = JsonNode.Parse("{\"meta\":{\"itemId\":3},\"content\":{\"msgContent\":{\"text\":\"missing.txt\"}},\"file\":{\"fileId\":3,\"fileSize\":1,\"fileProtocol\":\"xftp\",\"fileName\":\"missing.txt\",\"fileSource\":{\"filePath\":\"missing.bin\",\"cryptoArgs\":{}},\"fileStatus\":{\"type\":\"rcvComplete\"}}}")!;
            var missingCard=(Border)create.Invoke(window,new object[]{missing,"已清理",false,3L})!;
            Assert(!((StackPanel)missingCard.Child).Children.OfType<Button>().Any() && Texts(missingCard).Any(t=>t.Text.Contains("本机副本已清理")),"Missing local ciphertext disables export with an explanation");
            using (var core = new CoreClient())
            {
                var field=typeof(MainWindow).GetField("core",BindingFlags.Instance|BindingFlags.NonPublic)!;
                field.SetValue(window,core);
                foreach(var (verified,ready) in new[]{(false,true),(true,false),(false,false),(true,true)})
                {
                    selected.SetValue(window,new ContactRow(1,"虚构测试联系人","测试",verified,ready));
                    typeof(MainWindow).GetMethod("UpdateActions",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
                    var actions=panel.Children.OfType<Border>().SelectMany(b=>((StackPanel)b.Child).Children.OfType<Button>()).ToArray();
                    Assert(actions.Single(b=>b.Content.ToString()=="取消传输").IsEnabled,"Cancel remains enabled without network: verified="+verified+", ready="+ready);
                    Assert(!((Button)window.FindName("FileButton")).IsEnabled && !actions.Single(b=>b.Content.ToString()=="接收文件").IsEnabled,"Send/receive remain blocked without network");
                    Assert(actions.Single(b=>b.Content.ToString()=="另存为…").IsEnabled==(verified&&ready),"Export still requires verified ready contact");
                }
                field.SetValue(window,null);
            }
            ((UIElement)window.FindName("UnlockView")).Visibility=Visibility.Collapsed;
            ((UIElement)window.FindName("ChatView")).Visibility=Visibility.Visible;
            ((UIElement)window.FindName("EmptyConversation")).Visibility=Visibility.Collapsed;
            ((TextBlock)window.FindName("ChatTitle")).Text="虚构文件传输预览";
            ((TextBlock)window.FindName("ChatDetail")).Text="仅用于界面检查 · 未连接真实联系人";
            ((TextBlock)window.FindName("FooterStatus")).Text="合成界面测试，无真实聊天资料";
            string shots=Path.Combine(RuntimeSecurity.Base,"visual-files");Directory.CreateDirectory(shots);
            foreach(var (width,height) in new[]{(960,660),(1440,900),(2560,1440)})
            {
                window.Width=width;window.Height=height;var surface=(FrameworkElement)window.Content;surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();
                var composer=(FrameworkElement)window.FindName("ComposerContent");var button=(FrameworkElement)window.FindName("FileButton");
                Assert(button.ActualWidth>0 && composer.ActualWidth<=840,"File composer visible and bounded at "+width+": "+button.ActualWidth+", "+composer.ActualWidth);
                var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(surface);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(shots,width+".png"));encoder.Save(stream);
            }
            window.EmergencyLock();
            Assert(panel.Children.Count==0 && !((Button)window.FindName("FileButton")).IsEnabled,"Lock removes file cards and disables file selection");
            ((UIElement)window.FindName("CachePanel")).Visibility=Visibility.Visible;
            ((TextBlock)window.FindName("CacheUsage")).Text="已用 20 MB · 在途预留 44 MB";
            ((TextBlock)window.FindName("CacheDetails")).Text="可清理 8 MB，共 2 个文件。\n当前有 1 个未结束任务；它们的本地文件会保留。";
            window.Width=960; window.Height=660;
            var cacheSurface=(FrameworkElement)window.Content; cacheSurface.Measure(new Size(960,660)); cacheSurface.Arrange(new Rect(0,0,960,660)); cacheSurface.UpdateLayout();
            var cacheImage=new RenderTargetBitmap(960,660,96,96,PixelFormats.Pbgra32); cacheImage.Render(cacheSurface);
            var cacheEncoder=new PngBitmapEncoder(); cacheEncoder.Frames.Add(BitmapFrame.Create(cacheImage)); using(var stream=File.Create(Path.Combine(shots,"cache.png")))cacheEncoder.Save(stream);
            window.EmergencyLock();
            Assert(((UIElement)window.FindName("CachePanel")).Visibility==Visibility.Collapsed && ((TextBlock)window.FindName("CacheUsage")).Text=="" && !((Button)window.FindName("CacheButton")).IsEnabled,"Lock clears cache panel data and disables entry");
            File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"file-ui-qa.json"),new JsonObject{["status"]="passed",["checks"]=new JsonArray(checks.Select(c=>JsonValue.Create(c)).ToArray())}.ToJsonString(new(){WriteIndented=true}));
            return 0;
        }
        catch(Exception e){File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"file-ui-qa.json"),new JsonObject{["status"]="failed",["reason"]=e.Message}.ToJsonString());return 1;}
        finally{window.Close();}
    }
    private static IEnumerable<TextBlock> Texts(DependencyObject element)
    {
        if(element is TextBlock text)yield return text;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)foreach(var child in Texts(VisualTreeHelper.GetChild(element,i)))yield return child;
    }
}
