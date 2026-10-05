using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PrivateChat;

internal static class ProfileKeyMigration
{
    private const string ExportDirectory = "rekey";
    // The pinned SQLite Windows VFS still has a MAX_PATH-sized pathname budget.
    // Reserve its journal suffix, not just the final database filename.
    internal static void RequireMigrationPath(string profile)
        => RequireExportPath(Path.Combine(profile, ".backup-work-" + new string('0', 32)));
    private static void RequireExportPath(string stage)
    {
        if (Path.Combine(stage, ExportDirectory, "chat_agent.db-journal").Length >= 260)
            throw new MigrationPathTooLongException();
    }
    // Only a disposable, bounded staging profile may be rewritten. The live
    // profile is installed with the existing durable restore transaction.
    internal static async Task RotateStage(string stage, string password, CancellationToken token)
    {
        stage = FileTransfer.LocalPath(stage);
        if (!Regex.IsMatch(Path.GetFileName(stage), @"\A\.backup-work-[a-f0-9]{32}\z")) throw new IOException("Rekey requires an owned staging profile");
        RequireExportPath(stage);
        using var pins = new DeletionFile.Pins(stage, true);
        string key = ""; byte[] random = RandomNumberGenerator.GetBytes(32);
        // The enclosing stage already has a random identity; a second GUID only
        // consumes native database path budget. Never reuse an existing export.
        string exported = Path.Combine(stage, ExportDirectory);
        if (Directory.Exists(exported) || File.Exists(exported)) throw new IOException("Migration export already exists");
        Directory.CreateDirectory(exported);
        try
        {
            key = Convert.ToHexString(random);
            ProfileKeys.Write(exported, password, random);
            using (var client = new CoreClient())
            {
                using var worker = Process.GetProcessById(client.ProcessId); _ = worker.SafeHandle;
                using var cancellation = token.Register(client.Dispose);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if ((await client.Init(Path.Combine(stage, "chat"), password))["type"]?.ToString() != "ok") throw new InvalidDataException("Cannot open staged profile");
                    foreach (string database in new[] { "chat", "agent" })
                    {
                        token.ThrowIfCancellationRequested();
                        string target = FileTransfer.LocalPath(Path.Combine(exported, "chat_" + database + ".db"));
                        var version = await client.Result("/sql " + database + " PRAGMA user_version");
                        if (!int.TryParse(version["rows"]?[1]?.ToString(), out int userVersion)) throw new InvalidDataException("Missing schema version");
                        await client.Result("/sql " + database + " ATTACH DATABASE '" + target.Replace("'", "''") + "' AS pc_key_export KEY '" + key + "'");
                        await client.Result("/sql " + database + " SELECT sqlcipher_export('pc_key_export')");
                        await client.Result("/sql " + database + " PRAGMA pc_key_export.user_version=" + userVersion);
                    }
                    // /sql opens a transaction. DETACH cannot run in that transaction;
                    // closing the stores closes and commits both attached databases.
                    await client.CloseStore();
                }
                finally { client.Dispose(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await ProfileBackup.WaitForProfile(stage, CancellationToken.None); }
            }
            await ProfileBackup.Validate(exported, stage, password, normalize: false, token);
            token.ThrowIfCancellationRequested();
            if (ProfileKeys.Exists(stage)) DeletionFile.DestroyKey(DeletionFile.Inspect(Path.Combine(stage, ProfileKeys.FileName)));
            foreach (string name in ProfileBackup.DatabaseFiles) ProfileBackup.DeleteOwned(stage, Path.Combine(stage, name));
            foreach (string name in ProfileBackup.DatabaseFiles.Append(ProfileKeys.FileName))
            {
                string from = Path.Combine(exported, name);
                if (File.Exists(from)) File.Move(from, Path.Combine(stage, name));
            }
        }
        finally { key = ""; CryptographicOperations.ZeroMemory(random); ProfileBackup.DeleteOwned(stage, exported); }
    }

    internal static async Task Enable(string profile, string password, CancellationToken token)
    {
        profile = FileTransfer.LocalPath(profile);
        RequireMigrationPath(profile);
        using var pins = new DeletionFile.Pins(profile, true);
        if (ProfileKeys.IsStrong(profile) || ProfileDeletion.Pending(profile)) throw new IOException("Profile is already upgraded or deletion is pending");
        await ProfileBackup.WaitForProfile(profile, token);
        string stage = ProfileBackup.NewStage(profile);
        try
        {
            await Task.Run(() => ProfileBackup.CopyProfile(profile, stage, token), token);
            await ProfileBackup.Validate(stage, profile, password, normalize: false, token);
            await RotateStage(stage, password, token);
            token.ThrowIfCancellationRequested();
            await Task.Run(() => ProfileRestore.Install(profile, stage, discardPrevious: true), CancellationToken.None);
        }
        finally { ProfileBackup.DeleteOwned(profile, stage); }
    }
}

internal sealed class MigrationPathTooLongException() : IOException("Profile path exceeds the native migration path budget");
