using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PrivateChat;

public partial class MainWindow
{
    private bool backupRunning, closeAfterBackup;
    private CancellationTokenSource? backupCancellation;

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || cacheCleaning || backupRunning || DeletionBlocksUnlock || !captureProtected || Directory.Exists(Path.Combine(dataRoot, ".restore-transaction"))) return;
        BackupPassword.Clear(); BackupStatus.Text = "";
        BackupCreateButton.IsEnabled = !IsNew;
        BackupRestoreButton.IsEnabled = true;
        BackupPanel.Visibility = Visibility.Visible; BackupPassword.Focus();
    }
    private void CloseBackup()
    {
        backupCancellation?.Cancel(); BackupPassword.Clear();
        BackupPanel.Visibility = Visibility.Collapsed; BackupStatus.Text = "";
    }
    private void BackupClose_Click(object sender, RoutedEventArgs e)
    {
        if (backupRunning) { backupCancellation?.Cancel(); BackupStatus.Text = "正在安全结束操作，请稍候…"; }
        else CloseBackup();
    }
    private void BackupClosing(object? sender, CancelEventArgs e)
    {
        if (!backupRunning) return;
        e.Cancel = true; closeAfterBackup = true; backupCancellation?.Cancel();
        BackupPassword.Clear(); BackupStatus.Text = "正在安全结束操作，完成后关闭…";
    }
    private async void BackupCreate_Click(object sender, RoutedEventArgs e) => await RunBackup(restore: false);
    private async void BackupRestore_Click(object sender, RoutedEventArgs e) => await RunBackup(restore: true);

    private async Task RunBackup(bool restore)
    {
        if (backupRunning || busy || cacheCleaning || DeletionBlocksUnlock || !captureProtected || (!restore && IsNew) || Directory.Exists(Path.Combine(dataRoot, ".restore-transaction"))) return;
        string password = BackupPassword.Password; BackupPassword.Clear();
        if (password.Length is < 12 or > 256) { BackupStatus.Text = "请输入至少 12 个字符的口令。恢复时使用创建备份当时的资料库口令。"; return; }
        string? path = null;
        try
        {
            if (restore)
            {
                var dialog = new OpenFileDialog { Title = "选择本地加密备份", Filter = "Private Chat 加密备份|*.pcbackup", CheckFileExists = true, Multiselect = false };
                if (dialog.ShowDialog(this) != true) return;
                path = ProfileBackup.BackupPath(dialog.FileName, dataRoot);
                if (MessageBox.Show(this, "恢复会替换本机当前的联系人和聊天记录，不会合并。原资料会保留为本机加密副本。\n\n请先关闭原设备上的这个资料库。不要同时使用同一份资料；旧备份可能需要修复联系人连接，备份之后的消息无法从中找回。\n\n是否继续校验并恢复？", "恢复本地备份", MessageBoxButton.OKCancel, MessageBoxImage.None) != MessageBoxResult.OK) return;
            }
            else
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateChatBackups");
                FileTransfer.LocalPath(folder); Directory.CreateDirectory(folder);
                var dialog = new SaveFileDialog { Title = "保存本地加密备份", InitialDirectory = folder, FileName = "PrivateChat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".pcbackup", Filter = "Private Chat 加密备份|*.pcbackup", DefaultExt = ".pcbackup", AddExtension = true, OverwritePrompt = true };
                if (dialog.ShowDialog(this) != true) return;
                path = ProfileBackup.BackupPath(dialog.FileName, dataRoot);
                if (File.Exists(path)) { BackupStatus.Text = "为保留已有备份，请换一个文件名。"; return; }
            }
            // A lock/sleep event may have arrived while a native dialog was open.
            if (closing || BackupPanel.Visibility != Visibility.Visible) return;
            using var worker = core == null ? null : Process.GetProcessById(core.ProcessId);
            if (worker != null) _ = worker.SafeHandle;
            Lock(); // Stop network and the old worker before any snapshot can be made.
            backupRunning = true; busy = true;
            using var cancellation = new CancellationTokenSource(); backupCancellation = cancellation;
            int turn = epoch;
            BackupPanel.Visibility = Visibility.Visible;
            BackupPassword.IsEnabled = false; BackupCreateButton.IsEnabled = false; BackupRestoreButton.IsEnabled = false;
            BackupCloseButton.Content = "取消"; UnlockButton.IsEnabled = false; UpdateActions();
            BackupStatus.Text = restore ? "正在本机解密、校验和恢复…" : "正在本机创建并校验加密备份…";
            try
            {
                if (worker != null) await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
                string? previous = null;
                if (restore) previous = await ProfileBackup.Restore(dataRoot, path, password, cancellation.Token);
                else await ProfileBackup.Create(dataRoot, path, password, cancellation.Token);
                if (turn == epoch && !closing)
                {
                    BackupStatus.Text = restore
                        ? "恢复完成。请用备份当时的口令解锁。" + (previous == null ? "" : "\n原资料保留在：" + previous)
                        : "备份已保存并通过完整性校验：\n" + path + "\n请妥善保管口令，遗忘后无法恢复。";
                    FooterStatus.Text = restore ? "本地恢复完成 · 解锁后才会联网" : "本地备份完成 · 未上传";
                }
            }
            catch (OperationCanceledException) { if (turn == epoch) BackupStatus.Text = "操作已取消。未完成的备份不会作为有效备份保存。"; }
            catch (BackupBusyFilesException) { if (turn == epoch) BackupStatus.Text = "还有未结束的文件任务。请解锁后完成或取消这些任务，再创建备份。"; }
            catch
            {
                if (turn == epoch) BackupStatus.Text = "操作未完成。请检查口令、备份完整性、磁盘空间及本地目录权限。\n只支持未同步的本地目录或 U 盘；请勿删除原资料。";
            }
            finally
            {
                backupCancellation = null; backupRunning = false; busy = false;
                BackupPassword.Clear(); BackupPassword.IsEnabled = true;
                BackupCloseButton.Content = "关闭"; BackupCreateButton.IsEnabled = !IsNew; BackupRestoreButton.IsEnabled = true;
                SetUnlockText(); UnlockButton.IsEnabled = true; UpdateActions();
                // If recovery itself failed, opening a mixed profile must remain impossible.
                if (Directory.Exists(Path.Combine(dataRoot, ".restore-transaction")))
                { UnlockButton.IsEnabled = false; BackupButton.IsEnabled = false; BackupCreateButton.IsEnabled = false; BackupRestoreButton.IsEnabled = false; ShowUnlockError("恢复尚未完成。请关闭并重新打开应用，让它回退；请保留整个资料目录。"); }
                if (closeAfterBackup) Close();
            }
        }
        catch { if (!closing) BackupStatus.Text = "无法使用这个位置。请选择未同步的本地目录或 U 盘，并检查访问权限。"; }
        finally { password = ""; }
    }
}
