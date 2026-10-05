using System.IO;
using System.Text.Json;

namespace PrivateChat;

// Caller owns instance.lock. Fixed paths only; the lock and Tor state never move.
internal static class ProfileRestore
{
    private const string Transaction = ".restore-transaction";
    private sealed record Journal(string[] Originals, string RecoveryName, bool DiscardPrevious = false);

    internal static string? Install(string root, string stage, bool discardPrevious = false)
    {
        Recover(root);
        string tx = FileTransfer.LocalPath(Path.Combine(root, Transaction));
        Directory.CreateDirectory(tx);
        string previous = Path.Combine(tx, "previous"); Directory.CreateDirectory(previous);
        var originals = ProfileBackup.Roots.Where(n => Exists(Path.Combine(root, n))).ToArray();
        var journal = new Journal(originals, "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"), discardPrevious);
        WriteDurable(Path.Combine(tx, "pending.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
        try
        {
            foreach (string name in originals) Move(FileTransfer.LocalPath(Path.Combine(root, name)), Path.Combine(previous, name));
            foreach (string name in ProfileBackup.Roots)
            {
                string from = FileTransfer.LocalPath(Path.Combine(stage, name));
                if (Exists(from)) Move(from, FileTransfer.LocalPath(Path.Combine(root, name)));
            }
            WriteDurable(Path.Combine(tx, "committed"), "committed"u8.ToArray());
        }
        catch { Recover(root); throw; }
        return Recover(root);
    }

    // Invoked before any profile opens, including after a crash halfway through restore.
    internal static string? Recover(string root)
    {
        if (ProfileDeletion.Pending(root)) throw new IOException("Deletion is incomplete");
        root = FileTransfer.LocalPath(root);
        string tx = FileTransfer.LocalPath(Path.Combine(root, Transaction));
        if (!Directory.Exists(tx)) return null;
        string pending = FileTransfer.LocalPath(Path.Combine(tx, "pending.json"));
        if (!File.Exists(pending)) { ProfileBackup.DeleteOwned(root, tx); return null; }
        if (new FileInfo(pending).Length > 8192) throw new InvalidDataException("Invalid recovery journal");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(pending)) ?? throw new InvalidDataException();
        if (journal.Originals == null || journal.Originals.Length > ProfileBackup.Roots.Count() || journal.Originals.Distinct().Count() != journal.Originals.Length || journal.Originals.Any(n => !ProfileBackup.Roots.Contains(n)) || !System.Text.RegularExpressions.Regex.IsMatch(journal.RecoveryName ?? "", @"\Abefore-restore-\d{8}-\d{6}-[a-f0-9]{32}\z")) throw new InvalidDataException("Invalid recovery journal");
        string previous = FileTransfer.LocalPath(Path.Combine(tx, "previous"));
        if (journal.DiscardPrevious && File.Exists(FileTransfer.LocalPath(Path.Combine(tx, "committed"))))
        {
            // The replacement was validated before install. Only this transaction's
            // old files may be cleared. A failed cleanup retains the committed journal
            // so startup retries cleanup, never resurrects the previous weaker key.
            if (Directory.Exists(previous))
            {
                var plan = ProfileDeletion.InspectProfile(previous);
                if (plan.UnrecognizedRoots != 0) throw new IOException("Unexpected migration recovery files");
                ProfileDeletion.Execute(plan, plan.Phrase, true, CancellationToken.None);
            }
            ProfileBackup.DeleteOwned(root, tx);
            return null;
        }
        if (!File.Exists(FileTransfer.LocalPath(Path.Combine(tx, "committed"))))
        {
            foreach (string name in ProfileBackup.Roots)
            {
                string original = FileTransfer.LocalPath(Path.Combine(previous, name)), live = FileTransfer.LocalPath(Path.Combine(root, name));
                if (Exists(original))
                { ProfileBackup.DeleteOwned(root, live); Copy(original, live); }
                else if (!journal.Originals.Contains(name)) ProfileBackup.DeleteOwned(root, live);
                else if (!Exists(live)) throw new IOException("Original profile missing during recovery");
            }
            // Keep rollback idempotent across failures until every original has returned.
            WriteDurable(Path.Combine(tx, "rolled-back"), "rolled-back"u8.ToArray());
        }
        string history = FileTransfer.LocalPath(Path.Combine(root, ".restore-history")); Directory.CreateDirectory(history);
        string destination = FileTransfer.LocalPath(Path.Combine(history, journal.RecoveryName!));
        Directory.Move(tx, destination);
        return journal.Originals.Length == 0 ? null : Path.Combine(destination, "previous");
    }

    internal static void Startup(string root)
    {
        if (ProfileDeletion.Pending(root)) throw new IOException("Deletion is incomplete; restore recovery must not resurrect old data");
        Recover(root);
        foreach (string stage in Directory.EnumerateDirectories(root, ".backup-work-*"))
            if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(stage), @"\A\.backup-work-[a-f0-9]{32}\z")) ProfileBackup.DeleteOwned(root, stage);
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void Move(string source, string target)
    { if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); }
    private static void Copy(string source, string target)
    {
        FileTransfer.LocalPath(source); FileTransfer.LocalPath(target);
        if (Directory.Exists(source))
        { Directory.CreateDirectory(target); foreach (string child in Directory.EnumerateFileSystemEntries(source)) Copy(child, Path.Combine(target, Path.GetFileName(child))); }
        else
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output); output.Flush(true);
        }
    }
    private static void WriteDurable(string target, byte[] bytes)
    {
        // A complete, flushed temp file is renamed before any live data is moved.
        string part = target + ".tmp";
        using (var output = new FileStream(FileTransfer.LocalPath(part), FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
        File.Move(part, FileTransfer.LocalPath(target), overwrite: true);
    }
}
