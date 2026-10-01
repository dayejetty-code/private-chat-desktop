using System.IO;
using System.Text;
using System.Windows;

namespace PrivateChat;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!RuntimeSecurity.ConfigureProcess()) return 70;
        if (args.SequenceEqual(new[] { "--worker" })) return CoreWorker.Run();
#if ENABLE_QA
        if (args.Length == 2 && args[0] == "--supervision-probe") return SecurityTests.RunSupervisionProbe(args[1]);
        if (args.Contains("--ui-security-test")) return SecurityTests.RunUi();
        if (args.Contains("--self-test")) return SelfTest.Run(args).GetAwaiter().GetResult();
#endif
        // Test/capture commands are absent from the distributed application.
        if (args.Length != 0 && !(args.Length == 2 && args[0] == "--data" && Path.IsPathFullyQualified(args[1]))) return 64;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateChatDesktop");
        // Development and two-endpoint acceptance testing use a distinct, explicit data root.
        var index = Array.IndexOf(args, "--data");
        if (index >= 0 && args.Length > index + 1) data = Path.GetFullPath(args[index + 1]);
        FileStream instance;
        try
        {
            Directory.CreateDirectory(data);
            instance = new FileStream(Path.Combine(data, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        { MessageBox.Show("这个本地资料库已在另一个窗口打开。请返回已打开的窗口。", "Private Chat"); return 1; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { MessageBox.Show("无法打开本地资料目录。请检查文件夹权限和磁盘空间，再重新启动。不要删除已有资料库。", "Private Chat"); return 1; }
        using (instance)
        {
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) => { e.Handled = true; try { (app.MainWindow as MainWindow)?.EmergencyLock(); } catch { } MessageBox.Show("操作未完成，应用已尝试锁定并断开连接。请重新解锁；聊天内容不会写入错误日志。", "Private Chat"); };
            app.Run(new MainWindow(data));
        }
        return 0;
    }
}
