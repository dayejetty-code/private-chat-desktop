using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PrivateChat;

internal sealed class PrivateClipboard : IDisposable
{
    private const string OwnerFormat = "PrivateChat.EphemeralClipboardOwner";
    private readonly byte[] owner = Guid.NewGuid().ToByteArray();
    private readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromSeconds(30) };
    public PrivateClipboard() => expiry.Tick += (_, _) => ClearOwned();
    public void Attach(TextBox box)
    {
        box.IsUndoEnabled = false;
        box.AllowDrop = false;
        DataObject.AddCopyingHandler(box, (_, e) =>
        {
            if (e.IsDragDrop || !e.DataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
            AddPrivacyFormats(e.DataObject);
            expiry.Stop(); expiry.Start();
        });
    }
    internal DataObject CreateData(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        AddPrivacyFormats(data);
        return data;
    }
    private void AddPrivacyFormats(IDataObject data)
    {
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
        data.SetData(OwnerFormat, new MemoryStream(owner.ToArray()));
    }
    public bool Copy(string text)
    {
        try { Clipboard.SetDataObject(CreateData(text), false); expiry.Stop(); expiry.Start(); return true; }
        catch { return false; }
    }
    public void ClearOwned()
    {
        expiry.Stop();
        try
        {
            // Do not read unrelated clipboard text or clear another application's copy.
            if (Clipboard.GetData(OwnerFormat) is MemoryStream marker && marker.ToArray().SequenceEqual(owner)) Clipboard.Clear();
        }
        catch { }
    }
    public void Dispose() => ClearOwned();
}
