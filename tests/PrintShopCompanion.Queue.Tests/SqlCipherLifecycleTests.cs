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
/// <para>Documented Phase 9/10 contract honoured by these tests: an
/// undecryptable database (wrong key, foreign plaintext file) is
/// QUARANTINED as <c>queue.db.corrupt-&lt;stamp&gt;</c> — preserved byte-identical,
/// never deleted, never decrypted — a fresh encrypted database is created,
/// and the failure is journaled to <c>Database/recovery-log.jsonl</c>
/// (SqliteJobQueue.EnsureSchemaOrRecoverAsync, runs 46157255797 evidence).</para>
///
/// <para>Honesty rules: every database is created by a GENUINE queue
/// operation (never fabricated bytes); key material never appears in
/// assertions or output — only SHA-256 fingerprints; isolated temp roots;
/// bounded operations; on a test host where the e_sqlcipher provider lost
/// its bind race (multi-TFM xUnit host, run 46157255797) the encrypted-path
/// tests SKIP with an explicit reason — the real encrypted verification
/// then runs via tools/PrintEXE.QueueHarness on the MSI matrix runner
/// (deterministic apphost load context). A skip is never reported as a
/// pass.</para>
/// </summary>
public class SqlCipherLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PrintExeSqlCipherTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "Database", "queue.db");
    private string KeyPath => Path.Combine(_root, "Security", "queue.key");

    // Deterministic attempt to bring up the e_sqlcipher provider. If the host
    // already registered a plain provider, this returns false and the tests
    // below SKIP honestly instead of failing on a bind race they cannot win.
    private static readonly bool SqlCipherReady =
        SqlCipherInitializer.Initialize(NullLogger.Instance);

    private const string ProviderSkipReason =
        "e_sqlcipher provider not available in THIS test host (multi-TFM bind race, run 46157255797 evidence); " +
        "encrypted-path verification runs deterministically via tools/PrintEXE.QueueHarness on the MSI matrix runner";

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
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
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
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
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

        Assert.Equal(fp, Fingerprint(NewKeyStore()));
    }

    // ── SQL-6: incorrect key → QUARANTINE contract (no plaintext fallback) ──
    [SkippableFact]
    public async Task SQL6_WrongKey_Quarantined_Preserved_FreshEncryptedDbCreated()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var seedQueue = NewEncryptedQueue();
        await seedQueue.EnqueueAsync(MakeJob("job-enc-004", "Must be preserved via quarantine"));
        SqliteConnection.ClearAllPools();
        var originalBytes = await File.ReadAllBytesAsync(DbPath);
        var originalSha = Convert.ToHexString(SHA256.HashData(originalBytes));

        // A DIFFERENT installation's key: valid 32 random bytes, DPAPI-wrapped
        // by the same user (the key store loads it cleanly) but NOT the key
        // that encrypted queue.db.
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath,
            System.Security.Cryptography.ProtectedData.Protect(
                RandomNumberGenerator.GetBytes(32), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var state = new DatabaseRecoveryState();
        var wrongKeyQueue = NewEncryptedQueue(state);
        var jobsAfter = await wrongKeyQueue.ListAsync();   // per Phase 9 contract: quarantine + fresh DB, NO throw
        SqliteConnection.ClearAllPools();

        // Quarantine evidence: the undecryptable file was MOVED (not deleted,
        // not decrypted) and is byte-identical to what was on disk.
        var quarantined = Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.corrupt-*");
        Assert.NotEmpty(quarantined);
        Assert.Equal(originalSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(quarantined[0]))));

        // A fresh ENCRYPTED database replaced it (never plaintext).
        Assert.True(File.Exists(DbPath));
        Assert.NotEqual(PlainSqliteMagic, ReadHeader16(DbPath));
        Assert.Empty(jobsAfter);   // fresh DB — no jobs; originals live in quarantine

        // The failure is journaled (evidence retained), no destructive cleanup.
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(DbPath)!, "recovery-log.jsonl")),
            "recovery journal must record the classified failure");
    }

    // ── SQL-7: plaintext DB where encrypted storage is required → quarantine ─
    [SkippableFact]
    public async Task SQL7_PlainSqliteDb_Quarantined_FreshEncryptedDbCreated()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var plainOpts = Options.Create(new StorageOptions { QueueDbPath = DbPath });
        var plainQueue = new SqliteJobQueue(
            plainOpts, NewLayout(), keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await plainQueue.EnqueueAsync(MakeJob("job-plain-001", "Written as plaintext"));
        SqliteConnection.ClearAllPools();
        Assert.Equal(PlainSqliteMagic, ReadHeader16(DbPath));
        var plainBytes = await File.ReadAllBytesAsync(DbPath);
        var plainSha = Convert.ToHexString(SHA256.HashData(plainBytes));

        // Demand the production encrypted path over the SAME file.
        var encryptedQueue = NewEncryptedQueue();
        var jobsAfter = await encryptedQueue.ListAsync();   // quarantine + fresh encrypted DB, NO throw
        await encryptedQueue.EnqueueAsync(MakeJob("job-plain-002", "Fresh encrypted DB works"));
        SqliteConnection.ClearAllPools();

        // The plaintext file was QUARANTINED byte-identical (preserved as
        // evidence) — not silently adopted, not deleted.
        var quarantined = Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.corrupt-*");
        Assert.NotEmpty(quarantined);
        Assert.Equal(plainSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(quarantined[0]))));

        // The active database is now ENCRYPTED and operational.
        Assert.True(File.Exists(DbPath));
        Assert.NotEqual(PlainSqliteMagic, ReadHeader16(DbPath));
        Assert.Contains(jobsAfter, j => j.JobId == "job-plain-002");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(DbPath)!, "recovery-log.jsonl")));
    }

    // ── SQL-8: SQLCipher integrity verification ─────────────────────────────
    [SkippableFact]
    public async Task SQL8_IntegrityCheck_ReturnsOk_OnEncryptedDb()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-enc-005", "Integrity target"));
        SqliteConnection.ClearAllPools();

        var ks = NewKeyStore();
        var password = ks.GetKeyForConnectionString();   // in-memory only, never logged
        var result = await IntegrityCheckAsync(DbPath, password);
        Assert.Equal("ok", result);
    }

    // ── SQL-10: WAL/SHM lifecycle ───────────────────────────────────────────
    [SkippableFact]
    public async Task SQL10_WalShm_ExistWhileOpen_VanishAfterCleanClose()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
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
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
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
        Assert.NotEqual(32, new FileInfo(KeyPath).Length);
    }
}
