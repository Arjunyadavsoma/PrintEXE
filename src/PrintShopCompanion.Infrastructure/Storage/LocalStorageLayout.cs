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

    public LocalStorageLayout(
        IOptions<StorageOptions> opts,
        string? legacyRootOverride = null,
        string? rootOverride = null)
    {
        // rootOverride relocates the ENTIRE v0.5 layout (Security/, Database/,
        // Jobs/, Temp/, Logs/) — used by tests and portable installs. NOTE:
        // the SECOND parameter is legacyRootOverride (v0.4.7 migration
        // detection only). Passing a temp dir there — as early tests did —
        // left Root at the real %LocalAppData% path; the Windows CI failures
        // fixed in v0.7.1 came from exactly that mistake.
        Root = rootOverride ?? Path.Combine(
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

        // v1 contract Appendix C: the device token lives at
        // %AppData%\PrintEXE\device-token.dat. This is the new default for the
        // v1 companion API contract. The historical path
        // (%LocalAppData%\PrintShop\Companion\Security\device.dat) and the
        // v0.4.7 legacy path (%AppData%\PrintShop\Companion\device.dat) are
        // both migrated to the contract path on first run — see MigrateDeviceToken().
        ContractDeviceTokenDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintEXE");
        ContractDeviceTokenPath = Path.Combine(ContractDeviceTokenDir, "device-token.dat");

        // Historical v0.5 path — kept as the first migration source.
        DeviceTokenPath = Path.Combine(SecurityDir, "device.dat");
        QueueKeyPath = Path.Combine(SecurityDir, "queue.key");
        QueueDbPath = Path.Combine(DatabaseDir, "queue.db");
        LogFilePath = Path.Combine(LogsDir, "companion.log");

        // Detect the legacy v0.4.7 layout (under %AppData%, not %LocalAppData%).
        // P0 FIX: allow the legacy root to be overridden for test isolation.
        // Without this override, migration tests would create/delete files at
        // the REAL %AppData%\PrintShop\Companion\queue.db path, potentially
        // destroying a real user's data if tests run on a machine with an
        // actual PrintShop install.
        LegacyRoot = legacyRootOverride ?? Path.Combine(
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
        else
            // No explicit override → use the v1 contract default path.
            DeviceTokenPath = ContractDeviceTokenPath;
        if (!string.IsNullOrWhiteSpace(o.QueueDbPath))
            QueueDbPath = o.QueueDbPath;
        if (!string.IsNullOrWhiteSpace(o.LogDir))
            LogsDir = o.LogDir;
    }

    /// <summary>The v1 contract device-token directory: <c>%AppData%\PrintEXE\</c>.</summary>
    public string ContractDeviceTokenDir { get; }

    /// <summary>The v1 contract device-token path: <c>%AppData%\PrintEXE\device-token.dat</c>.</summary>
    public string ContractDeviceTokenPath { get; }

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
    /// Move a legacy device-token file to the v1 contract path
    /// (<c>%AppData%\PrintEXE\device-token.dat</c>) if it exists and the
    /// contract path doesn't. Three migration sources, checked in order:
    /// <list type="number">
    ///   <item>v0.5 historical: <c>%LocalAppData%\PrintShop\Companion\Security\device.dat</c></item>
    ///   <item>v0.4.7 legacy: <c>%AppData%\PrintShop\Companion\device.dat</c></item>
    /// </list>
    /// The FIRST existing source is moved to the contract path. If the contract
    /// path already has a token, nothing is moved (no overwrite). Returns true
    /// if a migration happened. The queue DB + queue key are NOT moved — they
    /// stay at the historical location (the contract doesn't specify those
    /// paths, and moving the encrypted DB is risky).
    /// </summary>
    public bool MigrateDeviceToken()
    {
        // Contract path already has a token — nothing to do.
        if (File.Exists(DeviceTokenPath))
            return false;

        // Try the v0.5 historical Security/device.dat first (most recent).
        var historicalV05 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrintShop", "Companion", "Security", "device.dat");
        if (File.Exists(historicalV05))
        {
            EnsureDirectoriesForToken();
            MoveWithRetry(historicalV05, DeviceTokenPath);
            return true;
        }

        // Then the v0.4.7 legacy %AppData%\PrintShop\Companion\device.dat.
        if (!string.IsNullOrEmpty(LegacyDeviceTokenPath) && File.Exists(LegacyDeviceTokenPath))
        {
            EnsureDirectoriesForToken();
            MoveWithRetry(LegacyDeviceTokenPath, DeviceTokenPath);
            return true;
        }
        return false;
    }

    /// <summary>Create the directory containing <see cref="DeviceTokenPath"/> (the contract path may be under a directory that EnsureDirectories() doesn't create).</summary>
    private void EnsureDirectoriesForToken()
    {
        var dir = Path.GetDirectoryName(DeviceTokenPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
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
            // Microsoft.Data.Sqlite parks closed connections in a process-wide
            // pool that KEEPS the DB file handle open. On Windows, File.Move
            // fails with "in use" for as long as the pool holds the handle —
            // the retry loop below cannot fix that, so clear the pool first
            // (no-op on Linux, where the move succeeded regardless). This is
            // the Windows CI failure fixed in v0.7.1: a reopen while ANY prior
            // queue instance in this process had touched the legacy file.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Windows can briefly hold a file handle on the legacy DB
            // after the SQLite connection pool is cleared (the OS takes
            // a few ms to release the handle). Retry the move a handful
            // of times with a short backoff so a transient lock does not
            // fail the migration. This is a robustness improvement, not
            // a behavior change — the move still happens exactly once.
            MoveWithRetry(LegacyQueueDbPath, backupPath);
        }

        return backupPath;
    }

    /// <summary>
    /// Move a file, retrying for up to ~2 seconds on
    /// <c>IOException</c>/<c>UnauthorizedAccessException</c> (Windows
    /// transient file-lock). Throws the last exception if all retries
    /// fail. Used by <see cref="PrepareQueueMigration"/> so a briefly-
    /// held handle on the legacy queue.db does not abort the migration.
    /// </summary>
    private static void MoveWithRetry(string source, string dest)
    {
        const int maxAttempts = 10;
        const int delayMs = 200;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, dest);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>
    /// Mark the migration as complete by creating a sentinel file. This
    /// prevents re-migration on subsequent startups. The sentinel is placed
    /// in the same directory as QueueDbPath (which may be overridden by
    /// appsettings.json), NOT the layout's computed DatabaseDir.
    /// </summary>
    public void MarkMigrationComplete()
    {
        var markerDir = Path.GetDirectoryName(QueueDbPath) ?? DatabaseDir;
        Directory.CreateDirectory(markerDir);
        var markerPath = Path.Combine(markerDir, ".migrated");
        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>
    /// Check if migration has been completed (sentinel file exists next to
    /// the actual queue.db, not the layout's computed DatabaseDir).
    /// </summary>
    public bool IsMigrationComplete()
    {
        var markerDir = Path.GetDirectoryName(QueueDbPath) ?? DatabaseDir;
        var markerPath = Path.Combine(markerDir, ".migrated");
        return File.Exists(markerPath);
    }
}
