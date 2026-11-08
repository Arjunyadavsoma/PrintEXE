namespace PrintShopCompanion.Infrastructure.Storage;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Resolves the v0.5 local storage layout under
/// <c>%LocalAppData%\PrintShop\Companion\</c> and migrates from the v0.4.7
/// layout (<c>%AppData%\PrintShop\Companion\</c>) on first run.
///
/// <para>The new layout is:</para>
/// <code>
/// %LocalAppData%\PrintShop\Companion\
///     Security\
///         device.dat       (DPAPI-encrypted device token)
///         queue.key        (DPAPI-encrypted SQLCipher key)
///     Database\
///         queue.db         (SQLCipher-encrypted SQLite)
///     Jobs\
///         incoming\        (transfers in progress, .pdf.tmp)
///         queued\          (downloaded, awaiting print)
///         printing\        (submitted to spooler)
///         completed\       (terminal, awaiting cleanup)
///     Temp\transfers\      (chunked transfer temp files)
///     Logs\
///         companion.log
/// </code>
/// <para>
/// <c>%LocalAppData%</c> is preferred over <c>%AppData%</c> because:
/// <list type="bullet">
///   <item>LocalAppData is for machine-specific data (the SQLite DB and
///   cached PDFs are tied to this PC, not the user's roaming profile).</item>
///   <item>If the user roams (e.g. domain account), AppData roams with them
///   — but the SQLCipher key is DPAPI-encrypted with CurrentUser scope,
///   which does NOT roam. So the DB would be unreadable on the new machine.</item>
///   <item>LocalAppData does NOT roam, so the DB + key stay together.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LocalStorageLayout
{
    public string Root { get; }
    public string SecurityDir { get; }
    public string DatabaseDir { get; }
    public string JobsIncomingDir { get; }
    public string JobsQueuedDir { get; }
    public string JobsPrintingDir { get; }
    public string JobsCompletedDir { get; }
    public string TempTransfersDir { get; }
    public string LogsDir { get; }

    public string DeviceTokenPath { get; }
    public string QueueKeyPath { get; }
    public string QueueDbPath { get; }
    public string LogFilePath { get; }

    /// <summary>
    /// The OLD v0.4.7 layout, used for migration detection. Null if the
    /// old layout doesn't exist on this machine (fresh install).
    /// </summary>
    public string? LegacyRoot { get; }
    public string? LegacyDeviceTokenPath { get; }
    public string? LegacyQueueDbPath { get; }
    public string? LegacyLogDir { get; }

    public LocalStorageLayout(IOptions<StorageOptions> opts)
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrintShop", "Companion");

        SecurityDir = Path.Combine(Root, "Security");
        DatabaseDir = Path.Combine(Root, "Database");
        JobsIncomingDir = Path.Combine(Root, "Jobs", "incoming");
        JobsQueuedDir = Path.Combine(Root, "Jobs", "queued");
        JobsPrintingDir = Path.Combine(Root, "Jobs", "printing");
        JobsCompletedDir = Path.Combine(Root, "Jobs", "completed");
        TempTransfersDir = Path.Combine(Root, "Temp", "transfers");
        LogsDir = Path.Combine(Root, "Logs");

        DeviceTokenPath = Path.Combine(SecurityDir, "device.dat");
        QueueKeyPath = Path.Combine(SecurityDir, "queue.key");
        QueueDbPath = Path.Combine(DatabaseDir, "queue.db");
        LogFilePath = Path.Combine(LogsDir, "companion.log");

        // Detect the legacy v0.4.7 layout (under %AppData%, not %LocalAppData%).
        LegacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion");
        LegacyDeviceTokenPath = Path.Combine(LegacyRoot, "device.dat");
        LegacyQueueDbPath = Path.Combine(LegacyRoot, "queue.db");
        LegacyLogDir = Path.Combine(LegacyRoot, "logs");

        // Override paths if the user has explicitly set them in appsettings.json
        // (advanced users only — most shops use the defaults).
        var o = opts.Value;
        if (!string.IsNullOrWhiteSpace(o.DeviceTokenPath))
            DeviceTokenPath = o.DeviceTokenPath;
        if (!string.IsNullOrWhiteSpace(o.QueueDbPath))
            QueueDbPath = o.QueueDbPath;
        if (!string.IsNullOrWhiteSpace(o.LogDir))
            LogsDir = o.LogDir;
    }

    /// <summary>
    /// Create all directories if they don't exist. Called on app startup.
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SecurityDir);
        Directory.CreateDirectory(DatabaseDir);
        Directory.CreateDirectory(JobsIncomingDir);
        Directory.CreateDirectory(JobsQueuedDir);
        Directory.CreateDirectory(JobsPrintingDir);
        Directory.CreateDirectory(JobsCompletedDir);
        Directory.CreateDirectory(TempTransfersDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>
    /// Returns the path for a job's PDF in the given state directory.
    /// The filename is always <c>&lt;jobId&gt;.pdf</c> — the customer-supplied
    /// filename is NEVER used as the local path (path-traversal protection).
    /// </summary>
    public string GetPdfPath(string jobId, string state) => state switch
    {
        "incoming" => Path.Combine(JobsIncomingDir, jobId + ".pdf"),
        "queued" => Path.Combine(JobsQueuedDir, jobId + ".pdf"),
        "printing" => Path.Combine(JobsPrintingDir, jobId + ".pdf"),
        "completed" => Path.Combine(JobsCompletedDir, jobId + ".pdf"),
        _ => Path.Combine(JobsQueuedDir, jobId + ".pdf"),
    };

    /// <summary>
    /// Returns the temp path for a job's PDF being downloaded (incoming).
    /// The file is written here with a <c>.tmp</c> extension and atomically
    /// renamed to the final path after SHA-256 verification.
    /// </summary>
    public string GetIncomingTempPath(string jobId) =>
        Path.Combine(JobsIncomingDir, jobId + ".pdf.tmp");

    /// <summary>
    /// Atomically move a file from <paramref name="source"/> to
    /// <paramref name="destination"/>. If the destination already exists
    /// (e.g. from a previous attempt), it's overwritten. The move is
    /// atomic on Windows when both paths are on the same volume (which
    /// they are here — both under <c>%LocalAppData%</c>).
    /// </summary>
    public void AtomicMove(string source, string destination)
    {
        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(source, destination);
    }

    /// <summary>
    /// Check if the legacy v0.4.7 layout exists (for migration).
    /// </summary>
    public bool HasLegacyLayout() =>
        !string.IsNullOrEmpty(LegacyRoot) && Directory.Exists(LegacyRoot);

    /// <summary>
    /// Move the legacy device.dat to the new Security/ location if it
    /// exists and the new location doesn't.
    /// </summary>
    public bool MigrateDeviceToken()
    {
        if (string.IsNullOrEmpty(LegacyDeviceTokenPath) || !File.Exists(LegacyDeviceTokenPath))
            return false;
        if (File.Exists(DeviceTokenPath))
            return false;  // already migrated
        EnsureDirectories();
        File.Move(LegacyDeviceTokenPath, DeviceTokenPath);
        return true;
    }

    /// <summary>
    /// Move the legacy logs directory if it exists. Old logs are NOT
    /// migrated into the new Logs/ directory — they stay in the old
    /// location for reference. Only new logs go to the new location.
    /// </summary>
    public void MigrateLogs()
    {
        // No-op: old logs stay in %AppData%\PrintShop\Companion\logs\
        // for reference; new logs go to %LocalAppData%\PrintShop\Companion\Logs\
    }

    /// <summary>
    /// Check if the legacy v0.4.7 queue.db exists at the old %AppData%
    /// location and the new encrypted queue.db does NOT exist at the
    /// new %LocalAppData% location. This means a migration is needed.
    /// </summary>
    public bool NeedsQueueMigration()
    {
        if (string.IsNullOrEmpty(LegacyQueueDbPath) || !File.Exists(LegacyQueueDbPath))
            return false;
        if (File.Exists(QueueDbPath))
            return false;  // already migrated (new DB exists)
        return true;
    }

    /// <summary>
    /// Migrate the legacy v0.4.7 queue.db (plain SQLite at %AppData%) to
    /// the new location at %LocalAppData%\...\Database\queue.db. The
    /// actual data migration (reading rows from the old DB and inserting
    /// into the new encrypted DB) is handled by SqliteJobQueue because it
    /// has the SQLCipher key. This method just:
    ///   1. Renames the old queue.db to queue.db.v047.bak (backup)
    ///   2. Returns the backup path so SqliteJobQueue can read it
    /// </summary>
    /// <returns>The path to the backup file, or null if no migration needed.</returns>
    public string? PrepareQueueMigration()
    {
        if (!NeedsQueueMigration()) return null;

        EnsureDirectories();
        var backupPath = LegacyQueueDbPath + ".v047.bak";

        // If a backup already exists (interrupted migration), don't overwrite it.
        if (!File.Exists(backupPath))
        {
            File.Move(LegacyQueueDbPath, backupPath);
        }

        return backupPath;
    }

    /// <summary>
    /// Mark the migration as complete by creating a sentinel file. This
    /// prevents re-migration on subsequent startups.
    /// </summary>
    public void MarkMigrationComplete()
    {
        EnsureDirectories();
        var markerPath = Path.Combine(DatabaseDir, ".migrated");
        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>
    /// Check if migration has been completed (sentinel file exists).
    /// </summary>
    public bool IsMigrationComplete()
    {
        var markerPath = Path.Combine(DatabaseDir, ".migrated");
        return File.Exists(markerPath);
    }
}
