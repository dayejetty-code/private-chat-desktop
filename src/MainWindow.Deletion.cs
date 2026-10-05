using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PrivateChat;

public partial class MainWindow
{
    private DeletionPlan? deletionPlan;
    private CancellationTokenSource? deletionCancellation;
    private bool deletionBusy, closeAfterDeletion, keyMigrationBusy;
    private int deletionVersion;
    private bool DeletionBlocksUnlock => deletionBusy || deletionPlan != null || ProfileDeletion.Pending(dataRoot);
    private void Deletion_Click(object sender, RoutedEventArgs e)
    {
        if (busy || cacheCleaning || deletionBusy || !captureProtected) return;
        CloseDeletion(); CloseCache(); CloseRelays(); CloseModal(); CloseRepair();
        DeletionPanel.Visibility = Visibility.Visible;
        DeletionStatus.Text = ProfileDeletion.Pending(dataRoot) ? "上次删除尚未完成，已阻止联网。点击“检查本机资料”重新列出剩余项目，再确认继续；无需再次输入已删除资料的口令。" : "先检查本机资料清单。此时不会删除任何文件。";
        DeletionPasswordPanel.Visibility = ProfileDeletion.Pending(dataRoot) ? Visibility.Collapsed : Visibility.Visible;
        DeletionProfileButton.IsEnabled = !IsNew || ProfileDeletion.Pending(dataRoot);
        UpdateKeyStatus();
        UpdateDeletionActions();
    }
    private void CloseDeletion()
    {
        ++deletionVersion; deletionCancellation?.Cancel(); deletionPlan = null;
        DeletionPassword.Clear(); DeletionPhrase.Clear(); DeletionAcknowledged.IsChecked = false;
        DeletionEntries.ItemsSource = null; DeletionSummary.Text = ""; DeletionStatus.Text = "";
        DeletionConfirmArea.Visibility = Visibility.Collapsed; DeletionPanel.Visibility = Visibility.Collapsed;
    }
    private void DeletionClose_Click(object sender, RoutedEventArgs e)
    {
        if (deletionBusy) { deletionCancellation?.Cancel(); DeletionStatus.Text = keyMigrationBusy ? "正在停止迁移；若已开始提交，将先完成提交或回退。" : "已请求停止；若已开始密钥阶段，会先逐份尝试清除全部密钥。已清除内容不能恢复。"; }
        else { CloseDeletion(); SetUnlockText(); UpdateActions(); }
    }
    private void DeletionClosing(object? sender, CancelEventArgs e)
    {
        if (!deletionBusy) return;
        e.Cancel = true; closeAfterDeletion = true; deletionCancellation?.Cancel();
        DeletionPassword.Clear(); DeletionStatus.Text = keyMigrationBusy ? "正在安全结束迁移，完成后关闭。" : "正在停止本地操作，完成后关闭。已经删除的文件不会恢复。";
    }
    private void UpdateDeletionActions()
    {
        DeletionButton.IsEnabled = !busy && !cacheCleaning && !deletionBusy;
        DeletionExecuteButton.IsEnabled = !deletionBusy && deletionPlan != null && DeletionAcknowledged.IsChecked == true && DeletionPhrase.Text == deletionPlan.Phrase;
        DeletionProfileButton.IsEnabled = !deletionBusy && (!IsNew || ProfileDeletion.Pending(dataRoot));
        KeyEnableButton.IsEnabled = !deletionBusy && !IsNew && !ProfileKeys.IsStrong(dataRoot) && !ProfileDeletion.Pending(dataRoot);
        DeletionPassword.IsEnabled = !deletionBusy; DeletionPhrase.IsEnabled = !deletionBusy; DeletionAcknowledged.IsEnabled = !deletionBusy;
        DeletionCloseButton.Content = deletionBusy ? "停止操作" : "关闭";
        if (DeletionBlocksUnlock) UnlockButton.IsEnabled = false;
    }
    private void DeletionConfirm_Changed(object sender, RoutedEventArgs e)
    { if (DeletionExecuteButton != null && DeletionPhrase != null && DeletionAcknowledged != null) UpdateDeletionActions(); }
    private async void DeletionProfile_Click(object sender, RoutedEventArgs e) => await PrepareDeletionOperation(enableKeys: false);
    private async void KeyEnable_Click(object sender, RoutedEventArgs e) => await PrepareDeletionOperation(enableKeys: true);
    private void UpdateKeyStatus()
    {
        KeyStatus.Text = ProfileKeys.IsStrong(dataRoot)
            ? "本机密钥由当前 Windows 用户保护，并使用 scrypt + AES-256-GCM 加密。需要聊天口令及系统用户密钥；重装 Windows、删除用户或丢失系统密钥后可能无法恢复。"
            : "现有资料尚未绑定 Windows 用户。输入当前口令可离线升级，保留身份和聊天，并清理本次迁移旧副本。升级后需要当前 Windows 用户密钥和聊天口令；重装 Windows 或丢失系统密钥后可能永久无法恢复。本版不提供备份。";
    }
    private async Task PrepareDeletionOperation(bool enableKeys)
    {
        if (busy || cacheCleaning || deletionBusy || !captureProtected) return;
        string password = DeletionPassword.Password; DeletionPassword.Clear();
        int version = deletionVersion;
        try
        {
            if (!ProfileDeletion.Pending(dataRoot) && password.Length is < 12 or > 256) { DeletionStatus.Text = "请输入当前资料库口令后再检查。"; return; }
            if (enableKeys && (IsNew || ProfileKeys.IsStrong(dataRoot) || ProfileDeletion.Pending(dataRoot))) return;
            if (closing || version != deletionVersion || DeletionPanel.Visibility != Visibility.Visible) return;
            using var worker = core == null ? null : Process.GetProcessById(core.ProcessId);
            using var transport = tor?.ProcessId is > 0 ? Process.GetProcessById(tor.ProcessId) : null;
            if (worker != null) _ = worker.SafeHandle; if (transport != null) _ = transport.SafeHandle;
            Lock(); // Stops core, Tor, transfers and clears the chat UI before review.
            deletionBusy = true; busy = true; keyMigrationBusy = enableKeys; version = deletionVersion;
            using var cancellation = new CancellationTokenSource(); deletionCancellation = cancellation;
            DeletionPanel.Visibility = Visibility.Visible; DeletionStatus.Text = enableKeys ? "正在离线迁移加密资料，保留身份和聊天；请等待校验完成…" : "正在离线验证口令并检查本机资料…";
            UpdateActions();
            try
            {
                if (worker != null) await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
                if (transport != null) await transport.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
                if (enableKeys)
                {
                    await ProfileKeyMigration.Enable(dataRoot, password, cancellation.Token);
                    if (!closing && version == deletionVersion)
                        DeletionStatus.Text = "新版密钥保护已启用，已绑定当前 Windows 用户，身份和聊天已保留。本次迁移旧副本已清理；以前另存的副本不会被追溯升级。请保管好 Windows 用户和聊天口令。";
                    return;
                }
                var plan = await ProfileDeletion.PrepareProfile(dataRoot, password, cancellation.Token);
                if (closing || version != deletionVersion || cancellation.IsCancellationRequested) return;
                deletionPlan = plan;
                DeletionEntries.ItemsSource = plan.Entries.Select(e => e.Directory ? e.Path + "\\" : e.Path).ToArray();
                DeletionSummary.Text = "本机资料目录：\n" + dataRoot + "\n" +
                    $"共 {plan.Files} 个文件，约 {FileTransfer.SizeLabel(plan.Bytes)}。" +
                    (plan.Keys > 0 ? $"\n首先销毁 {plan.Keys} 份本机密钥文件，再完整覆写、核验并删除资料。" : "\n此清单没有独立密钥文件，将完整覆写、核验并删除资料。") + "\n成功后自动关闭应用，结束本次进程。" +
                    (plan.UnrecognizedRoots > 0 ? $"\n目录中另有 {plan.UnrecognizedRoots} 个未识别条目，将保留。" : "");
                DeletionPhraseHint.Text = "输入“" + plan.Phrase + "”确认：";
                DeletionExecuteButton.Content = plan.Phrase;
                DeletionConfirmArea.Visibility = Visibility.Visible;
                DeletionStatus.Text = "清单包含数据库、联系人和密钥、附件缓存、Tor 缓存及内部迁移或恢复副本。另存的旧 .pcbackup 备份和导出的 TXT 不会随账号自动删除。";
            }
            catch (OperationCanceledException) { if (version == deletionVersion) DeletionStatus.Text = enableKeys ? "迁移已取消，原资料保留。" : "检查已取消，没有执行删除。"; }
            catch (MigrationPathTooLongException) { if (version == deletionVersion) DeletionStatus.Text = "资料目录路径过长，已在创建迁移副本前停止，原资料未改动。请保留整个目录并联系维护者调整路径，不要删除资料。"; }
            catch (InvalidDataException) { if (version == deletionVersion) DeletionStatus.Text = "口令不正确或资料库无法打开，没有执行删除。"; }
            catch { if (version == deletionVersion) DeletionStatus.Text = enableKeys ? "迁移未完成。原资料或恢复副本已保留；请重新启动应用完成回退，不要手动删除文件。" : "无法完成检查，没有执行删除。请检查文件是否被占用、权限和格式；不支持链接或网络盘。"; }
            finally
            {
                deletionCancellation = null; deletionBusy = false; busy = false; keyMigrationBusy = false;
                UpdateKeyStatus(); SetUnlockText(); UpdateActions();
                if (closeAfterDeletion) Close();
            }
        }
        finally { password = ""; }
    }
    private async void DeletionExecute_Click(object sender, RoutedEventArgs e)
    {
        var plan = deletionPlan;
        if (plan == null || plan.Profile != dataRoot || deletionBusy || busy || !captureProtected || DeletionAcknowledged.IsChecked != true || DeletionPhrase.Text != plan.Phrase) return;
        // All scope selection and password validation preceded this irreversible step.
        string phrase = DeletionPhrase.Text; int version = deletionVersion;
        deletionBusy = true; busy = true;
        using var cancellation = new CancellationTokenSource(); deletionCancellation = cancellation;
        DeletionStatus.Text = plan.Keys > 0 ? "正在先销毁本机密钥，再覆写清理资料…" : "正在处理已确认的项目，不会放入回收站…"; UpdateActions();
        var progress = new Progress<DeletionProgress>(value =>
        {
            if (!deletionBusy || version != deletionVersion) return;
            DeletionStatus.Text = value.Stage switch
            {
                "keys" => $"正在清除密钥 {value.Completed}/{value.Total}。此阶段会先尝试全部密钥，再响应停止。",
                "verify" => "正在复查清单，确认没有应用资料残留…",
                _ => $"正在覆写并核验资料：{FileTransfer.SizeLabel(value.VerifiedBytes)} / {FileTransfer.SizeLabel(value.TotalBytes)}；已处理 {value.Completed}/{value.Total} 项。"
            };
            if (value.Failures > 0) DeletionStatus.Text += $"\n已有 {value.Failures} 项未完成，将继续尝试其他已确认项目。";
        });
        try
        {
            await Task.Run(() => ProfileDeletion.Execute(plan, phrase, true, cancellation.Token, progress));
            closeAfterDeletion = true;
            if (version != deletionVersion) return;
            DeletionStatus.Text = "清单中的本机资料已删除。新建资料库会生成新的身份和联系人连接；另存的旧备份保留。" + (plan.UnrecognizedRoots > 0 ? "未识别条目仍留在原目录。" : "");
            FooterStatus.Text = "本地删除完成 · 未联网 · 不等于介质安全擦除";
        }
        catch (DeletionIncompleteException ex)
        {
            if (version == deletionVersion) DeletionStatus.Text = $"清除未全部完成，{ex.Failures} 项无法完成核验或删除。已清除内容不能撤销。\n" +
                "应用保持离线。请检查文件占用、只读属性或附加数据流，再重新检查并确认剩余清单。不会把部分完成报告为成功。";
        }
        catch
        {
            if (version == deletionVersion)
                DeletionStatus.Text = "删除未全部完成，部分文件可能已经删除且不能撤销。\n" +
                    "应用会保持离线。请关闭占用文件的程序，再点“检查本机资料”重新确认剩余清单。\n清单已失效，不会自动重试。";
        }
        finally
        {
            deletionCancellation = null; deletionBusy = false; busy = false; deletionPlan = null;
            DeletionEntries.ItemsSource = null; DeletionSummary.Text = ""; DeletionPhrase.Clear(); DeletionAcknowledged.IsChecked = false; DeletionConfirmArea.Visibility = Visibility.Collapsed;
            UpdateKeyStatus(); SetUnlockText(); UpdateActions(); if (closeAfterDeletion) Close();
        }
    }
}
