using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PrivateChat;

public partial class MainWindow
{
    private long fileContextVersion;
    private readonly List<(Button Button, string Action)> fileButtons = new();
    private void UpdateFileActions()
    {
        bool verified = selected is { Ready: true, Verified: true };
        FileButton.IsEnabled = CanNetwork && verified && !busy;
        foreach (var (button, action) in fileButtons)
            button.IsEnabled = core != null && !busy && (action == "cancel" || (verified && (action == "save" || CanNetwork)));
    }
    private bool FileContext(int turn, long version, CoreClient client, long contact) => turn == epoch && version == fileContextVersion && core == client && selected?.Id == contact;
    private async Task<bool> VerifiedForFile(CoreClient client, long contact, Func<bool> valid)
    {
        var response = await client.Result($"/_get code @{contact}");
        if (!valid()) return false;
        if (PrivacyPolicy.VerifiedContact(response["contact"])) return true;
        FooterStatus.Text = "安全码未核验或已变化，请先重新核验。";
        return false;
    }
    private async void File_Click(object sender, RoutedEventArgs e)
    {
        var current = core; var contact = selected; int turn = epoch; long version = fileContextVersion;
        if (!CanNetwork || current == null || contact is not { Ready: true, Verified: true } || busy) return;
        bool Valid() => FileContext(turn, version, current, contact.Id) && CanNetwork;
        JsonNode? source = null; bool submitted = false;
        busy = true; UpdateActions();
        try
        {
            var picker = new OpenFileDialog { Title = "选择纯文本文件（.txt，最多 25 MB）", Filter = TextDocument.Filter, DefaultExt = ".txt", Multiselect = false, CheckFileExists = true, DereferenceLinks = false, AddToRecent = false };
            if (picker.ShowDialog(this) != true || !Valid()) return;
            string path = FileTransfer.LocalPath(picker.FileName);
            TextDocument.RequireTextExtension(path);
            if (FileTransfer.Within(dataRoot, path)) throw new IOException();
            var info = new FileInfo(path);
            if (info.Length > FileTransfer.MaximumBytes) { FooterStatus.Text = "单个文件最多 25 MB。"; return; }
            string name = FileTransfer.NewTextName();
            if (MessageBox.Show(this, $"发送给：{contact.Name}\n\n发送名称：{name}\n原文件大小：{FileTransfer.SizeLabel(info.Length)}\n\n已使用随机文件名，原文件名不发送，本机文件不重命名。仅传送纯文本，统一为 UTF-8 编码；正文中的姓名、地址等信息会保留，对方可保存或转发。", "发送文本文档", MessageBoxButton.OKCancel, MessageBoxImage.None) != MessageBoxResult.OK || !Valid()) return;
            if (!await VerifiedForFile(current, contact.Id, Valid)) return;
            FooterStatus.Text = "正在检查纯文本并加密…";
            source = await current.EncryptFile(path);
            if (!Valid() || !await VerifiedForFile(current, contact.Id, Valid)) return;
            submitted = true; // A timed-out response may still represent a queued transfer.
            await current.SendFile(contact.Id, source, name);
            if (Valid()) { await RefreshMessages(); FooterStatus.Text = "文件已交给加密核心，上传状态见文件消息。"; }
        }
        catch (TextDocumentException ex) { if (FileContext(turn, version, current, contact.Id)) FooterStatus.Text = ex.UserMessage; }
        catch (CacheCapacityException) { submitted = false; if (FileContext(turn, version, current, contact.Id)) FooterStatus.Text = "附件空间不足（含在途预留）。请打开“附件缓存”清理后重试。"; }
        catch { if (FileContext(turn, version, current, contact.Id)) FooterStatus.Text = submitted ? "发送结果尚未确认，请先查看文件消息，避免重复发送。" : "文件准备失败。请选择本机 .txt 文件，并检查读取权限和磁盘空间（缓存上限 512 MB）。"; }
        finally
        {
            if (!submitted && source?["filePath"] is JsonNode filePath)
            { try { File.Delete(FileTransfer.CachedPath(dataRoot, filePath.ToString())); } catch { } }
            source?.AsObject().Clear();
            if (turn == epoch) { busy = false; UpdateActions(); }
        }
    }
    private Border CreateFileBubble(JsonNode item, string label, bool sent, long itemId)
    {
        var file = item["file"]!;
        string name = FileTransfer.DisplayName(item);
        var bubble = CreateMessageBubble(name, FileTransfer.SizeLabel(FileTransfer.Size(file)) + " · " + FileTransfer.StatusLabel(file) + "\n" + label, sent, itemId);
        var panel = (StackPanel)bubble.Child;
        ((TextBlock)panel.Children[1]).TextWrapping = TextWrapping.Wrap;
        bool textFile = FileTransfer.IsTextItem(item);
        if (!FileTransfer.Supported(file) || !textFile)
        {
            panel.Children.Add(new TextBlock { Text = "仅支持 25 MB 以内的 .txt 纯文本附件。", Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,0) });
            // An old pending attachment can still be stopped after upgrading.
            if (!FileTransfer.Cancellable(file)) return bubble;
        }
        bool present = HasCachedFile(file);
        if (textFile && FileTransfer.Exportable(file) && !present)
            panel.Children.Add(new TextBlock { Text = "本机副本已清理或缺失，无法另存为。", Foreground = muted, FontSize = 11, Margin = new Thickness(0,10,0,0) });
        string? action = textFile && FileTransfer.Receivable(file) ? "receive" : textFile && FileTransfer.Exportable(file) && present ? "save" : FileTransfer.Cancellable(file) ? "cancel" : null;
        if (action == null || selected == null) return bubble;
        long contactId = selected.Id;
        var button = new Button { Content = action == "receive" ? "接收文件" : action == "save" ? "另存为…" : "取消传输", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(10,6,10,6), Margin = new Thickness(0,10,0,0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => await FileAction(action, contactId, itemId, name);
        fileButtons.Add((button, action)); panel.Children.Add(button);
        return bubble;
    }
    private async Task FileAction(string action, long contactId, long itemId, string name)
    {
        var current = core; int turn = epoch; long version = fileContextVersion;
        if (action is not ("receive" or "save" or "cancel") || current == null || busy || selected?.Id != contactId) return;
        if (action != "cancel" && (selected is not { Verified: true, Ready: true } || (action == "receive" && !CanNetwork))) return;
        bool Valid() => FileContext(turn, version, current, contactId);
        busy = true; UpdateActions();
        try
        {
            if (action != "cancel" && !await VerifiedForFile(current, contactId, Valid)) return;
            var item = await FileTransfer.FreshItem(current, contactId, itemId);
            if (!Valid()) return;
            if (action != "cancel" && !FileTransfer.IsTextItem(item)) { FooterStatus.Text = "仅支持 .txt 纯文本附件。"; return; }
            var file = item["file"]!;
            name = FileTransfer.DisplayName(item);
            if (action == "receive")
            {
                if (!CanNetwork || !FileTransfer.Receivable(file)) return;
                await FileTransfer.Receive(current, dataRoot, file);
                if (Valid()) FooterStatus.Text = "正在接收加密文件，完成后可另存为。";
            }
            else if (action == "cancel")
            {
                if (!FileTransfer.Cancellable(file)) return;
                try { await current.Result("/fcancel " + FileTransfer.Id(file)); }
                catch (CoreException)
                {
                    // The core can cancel locally before its peer notification fails.
                    var after = await FileTransfer.FreshFile(current, contactId, itemId);
                    if (FileTransfer.Status(after) is not ("sndCancelled" or "rcvCancelled" or "rcvInvitation")) throw;
                }
                if (Valid()) FooterStatus.Text = "已请求取消传输。对方已保存的副本无法撤回。";
            }
            else if (action == "save")
            {
                if (!FileTransfer.Exportable(file)) return;
                var picker = new SaveFileDialog { Title = "另存为纯文本（保存后不受资料库口令保护）", FileName = name, Filter = TextDocument.Filter, DefaultExt = ".txt", AddExtension = true, AddToRecent = false, OverwritePrompt = false, DereferenceLinks = false };
                if (picker.ShowDialog(this) != true || !Valid()) return;
                string target = FileTransfer.LocalPath(picker.FileName);
                TextDocument.RequireTextExtension(target);
                if (File.Exists(target) || Directory.Exists(target)) { FooterStatus.Text = "该名称已存在，请使用新的文件名，避免覆盖。"; return; }
                if (FileTransfer.Within(dataRoot, target)) { FooterStatus.Text = "请选择聊天资料库以外的保存位置。"; return; }
                await current.ExportFile(file["fileSource"]!, FileTransfer.Size(file), target);
                if (Valid()) FooterStatus.Text = "已保存为 UTF-8 纯文本。保存的普通文件不再受资料库口令保护；请仅打开可信来源的文件。";
            }
            if (Valid()) await RefreshMessages();
        }
        catch (TextDocumentException ex) { if (Valid()) FooterStatus.Text = ex.UserMessage; }
        catch (CacheCapacityException) { if (Valid()) FooterStatus.Text = "附件空间不足（含在途预留）。请打开“附件缓存”清理后重试。"; }
        catch (CoreException) { if (Valid()) FooterStatus.Text = "文件操作未完成。文件可能已过期、被取消，或其文件中继不在当前受信任列表；没有改为直连。"; }
        catch { if (Valid()) FooterStatus.Text = "文件操作失败。请检查磁盘空间、保存位置和文件状态；不会自动打开文件。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private bool HasCachedFile(JsonNode file)
    {
        try { return file["fileSource"]?["filePath"] is JsonNode path && File.Exists(FileTransfer.CachedPath(dataRoot, path.ToString())); }
        catch (IOException) { return false; }
    }
}
