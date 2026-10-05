using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PrivateChat;

public partial class MainWindow
{
    private int routesVersion;
    private bool routesBusy;
    private CancellationTokenSource? routesCancellation;
    private async void TorRoutes_Click(object sender,RoutedEventArgs e)
    {
        if(core==null || tor?.Running!=true || busy)return;
        CloseModal(); CloseRelays(); CloseTorRoutes();
        routesCancellation=new CancellationTokenSource();
        TorRoutesPanel.Visibility=Visibility.Visible;
        TorRoutesClose.Focus();
        await RefreshTorRoutes();
    }
    private async void TorRoutesRefresh_Click(object sender,RoutedEventArgs e) => await RefreshTorRoutes();
    private void TorRoutesClose_Click(object sender,RoutedEventArgs e) { CloseTorRoutes(); TorRoutesButton.Focus(); }
    private void CloseTorRoutes()
    {
        ++routesVersion; routesCancellation?.Cancel(); routesCancellation?.Dispose(); routesCancellation=null; routesBusy=false;
        TorRoutesPanel.Visibility=Visibility.Collapsed; TorRoutesList.Children.Clear();
        TorRoutesStatus.Text=""; TorRoutesRefresh.IsEnabled=false;
    }
    private async Task RefreshTorRoutes()
    {
        var service=tor; var cancellation=routesCancellation;
        if(TorRoutesPanel.Visibility!=Visibility.Visible || routesBusy || service==null || cancellation==null)return;
        int turn=epoch, version=routesVersion;
        bool Valid()=>turn==epoch && version==routesVersion && tor==service && TorRoutesPanel.Visibility==Visibility.Visible;
        routesBusy=true; TorRoutesRefresh.IsEnabled=false;
        // Never present an earlier circuit sample as the current live path.
        TorRoutesList.Children.Clear(); TorRoutesStatus.Text="正在读取本机 Tor 的线路…";
        try
        {
            var snapshot=await service.ReadRoutes(cancellation.Token);
            if(!Valid())return;
            ShowTorRoutes(snapshot,service.Mode);
        }
        catch(OperationCanceledException) { if(Valid()){TorRoutesList.Children.Clear();TorRoutesStatus.Text="线路读取超时，请稍后刷新。未保留上次线路；聊天连接策略没有改变。";} }
        catch { if(Valid()){TorRoutesList.Children.Clear();TorRoutesStatus.Text="暂时无法读取本机 Tor 线路。可稍后刷新；不会为显示线路改用直连。";} }
        finally { if(Valid()){routesBusy=false;TorRoutesRefresh.IsEnabled=service.Running;} }
    }
    private void ShowTorRoutes(TorRouteSnapshot snapshot,string mode)
    {
        TorRoutesList.Children.Clear();
        string transport=mode switch {"obfs4"=>"obfs4 网桥","snowflake"=>"Snowflake 网桥",_=>"Tor"};
        TorRoutesStatus.Text=$"{transport} · 采样于 {snapshot.At.ToLocalTime():HH:mm:ss} · 显示 {snapshot.Circuits.Length}/{snapshot.TotalCircuits} 条线路 · {snapshot.UnassignedConnections} 个连接尚未分配线路";
        if(snapshot.Circuits.Length==0)
        {
            TorRoutesList.Children.Add(RouteText("Tor 尚未报告线路。连接建立过程中可以稍后刷新。",13,muted)); return;
        }
        foreach(var circuit in snapshot.Circuits)
        {
            string state=circuit.Status switch {"BUILT"=>"已建立","LAUNCHED"=>"开始建立","EXTENDED"=>"正在扩展","GUARD_WAIT"=>"等待入口",_=>circuit.Status};
            string purpose=circuit.Purpose switch {"GENERAL"=>"普通线路","HS_CLIENT_REND"=>"洋葱服务会合","HS_CLIENT_INTRO"=>"洋葱服务介绍点","HS_CLIENT_HSDIR"=>"洋葱服务目录","TESTING"=>"Tor 自检","MEASURE_TIMEOUT"=>"连接时间测量",_=>circuit.Purpose};
            var panel=new StackPanel();
            panel.Children.Add(new TextBlock {Text=$"线路 #{circuit.Id} · {state}",FontSize=15,FontWeight=FontWeights.SemiBold});
            panel.Children.Add(RouteText($"用途：{purpose} · 关联 {circuit.Connections} 个本机连接",11,muted,new Thickness(0,5,0,12)));
            for(int i=0;i<circuit.Hops.Length;i++)
            {
                var hop=circuit.Hops[i];
                var row=new Grid {Margin=new Thickness(0,0,0,12)};
                row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(32)}); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock{Text=(i+1).ToString("00"),Foreground=accent,FontSize=12,Margin=new Thickness(0,2,0,0)});
                var detail=new StackPanel();Grid.SetColumn(detail,1);row.Children.Add(detail);
                detail.Children.Add(new TextBlock{Text=hop.Name,FontSize=13,FontWeight=FontWeights.SemiBold});
                detail.Children.Add(RouteText(hop.Address==null ? "公告地址未提供（网桥或目录信息不可用）" : $"公告地址：{hop.Address} · 国家／地区：{hop.Country??"未知"}（估计）",11,muted,new Thickness(0,4,0,0)));
                detail.Children.Add(RouteText("指纹："+hop.Fingerprint,11,muted,new Thickness(0,3,0,0)));
                panel.Children.Add(row);
            }
            if(circuit.Hops.Length==0)panel.Children.Add(RouteText("节点尚未分配",12,muted,new Thickness(0,0,0,12)));
            panel.Children.Add(RouteText(circuit.Targets.Length==0 ? "暂无关联连接，可能是预建或内部线路。" : "关联目标（Tor 报告）：\n"+string.Join("\n",circuit.Targets),11,muted));
            TorRoutesList.Children.Add(new Border{Child=panel,BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(0,16,0,16)});
        }
    }
    private static TextBlock RouteText(string text,double size,Brush color,Thickness margin=default) => new(){Text=text,FontSize=size,Foreground=color,TextWrapping=TextWrapping.Wrap,Margin=margin};
}
