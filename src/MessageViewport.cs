using System.Windows;
using System.Windows.Controls;

namespace PrivateChat;

internal static class MessageViewport
{
    internal sealed record Anchor(long Id, double Y);
    public static Anchor? Capture(StackPanel panel, ScrollViewer scroll)
    {
        scroll.UpdateLayout();
        foreach (FrameworkElement item in panel.Children)
        {
            double y = item.TranslatePoint(new Point(), scroll).Y;
            if (item.Tag is long id && y + item.ActualHeight > 0)
                return new Anchor(id, y);
        }
        return null;
    }
    public static void Restore(StackPanel panel, ScrollViewer scroll, Anchor? anchor, double fallback)
    {
        scroll.UpdateLayout();
        var item = panel.Children.OfType<FrameworkElement>().FirstOrDefault(x => anchor != null && x.Tag is long id && id == anchor.Id);
        scroll.ScrollToVerticalOffset(item != null ? scroll.VerticalOffset + item.TranslatePoint(new Point(), scroll).Y - anchor!.Y : fallback);
    }
}
