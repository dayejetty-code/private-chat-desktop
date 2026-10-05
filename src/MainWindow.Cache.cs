using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;

namespace PrivateChat;

public partial class MainWindow
{
    private long cacheVersion;
    private bool cacheCleaning;
    private async void Cache_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || busy) return;
        var current = core; int turn = epoch; long version = ++cacheVersion;
        CachePanel.Visibility = Visibility.Visible;
        CacheUsage.Text = "正在统计附件空间…"; CacheDetails.Text = ""; CacheCleanButton.IsEnabled = false;
        busy = true; UpdateActions();
        try
        {
            var info = await current.CacheInfo();
            if (turn != epoch || version != cacheVersion || core != current) return;
            CacheUsage.Text = $"已用 {FileTransfer.SizeLabel(info["used"]!.GetValue<long>())} · 在途预留 {FileTransfer.SizeLabel(info["reserved"]!.GetValue<long>())}";
            CacheDetails.Text = $"可清理 {FileTransfer.SizeLabel(info["cleanable"]!.GetValue<long>())}，共 {info["count"]} 个文件。\n当前有 {info["active"]} 个未结束任务；它们的本地文件会保留。";
            CacheCleanButton.IsEnabled = info["count"]!.GetValue<int>() > 0;
        }
        catch { if (turn == epoch && version == cacheVersion) CacheUsage.Text = "统计未完成，请关闭后重试。没有清理任何文件。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private void CloseCache()
    {
        ++cacheVersion; CachePanel.Visibility = Visibility.Collapsed;
        CacheUsage.Text = ""; CacheDetails.Text = ""; CacheCleanButton.IsEnabled = false;
    }
    private void CacheClose_Click(object sender, RoutedEventArgs e) => CloseCache();
    private async void CacheClean_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || busy || !CacheCleanButton.IsEnabled) return;
        var current = core; int turn = epoch; long version = cacheVersion;
        if (MessageBox.Show(this, "将清理已结束附件的本机副本和可确认闲置的文件。清理后，这些附件将无法再从本机另存为；请先保存需要保留的 TXT。\n\n聊天文字、正在传输的文件以及你另存为的文件会保留。应用将锁定并断开连接，之后需重新解锁。", "清理附件缓存", MessageBoxButton.OKCancel, MessageBoxImage.None) != MessageBoxResult.OK || turn != epoch || version != cacheVersion) return;
        busy = true; CacheCleanButton.IsEnabled = false; UpdateActions();
        JsonNode? plan = null;
        try
        {
            plan = await current.CacheInfo(plan: true);
            if (turn != epoch || version != cacheVersion || core != current) return;
            using var worker = Process.GetProcessById(current.ProcessId);
            _ = worker.SafeHandle; // Keep the exact owned process handle across Lock/Dispose.
            cacheCleaning = true;
            Lock(); int locked = epoch;
            // Prevent another unlock until cleanup has finished; the profile instance lock remains held.
            busy = true; UnlockButton.IsEnabled = false;
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var result = await Task.Run(() => FileCache.CleanOffline(dataRoot, plan["plan"]!.AsArray()));
            if (epoch == locked)
                FooterStatus.Text = $"已清理 {result.Count} 个文件，释放 {FileTransfer.SizeLabel(result.Bytes)}" + (result.Skipped > 0 ? $"；{result.Skipped} 个变化或被占用的文件已保留" : "") + "。重新解锁后继续。";
        }
        catch { if (!closing) FooterStatus.Text = "清理未全部完成，未确认安全的文件已保留。可重新解锁后检查。"; }
        finally
        {
            plan?.AsObject().Clear();
            cacheCleaning = false;
            if (core == null || turn == epoch) { busy = false; UnlockButton.IsEnabled = true; UpdateActions(); }
        }
    }
}
