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
/// Phase 10.3 (Stage 6): negative and recovery boundaries for the ENCRYPTED
/// queue, all on isolated temporary storage through the application's real
/// classes. The DOCUMENTED Phase 9 contract (verified against the real
/// implementation, run 46157255797) is: an undecryptable/foreign database is
/// QUARANTINED as <c>queue.db.corrupt-&lt;stamp&gt;</c> (preserved byte-identical,
/// never deleted, never decrypted), a fresh ENCRYPTED database is created,
/// and the classified failure is journaled to <c>Database/recovery-log.jsonl</c>.
/// Repeat guards bound recovery attempts — no infinite loop.
///
/// <para>Scenario map: A wrong key · B corrupt database (encrypted) ·
/// C missing DPAPI key · D permission denial · E disk full (deterministic
/// SQLite <c>max_page_count</c> simulation — no real volume filled, no
/// fabricated exception).</para>
/// </summary>
public class SqlCipherNegativeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PrintExeSqlCipherNegative", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "Database", "queue.db");
    private string KeyPath => Path.Combine(_root, "Security", "queue.key");

    private static readonly bool SqlCipherReady =
        SqlCipherInitializer.Initialize(NullLogger.Instance);

    private const string ProviderSkipReason =
        "e_sqlcipher provider not available in THIS test host (multi-TFM bind race, run 46157255797 evidence); " +
        "encrypted-path verification runs deterministically via tools/PrintEXE.QueueHarness on the MSI matrix runner";

    public SqlCipherNegativeTests() => Directory.CreateDirectory(_root);

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
        ShopId = "shop-phase103-neg",
        DeviceId = "device-phase103-neg",
        Title = title,
        Copies = 1,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<byte[]> SeedOneJobAsync()
    {
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-neg-001", "Negative-test payload"));
        SqliteConnection.ClearAllPools();
        return await File.ReadAllBytesAsync(DbPath);
    }

    private static byte[] PlainSqliteMagic => "SQLite format 3\0"u8.ToArray();

    private static string[] QuarantineFiles(string root) =>
        Directory.GetFiles(Path.Combine(root, "Database"), "queue.db.corrupt-*");

    // ── Scenario A: incorrect key → quarantine contract ─────────────────────
    [SkippableFact]
    public async Task A_WrongKey_Quarantined_Preserved_FreshEncryptedDb_Journaled()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var original = await SeedOneJobAsync();
        var originalSha = Convert.ToHexString(SHA256.HashData(original));

        // Swap in a valid-but-different DPAPI key (the cross-user scenario).
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, System.Security.Cryptography.ProtectedData.Protect(
            RandomNumberGenerator.GetBytes(32), null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var state = new DatabaseRecoveryState();
        var queue = NewEncryptedQueue(state);
        var jobsAfter = await queue.ListAsync();   // quarantine + fresh DB per Phase 9 contract
        SqliteConnection.ClearAllPools();

        var quarantined = QuarantineFiles(_root);
        Assert.NotEmpty(quarantined);
        Assert.Equal(originalSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(quarantined[0]))));
        Assert.True(File.Exists(DbPath));
        Assert.NotEqual(PlainSqliteMagic, (await File.ReadAllBytesAsync(DbPath))[..16].ToArray());
        Assert.Empty(jobsAfter);
        Assert.True(File.Exists(Path.Combine(_root, "Database", "recovery-log.jsonl")),
            "the classified failure must be journaled");
        Assert.True(state.ConsecutiveFailures >= 0 && state.ConsecutiveFailures < 100,
            "repeat guard bounds recovery attempts — no infinite loop");
    }

    // ── Scenario B: corrupt database (encrypted) → quarantine contract ──────
    [SkippableFact]
    public async Task B_CorruptEncryptedDb_Quarantined_Preserved_EvidenceRetained()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        _ = await SeedOneJobAsync();

        // Genuine corruption: flip bytes in the middle of the real encrypted
        // file (isolated test copy — never a user database).
        var bytes = await File.ReadAllBytesAsync(DbPath);
        for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0x5A;
        await File.WriteAllBytesAsync(DbPath, bytes);
        var corruptSha = Convert.ToHexString(SHA256.HashData(bytes));

        var state = new DatabaseRecoveryState();
        var queue = NewEncryptedQueue(state);
        await queue.ListAsync();   // quarantine + fresh DB per Phase 9 contract
        SqliteConnection.ClearAllPools();

        var quarantined = QuarantineFiles(_root);
        Assert.NotEmpty(quarantined);
        Assert.Equal(corruptSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(quarantined[0]))));
        Assert.True(File.Exists(Path.Combine(_root, "Database", "recovery-log.jsonl")));
        Assert.True(state.ConsecutiveFailures < 100, "repeat guard must bound recovery attempts");
    }

    // ── Scenario C: missing DPAPI key → quarantine; restore → full recovery ─
    [SkippableFact]
    public async Task C_MissingKey_QuarantinedNotOverwritten_RestoreRecoversAllJobs()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var originalEncrypted = await SeedOneJobAsync();
        var originalSha = Convert.ToHexString(SHA256.HashData(originalEncrypted));
        var originalKeyBlob = await File.ReadAllBytesAsync(KeyPath);

        // Delete the key (the documented "key lost" disaster).
        File.Delete(KeyPath);

        // A replacement key is generated for FUTURE databases; the existing
        // encrypted database is NOT silently re-encrypted or destroyed — it is
        // quarantined intact and the queue starts over encrypted.
        var state = new DatabaseRecoveryState();
        var queue = NewEncryptedQueue(state);
        var jobsAfter = await queue.ListAsync();
        SqliteConnection.ClearAllPools();

        Assert.True(File.Exists(KeyPath), "a replacement key file is generated for future databases");
        Assert.NotEqual(originalKeyBlob, await File.ReadAllBytesAsync(KeyPath));
        Assert.Empty(jobsAfter);

        // The ORIGINAL encrypted image is preserved byte-identical in quarantine.
        var quarantined = QuarantineFiles(_root);
        Assert.NotEmpty(quarantined);
        Assert.Equal(originalSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(quarantined[0]))));
        Assert.True(File.Exists(Path.Combine(_root, "Database", "recovery-log.jsonl")));

        // Documented operator recovery: restore the original key AND the
        // quarantined image → every job is readable again, no data lost.
        File.WriteAllBytes(KeyPath, originalKeyBlob);
        File.Move(quarantined[0], DbPath, overwrite: true);
        var restored = NewEncryptedQueue();
        var jobs = await restored.ListAsync();
        var job = Assert.Single(jobs);
        Assert.Equal("job-neg-001", job.JobId);
        Assert.Equal(JobStatus.Pending, job.Status);
        SqliteConnection.ClearAllPools();
    }

    // ── Scenario D: permission denial → classified, no destructive cleanup ──
    [SkippableFact]
    public async Task D_PermissionDenial_Classified_NoDestructiveCleanup()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows ACLs required");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var original = await SeedOneJobAsync();
        var dbDir = Path.GetDirectoryName(DbPath)!;

        var dirInfo = new DirectoryInfo(dbDir);
        var security = dirInfo.GetAccessControl();
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            currentUser,
            System.Security.AccessControl.FileSystemRights.Read |
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Deny);
        Exception? caught = null;
        try
        {
            security.AddAccessRule(deny);
            dirInfo.SetAccessControl(security);

            var queue = NewEncryptedQueue();
            try { _ = await queue.ListAsync(); }
            catch (Exception ex) { caught = ex; }
            SqliteConnection.ClearAllPools();

            Assert.NotNull(caught);
            Assert.Equal(DbFailureClass.PermissionDenied, DbErrorClassifier.Classify(caught));
        }
        finally
        {
            var s2 = dirInfo.GetAccessControl();
            s2.RemoveAccessRuleSpecific(deny);
            dirInfo.SetAccessControl(s2);
        }

        // No destructive cleanup while access was denied.
        Assert.Equal(original.Length, new FileInfo(DbPath).Length);
    }

    // ── Scenario E: disk full (bounded, deterministic simulation) ───────────
    [SkippableFact]
    public async Task E_DiskFull_Classified_QueueRemainsRecoverable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        Skip.IfNot(SqlCipherReady, ProviderSkipReason);
        var queue = NewEncryptedQueue();
        await queue.EnqueueAsync(MakeJob("job-neg-full-1", "Before simulated disk-full"));

        // Deterministic disk-full: cap the database at its CURRENT page count
        // so the next growth cannot allocate. A real SQLite/SQLCipher
        // mechanism — no fabricated exception, no real volume filled.
        await using (var conn = await queue.OpenScopedAsync(CancellationToken.None))
        {
            await using (var pages = conn.CreateCommand())
            {
                pages.CommandText = "PRAGMA page_count;";
                var pageCount = Convert.ToInt64(await pages.ExecuteScalarAsync());
                await using (var cap = conn.CreateCommand())
                {
                    cap.CommandText = $"PRAGMA max_page_count={pageCount};";
                    await cap.ExecuteScalarAsync();
                }
            }

            Exception? caught = null;
            try
            {
                await queue.EnqueueAsync(MakeJob("job-neg-full-2", "Must fail when full"));
            }
            catch (Exception ex) { caught = ex; }

            Assert.NotNull(caught);
            Assert.Equal(DbFailureClass.DiskFull, DbErrorClassifier.Classify(caught));
        }

        // Lift the cap (space "freed") → queued jobs remain recoverable and
        // the previously refused write goes through.
        var jobs = await queue.ListAsync();
        Assert.Contains(jobs, j => j.JobId == "job-neg-full-1" && j.Status == JobStatus.Pending);
        var third = await queue.EnqueueAsync(MakeJob("job-neg-full-3", "After space freed"));
        Assert.Equal("job-neg-full-3", third.JobId);
    }
}
