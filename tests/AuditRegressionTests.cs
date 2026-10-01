using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PrivateChat;

// Fault injection is confined to the QA assembly and synthetic profiles.
internal static class AuditRegressionTests
{
    public static void Run(MainWindow window, Action unlock, Action<bool, string> assert)
    {
        var results = new List<(bool Passed, string Name)>();
        void Check(bool passed, string name) => results.Add((passed, name));
        T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        void Call(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, Array.Empty<object>());
        var input = (TextBox)window.FindName("ModalInput");
        var modal = (UIElement)window.FindName("Modal");
        var contacts = (ListBox)window.FindName("ContactsList");

        unlock();
        var timer = Field<DispatcherTimer>(window, "timer");
        timer.Stop();
        var core = Field<CoreClient>(window, "core");
        var pending = Field<ConcurrentDictionary<long, TaskCompletionSource<JsonNode>>>(core, "pending");
        var gate = Field<SemaphoreSlim>(core, "writeGate");
        contacts.ItemsSource = new[] { new ContactRow(90001, "Synthetic verification A", "", false, true), new ContactRow(90002, "Synthetic verification B", "", false, true) };
        contacts.SelectedIndex = 0;
        if (!Pump(() => pending.IsEmpty && !Field<bool>(window, "historyLoading"), 10)) throw new Exception("Synthetic conversation did not settle");

        // Hold the real IPC write queue, issue the UI request, close/change the view,
        // then deliver a synthetic successful response through its actual pending task.
        foreach (bool switchAwayAndBack in new[] { false, true })
        {
            gate.Wait();
            TaskCompletionSource<JsonNode> reply;
            try
            {
                ((Button)window.FindName("VerifyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                reply = pending.Values.Single();
                if (switchAwayAndBack) { contacts.SelectedIndex = 1; contacts.SelectedIndex = 0; }
                else Call("CloseModal");
                reply.TrySetResult(new JsonObject { ["result"] = new JsonObject { ["connectionCode"] = "11111 22222 33333 44444 55555 66666" } });
            }
            finally { gate.Release(); }
            if (!Pump(() => pending.IsEmpty && !Field<bool>(window, "historyLoading"), 10)) throw new Exception("Delayed verification response did not settle");
            Drain();
            Check(modal.Visibility == Visibility.Collapsed && input.Text.Length == 0,
                switchAwayAndBack ? "Old safety code cannot reopen after switching away and back" : "Closing dialog discards delayed safety-code response");
            Call("CloseModal");
        }
        window.EmergencyLock();
        timer.Start();

        foreach (var mode in new[] { PowerModes.Suspend, PowerModes.Resume })
        {
            unlock();
            int pid = Field<CoreClient>(window, "core").ProcessId;
            ((TextBox)window.FindName("MessageInput")).Text = "SYNTHETIC-POWER-SECRET";
            var handler = typeof(MainWindow).GetMethod("PowerChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            handler?.Invoke(window, new object[] { window, new PowerModeChangedEventArgs(mode) });
            bool locked = handler != null && Pump(() => Field<CoreClient?>(window, "core") == null && !Alive(pid), 5);
            Check(locked && ((TextBox)window.FindName("MessageInput")).Text.Length == 0,
                "Power " + mode + " notification locks UI and terminates actual core");
            window.EmergencyLock();
        }

        unlock();
        var disconnected = Field<CoreClient>(window, "core");
        int disconnectedPid = disconnected.ProcessId;
        Field<Process>(disconnected, "process").StandardOutput.Dispose();
        // Windows anonymous-pipe reads can stay pending after local disposal.
        // A response wakes that read so the reader observes the broken stream.
        _ = disconnected.Command("/u").ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        Check(Pump(() => Field<CoreClient?>(window, "core") == null && !Alive(disconnectedPid), 5),
            "Broken core response pipe terminates core and locks actual UI");
        window.EmergencyLock();
        File.WriteAllText(Path.Combine(RuntimeSecurity.Base, "audit-regressions.json"), new JsonArray(results.Select(r => (JsonNode)new JsonObject { ["passed"] = r.Passed, ["check"] = r.Name }).ToArray()).ToJsonString(new() { WriteIndented = true }));
        foreach (var result in results) assert(result.Passed, result.Name);
    }

    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; } }
    private static bool Pump(Func<bool> condition, int seconds)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        return condition();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
