using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]);
        var app = new Application();
        var windowType = assembly.GetType("PrivateChat.MainWindow")!;
        var window = (Window)(windowType.GetConstructors()[0].GetParameters().Length == 1 ? Activator.CreateInstance(windowType, args[1])! : Activator.CreateInstance(windowType, args[1], false, false)!);
        var input = (TextBox)window.FindName("MessageInput");
        input.IsEnabled = true;
        input.AppendText("SYNTHETIC-UNDO-SECRET");
        input.Clear();
        input.Undo();
        var data = new DataObject(DataFormats.UnicodeText, "SYNTHETIC-COPY-SECRET");
        var copy = new DataObjectCopyingEventArgs(data, false);
        input.RaiseEvent(copy);
        var policy = assembly.GetType("PrivateChat.PrivacyPolicy")!;
        var original = System.Text.Json.Nodes.JsonNode.Parse("{}");
        var cfg = (System.Text.Json.Nodes.JsonNode)policy.GetMethod("Configure")!.Invoke(null, new object[]{ original!, 59000 })!;
        cfg["smpProxyMode"] = "never";
        bool weakenedAccepted = (bool)policy.GetMethod("IsStrict")!.Invoke(null, new object[]{ cfg, 59000 })!;
        Console.WriteLine(JsonSerializer.Serialize(new {
            clearedTextRestoredByUndo = input.Text.Contains("SYNTHETIC-UNDO-SECRET"),
            standardCopyExcludesHistory = copy.DataObject.GetDataPresent("CanIncludeInClipboardHistory"),
            standardCopyExcludesCloud = copy.DataObject.GetDataPresent("CanUploadToCloudClipboard"),
            weakenedProxyPolicyAccepted = weakenedAccepted,
            testRunnerIncluded = assembly.GetType("PrivateChat.SelfTest") != null
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
