using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace PrivateChat;

public partial class MainWindow
{
    private int relayVersion;
    private bool relayLoaded;
    private string? testedRelayFingerprint;
    private int testedRelayPort;
    private static string RelayFingerprint(string[] addresses) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',addresses))));
    private bool RelayActive(int turn, int version, CoreClient current) => epoch == turn && relayVersion == version && core == current && RelayPanel.Visibility == Visibility.Visible;
    private void RelayInput_Changed(object sender, TextChangedEventArgs e)
    {
        testedRelayFingerprint = null;
        if (relayLoaded && RelayStatus != null) RelayStatus.Text = "列表已修改，请重新测试。";
        UpdateRelayActions();
    }
    private void UpdateRelayActions()
    {
        if (RelaySaveButton == null) return;
        RelayInput.IsEnabled = !busy && relayLoaded;
        RelayTestButton.IsEnabled = !busy && relayLoaded && CanNetwork && !string.IsNullOrWhiteSpace(RelayInput.Text);
        RelaySaveButton.IsEnabled = !busy && relayLoaded && CanNetwork && testedRelayFingerprint != null && tor?.Port == testedRelayPort;
        RelayRestoreButton.IsEnabled = !busy && relayLoaded && core != null;
    }
    private void ShowRelayConfiguration(JsonArray config)
    {
        bool presets = RelaySettings.IsPresetConfiguration(config);
        var enabled = RelaySettings.Enabled(config);
        RelayCurrent.Text = $"当前：{(presets ? "预设中继" : "自选中继")} · {enabled.Length} 个已启用 · 始终经过 Tor";
        RelayInput.Text = string.Join(Environment.NewLine,enabled);
        testedRelayFingerprint = null;
        relayLoaded = true;
    }
    private async void RelaySettings_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || busy) return;
        CloseModal(); CloseRelays();
        int turn = epoch, version = relayVersion; var current = core;
        RelayPanel.Visibility = Visibility.Visible; RelayCurrent.Text = "正在读取配置…"; busy = true; UpdateActions();
        try
        {
            var config = await RelaySettings.Read(current,userId);
            if (!RelayActive(turn,version,current)) return;
            ShowRelayConfiguration(config);
            RelayStatus.Text = CanNetwork ? "填入服务器提供的完整地址，然后测试连接。" : "可恢复预设；测试自定义中继需要先建立 Tor 通道。";
        }
        catch { if (RelayActive(turn,version,current)) RelayStatus.Text = "配置读取失败，请关闭后重新打开。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private async void RelayTest_Click(object sender, RoutedEventArgs e)
    {
        if (core == null || !CanNetwork || busy || !relayLoaded) return;
        string[] addresses;
        try { addresses = RelaySettings.Lines(RelayInput.Text); }
        catch (ArgumentException ex) { RelayStatus.Text = ex.Message; return; }
        int turn = epoch, version = relayVersion, port = tor!.Port; var current = core; long user = userId;
        testedRelayFingerprint = null; busy = true; UpdateActions();
        try
        {
            for (int i=0;i<addresses.Length;i++)
            {
                if (!RelayActive(turn,version,current) || !CanNetwork) return;
                RelayStatus.Text = $"正在经 Tor 测试第 {i+1}/{addresses.Length} 个中继…";
                bool passed = await RelaySettings.Test(current,user,addresses[i],port);
                if (!RelayActive(turn,version,current)) return;
                if (!passed) { RelayStatus.Text = $"第 {i+1} 个中继测试未通过。请检查地址、访问密码或网络后重试；当前配置未修改。"; return; }
            }
            testedRelayFingerprint = RelayFingerprint(addresses); testedRelayPort = port;
            RelayStatus.Text = $"{addresses.Length} 个中继测试通过。点击“启用此列表”后生效；测试成功不代表后续始终可用。";
        }
        catch (RelayPolicyException) { if(turn == epoch) { Lock(); ShowUnlockError("网络隐私配置校验失败，已锁定。请重新解锁。"); } }
        catch (ArgumentException ex) { if (RelayActive(turn,version,current)) RelayStatus.Text = ex.Message; }
        catch { if (RelayActive(turn,version,current)) RelayStatus.Text = "测试未完成。当前配置未修改，请检查 Tor 和服务器后重试。"; }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private async void RelaySave_Click(object sender, RoutedEventArgs e) => await SaveRelays(false);
    private async void RelayRestore_Click(object sender, RoutedEventArgs e) => await SaveRelays(true);
    private async Task SaveRelays(bool presets)
    {
        if (core == null || busy || !relayLoaded || tor == null) return;
        string[]? addresses = null;
        if (!presets)
        {
            try { addresses = RelaySettings.Lines(RelayInput.Text); }
            catch(ArgumentException ex) { RelayStatus.Text = ex.Message; return; }
            if (!CanNetwork || testedRelayPort != tor.Port || testedRelayFingerprint != RelayFingerprint(addresses))
            { RelayStatus.Text = "请先测试当前列表，再启用。"; return; }
        }
        int turn = epoch, version = relayVersion, port = tor.Port; var current = core; long user = userId;
        busy = true; RelayStatus.Text = "正在保存并读取确认…"; UpdateActions();
        try
        {
            var saved = await RelaySettings.Save(current,user,addresses,port,() => RelayActive(turn,version,current));
            if (!RelayActive(turn,version,current)) return;
            ShowRelayConfiguration(saved);
            RelayStatus.Text = presets ? "已启用全部内置预设中继，并移除自定义条目。已有联系人仍保留原连接。" : "已启用并读取确认。新建连接使用此列表，已有联系人不会自动迁移。";
        }
        catch (RelayPolicyException) { if(turn == epoch) { Lock(); ShowUnlockError("网络隐私配置校验失败，已锁定。请重新解锁。"); } }
        catch(ArgumentException ex) { if (RelayActive(turn,version,current)) RelayStatus.Text = ex.Message; }
        catch(OperationCanceledException) { }
        catch { if (RelayActive(turn,version,current)) { testedRelayFingerprint = null; relayLoaded = false; RelayStatus.Text = "保存结果未确认，请关闭后重新打开设置，核对当前实际配置。"; } }
        finally { if (turn == epoch) { busy = false; UpdateActions(); } }
    }
    private void CloseRelays()
    {
        ++relayVersion; relayLoaded = false; testedRelayFingerprint = null; testedRelayPort = 0;
        RelayInput.Clear(); RelayCurrent.Text = ""; RelayStatus.Text = ""; RelayPanel.Visibility = Visibility.Collapsed;
    }
    private void RelayClose_Click(object sender, RoutedEventArgs e) => CloseRelays();
}
