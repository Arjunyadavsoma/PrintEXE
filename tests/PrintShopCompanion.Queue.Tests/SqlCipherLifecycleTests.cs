namespace PrintShopCompanion.Queue.Tests;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// Phase 10.3 (Stage 2): the twelve REQUIRED SQLCipher lifecycle tests,
/// executed against the application's REAL encrypted stack — the production
/// <see cref="QueueKeyStore"/> (DPAPI CurrentUser), the production
/// <see cref="SqliteJobQueue"/> (SQLCipher via bundle_e_sqlcipher), the
/// production schema/migration path and the production failure classifier.
///
/// <para>Honesty rules honoured here:</para>
/// <list type="bullet">
/// <item>every database is created by a GENUINE queue operation (never
/// hand-fabricated bytes or headers);</item>
/// <item>key material never appears in assertions, output or exceptions —
/// only SHA-256 fingerprints (hash of a random 256-bit key reveals
/// nothing usable);</item>
/// <item>every test runs in an isolated temp root (rootOverride) and uses
/// bounded operations;</item>
/// <item><see cref="SqliteJobQueue"/> is not IDisposable — teardown is
/// <see cref="SqliteConnection.ClearAllPools"/> (which SQL-11 also tests
/// explicitly);</item>
/// <item>on non-Windows (no DPAPI) tests SKIP with a reason — a skipped
/// test is never reported as passed.</item>
/// </list>
/// </summary>
public class SqlCipherLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PrintExeSqlCipherTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "Database", "queue.db");
    private string KeyPath => Path.Combine(_root, "Security", "queue.key");

    public SqlCipherLifecycleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LocalStorageLayout NewLayout() =>
        new(Options.Create(new StorageOptions()), rootOverride: _root);

    private QueueKeyStore NewKeyStore() =>
        new(NewLayout(), NullLogger<QueueKeyStore>.Instance);

    private SqliteJobQueue NewEncryptedQueue(DatabaseRecoveryState? state = null)
    {
        var opts = Options.Create(new StorageOptions { QueueDbPath = DbPath });
        return new SqliteJobQueue(
            opts, NewLayout(), NewKeyStore(), NullLogger<SqliteJobQueue>.Instance, state);
    }

    private static PrintJobRecord MakeJob(string id, string title, JobStatus status = JobStatus.Pending) => new()
    {
        JobId = id,
        ShopId = "shop-phase103-test",
        DeviceId = "device-phase103-test",
        Title = title,
        Copies = 1,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>SHA-256 of the raw key bytes — a NON-SECRET fingerprint.</summary>
    private static string Fingerprint(QueueKeyStore ks)
    {
        var key = ks.LoadOrGenerateKey();
        return Convert.ToHexString(SHA256.HashData(key));
    }

    private static byte[] ReadHeader16(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[16];
        var n = fs.Read(buf, 0, 16);
        Assert.Equal(16, n);
        return buf;
    }

    private static byte[] PlainSqliteMagic => "SQLite format 3\0"u8.ToArray();

    private static async Task<string> IntegrityCheckAsync(string dbPath, string password)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Password={password};Pooling=false");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return (string?)(await cmd.ExecuteScalarAsync()) ?? string.Empty;
    }

    // ── SQL-1: first-run key creation ───────────────────────────────────────
    [SkippableFact]
    public void SQL1_FirstRun_CreatesDpapiProtectedKeyFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI (ProtectedData/CurrentUser) requires Windows");
        var ks = NewKeyStore();
        var fp1 = Fingerprint(ks);

        Assert.True(File.Exists(KeyPath), "queue.key must exist after first run");
        var blob = File.ReadAllBytes(KeyPath);
        Assert.True(blob.Length > 40, $"DPAPI blob should exceed raw key size, was {blob.Length}");
        Assert.NotEqual(32, blob.Length);
        Assert.Equal(64, fp1.Length);
    }

    // ── SQL-2/SQL-3: key persistence across restarts + same-user retrieval ──
    [SkippableFact]
    public void SQL2_KeyPersistsAcrossRestarts_SameFingerprint_NoRewrite()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var fp1 = Fingerprint(NewKeyStore());
        var mtime1 = File.GetLastWriteTimeUtc(KeyPath);
        var bytes1 = File.ReadAllBytes(KeyPath);

        var fp2 = Fingerprint(NewKeyStore());

        Assert.Equal(fp1, fp2);
        Assert.Equal(bytes1, File.ReadAllBytes(KeyPath));
        Assert.Equal(mtime1, File.GetLastWriteTimeUtc(KeyPath));
    }

    [SkippableFact]
    public void SQL3_SameUser_RetrievesSameKey_FromCopiedKeyFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var fp1 = Fingerprint(NewKeyStore());
        var copyPath = Path.Combine(_root, "Security", "queue.key.copy-for-retrieval-test");
        File.Copy(KeyPath, copyPath);

        var encrypted = File.ReadAllBytes(copyPath);
        var unprotected = System.Security.Cryptography.ProtectedData.Unprotect(
            encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var fpCopy = Convert.ToHexString(SHA256.HashData(unprotected));

        Assert.Equal(fp1, fpCopy);
        Assert.Equal(32, unprotected.Length);
    }

    // ── SQL-4: database creation through a GENUINE queue operation ──────────
    [SkippableFact]
    public async Task SQL4_GenuineQueueOperation_CreatesEncryptedDb_NotPlainSqlite()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-enc-001", "Real encrypted queue write"));
        SqliteConnection.ClearAllPools();

        Assert.True(File.Exists(DbPath), "queue.db must exist after a genuine EnqueueAsync");
        Assert.NotEqual(PlainSqliteMagic, ReadHeader16(DbPath));
    }

    // ── SQL-5/SQL-9: correct-key access + restart/reopen state survival ─────
    [SkippableFact]
    public async Task SQL5_SQL9_RestartReopen_WithCorrectKey_PreservesStateThroughLifecycle()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var fp = Fingerprint(NewKeyStore());

        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-enc-002", "Pending across restart"));
        await queue.EnqueueAsync(MakeJob("job-enc-003", "Terminal job", JobStatus.Printed));
        SqliteConnection.ClearAllPools();

        // Restart: brand-new queue + brand-new key store over the SAME files.
        var queue2 = NewEncryptedQueue();
        var jobs = await queue2.ListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, j => j.JobId == "job-enc-002" && j.Status == JobStatus.Pending);
        Assert.Contains(jobs, j => j.JobId == "job-enc-003" && j.Status == JobStatus.Printed);
        SqliteConnection.ClearAllPools();

        // The key never changed across "restarts".
        Assert.Equal(fp, Fingerprint(NewKeyStore()));
    }

    // ── SQL-6: rejection of an incorrect key, no plaintext fallback ─────────
    [SkippableFact]
    public async Task SQL6_WrongKey_Rejected_Classified_NoPlaintextFallback_OriginalUntouched()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var seedQueue = NewEncryptedQueue();
        await seedQueue.EnqueueAsync(MakeJob("job-enc-004", "Must survive a wrong-key attempt"));
        SqliteConnection.ClearAllPools();
        var originalBytes = await File.ReadAllBytesAsync(DbPath);

        // A DIFFERENT installation's key: valid 32 random bytes, DPAPI-wrapped
        // by the same user (so the key store loads it cleanly) but NOT the key
        // that encrypted queue.db — the cross-user / swapped-keyfile scenario.
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath,
            System.Security.Cryptography.ProtectedData.Protect(
                RandomNumberGenerator.GetBytes(32), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

        Exception? caught = null;
        var wrongKeyQueue = NewEncryptedQueue();
        try
        {
            _ = await wrongKeyQueue.ListAsync();
        }
        catch (Exception ex) { caught = ex; }
        SqliteConnection.ClearAllPools();

        Assert.NotNull(caught);
        Assert.Equal(DbFailureClass.Corruption, DbErrorClassifier.Classify(caught));

        // NO plaintext fallback: the file is byte-identical to before.
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(DbPath));
        Assert.NotEqual(PlainSqliteMagic, ReadHeader16(DbPath));
    }

    // ── SQL-7: rejection of a plaintext DB where encrypted storage is required
    [SkippableFact]
    public async Task SQL7_PlainSqliteDb_RejectedWhenEncryptedRequired_BytesUntouched()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var plainOpts = Options.Create(new StorageOptions { QueueDbPath = DbPath });
        var plainQueue = new SqliteJobQueue(
            plainOpts, NewLayout(), keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await plainQueue.EnqueueAsync(MakeJob("job-plain-001", "Written as plaintext"));
        SqliteConnection.ClearAllPools();
        Assert.Equal(PlainSqliteMagic, ReadHeader16(DbPath));
        var plainBytes = await File.ReadAllBytesAsync(DbPath);

        // Now demand the production encrypted path over the SAME file.
        Exception? caught = null;
        var encryptedQueue = NewEncryptedQueue();
        try
        {
            await encryptedQueue.EnqueueAsync(MakeJob("job-plain-002", "Must be refused"));
        }
        catch (Exception ex) { caught = ex; }
        SqliteConnection.ClearAllPools();

        Assert.NotNull(caught);
        Assert.Equal(DbFailureClass.Corruption, DbErrorClassifier.Classify(caught));
        Assert.Equal(plainBytes, await File.ReadAllBytesAsync(DbPath));
    }

    // ── SQL-8: SQLCipher integrity verification ─────────────────────────────
    [SkippableFact]
    public async Task SQL8_IntegrityCheck_ReturnsOk_OnEncryptedDb()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-enc-005", "Integrity target"));
        SqliteConnection.ClearAllPools();

        // The CORRECT key exactly as the app produces it (hex raw-key form).
        // The password lives only in the connection object — never logged.
        var ks = NewKeyStore();
        var password = ks.GetKeyForConnectionString();
        var result = await IntegrityCheckAsync(DbPath, password);
        Assert.Equal("ok", result);
    }

    // ── SQL-10: WAL/SHM lifecycle ───────────────────────────────────────────
    [SkippableFact]
    public async Task SQL10_WalShm_ExistWhileOpen_VanishAfterCleanClose()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-enc-006", "WAL lifecycle"));

        var ks = NewKeyStore();
        var password = ks.GetKeyForConnectionString();
        await using (var conn = new SqliteConnection($"Data Source={DbPath};Password={password};Pooling=false"))
        {
            await conn.OpenAsync();
            await using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                Assert.Equal("wal", (string?)(await wal.ExecuteScalarAsync()));
            }
            Assert.True(File.Exists(DbPath + "-wal"), "WAL sidecar must exist while a WAL connection is open");
            await queue.EnqueueAsync(MakeJob("job-enc-007", "Under WAL"));
        }
        SqliteConnection.ClearAllPools();

        Assert.False(File.Exists(DbPath + "-wal"), "WAL sidecar must be checkpointed away after clean close");
        Assert.False(File.Exists(DbPath + "-shm"), "SHM sidecar must disappear after the last connection closes");
        Assert.True(File.Exists(DbPath));
        Assert.NotEqual(PlainSqliteMagic, ReadHeader16(DbPath));
    }

    // ── SQL-11: connection-pool cleanup ─────────────────────────────────────
    [SkippableFact]
    public async Task SQL11_ConnectionPool_Cleanup_ReleasesFileLocks()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-enc-008", "Pool test"));
        var jobs = await queue.ListAsync();
        Assert.NotEmpty(jobs);

        // Pooled connections keep the file locked until pools are cleared.
        Assert.ThrowsAny<IOException>(() => Directory.Delete(_root, recursive: true));

        SqliteConnection.ClearAllPools();
        var moved = Path.Combine(_root, "Database", "queue.db.moved-by-test");
        File.Move(DbPath, moved);
        Assert.True(File.Exists(moved));
        File.Move(moved, DbPath);
    }

    // ── SQL-12: key-file permission checks ──────────────────────────────────
    [SkippableFact]
    public void SQL12_KeyFile_Permissions_SameUserAccess_NoWorldWritable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI + Windows ACLs require Windows");
        _ = Fingerprint(NewKeyStore());
        Assert.True(File.Exists(KeyPath));

        var security = new System.Security.AccessControl.FileSecurity(KeyPath,
            System.Security.AccessControl.AccessControlSections.Access |
            System.Security.AccessControl.AccessControlSections.Owner);
        var rules = security.GetAccessRules(true, true,
            typeof(System.Security.Principal.SecurityIdentifier));

        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var hasRead = false;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference.Value == currentUser.Value &&
                (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadData) != 0)
            {
                hasRead = true;
            }
            if (rule.IdentityReference.Value == "S-1-1-0" /* Everyone */ &&
                (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.WriteData) != 0)
            {
                Assert.Fail("queue.key grants Everyone write access — key-file permission defect");
            }
        }
        Assert.True(hasRead, "the owning (current) user must be able to read queue.key");
        // DPAPI blob on disk, not the raw key (also asserted in SQL-1).
        Assert.NotEqual(32, new FileInfo(KeyPath).Length);
    }
}
