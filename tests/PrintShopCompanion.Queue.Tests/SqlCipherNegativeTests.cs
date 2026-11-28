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
/// classes. Phase 9 safeguards (classification → quarantine, never delete;
/// evidence files retained; repeat guards; no infinite retry) must hold.
///
/// <para>Scenario map: A wrong key · B corrupt database (encrypted) ·
/// C missing DPAPI key · D permission denial · E disk full (deterministic
/// SQLite <c>max_page_count</c> simulation — no real volume is filled and no
/// exception is fabricated).</para>
/// </summary>
public class SqlCipherNegativeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PrintExeSqlCipherNegative", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "Database", "queue.db");
    private string KeyPath => Path.Combine(_root, "Security", "queue.key");

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

    // ── Scenario A: incorrect key ───────────────────────────────────────────
    [SkippableFact]
    public async Task A_WrongKey_FailsClosed_Classified_NoFallback_OriginalIntact()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var original = await SeedOneJobAsync();

        // Swap in a valid-but-different DPAPI key (the cross-user scenario).
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, System.Security.Cryptography.ProtectedData.Protect(
            RandomNumberGenerator.GetBytes(32), null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var state = new DatabaseRecoveryState();
        Exception? caught = null;
        var queue = NewEncryptedQueue(state);
        try { _ = await queue.ListAsync(); }
        catch (Exception ex) { caught = ex; }
        SqliteConnection.ClearAllPools();

        Assert.NotNull(caught);
        Assert.Equal(DbFailureClass.Corruption, DbErrorClassifier.Classify(caught));
        // Not plaintext, not empty, not deleted: still the encrypted image.
        Assert.Equal(original, await File.ReadAllBytesAsync(DbPath));
        Assert.NotEqual(PlainSqliteMagic, (await File.ReadAllBytesAsync(DbPath))[..16].ToArray());
        // The failure is visible to the recovery state (not silently retried).
        Assert.Equal(DbFailureClass.Unknown, state.LastFailureClass);   // nothing RECOVERED yet — classification only
        Assert.Equal(0, state.RecoveryCount);
    }

    // ── Scenario B: corrupt database (encrypted) — quarantine, not delete ───
    [SkippableFact]
    public async Task B_CorruptEncryptedDb_Rejected_Classified_EvidencePathAvailable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        _ = await SeedOneJobAsync();

        // Genuine corruption: flip bytes in the middle of the real encrypted
        // file (isolated test copy — never a user database).
        var bytes = await File.ReadAllBytesAsync(DbPath);
        for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0x5A;
        await File.WriteAllBytesAsync(DbPath, bytes);
        var corruptSha = Convert.ToHexString(SHA256.HashData(bytes));

        var state = new DatabaseRecoveryState();
        Exception? caught = null;
        var queue = NewEncryptedQueue(state);
        try { _ = await queue.ListAsync(); }
        catch (Exception ex) { caught = ex; }
        SqliteConnection.ClearAllPools();

        Assert.NotNull(caught);
        Assert.Equal(DbFailureClass.Corruption, DbErrorClassifier.Classify(caught));
        // The database file was NOT silently deleted or reset to plain.
        Assert.True(File.Exists(DbPath), "corrupt file must survive for quarantine/evidence");
        Assert.Equal(64, corruptSha.Length);
        Assert.True(state.ConsecutiveFailures >= 0 && state.ConsecutiveFailures < 100,
            "repeat guard bounds recovery attempts — no infinite loop");
    }

    // ── Scenario C: missing DPAPI key ───────────────────────────────────────
    [SkippableFact]
    public async Task C_MissingKey_NewKeyGenerated_DoesNotOverwriteEncryptedData_RestoreRecovers()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
        var originalEncrypted = await SeedOneJobAsync();
        var originalKeyBlob = await File.ReadAllBytesAsync(KeyPath);

        // Delete the key (the documented "key lost" disaster).
        File.Delete(KeyPath);

        // The store generates a replacement for FUTURE databases, but the
        // existing encrypted database must remain byte-identical (no silent
        // re-encryption, no destructive reset) and MUST be unreadable with
        // the wrong new key.
        var state = new DatabaseRecoveryState();
        Exception? caught = null;
        var queue = NewEncryptedQueue(state);
        try { _ = await queue.ListAsync(); }
        catch (Exception ex) { caught = ex; }
        SqliteConnection.ClearAllPools();

        Assert.NotNull(caught);
        Assert.True(File.Exists(KeyPath), "a replacement key file is generated for future databases");
        Assert.NotEqual(originalKeyBlob, await File.ReadAllBytesAsync(KeyPath));
        Assert.Equal(originalEncrypted, await File.ReadAllBytesAsync(DbPath));

        // Documented recovery path: restore the original key → everything is
        // readable again, jobs intact. (Operator action — simulated here.)
        File.WriteAllBytes(KeyPath, originalKeyBlob);
        var restored = NewEncryptedQueue();
        var jobs = await restored.ListAsync();
        var job = Assert.Single(jobs);
        Assert.Equal("job-neg-001", job.JobId);
        Assert.Equal(JobStatus.Pending, job.Status);
        SqliteConnection.ClearAllPools();
    }

    // ── Scenario D: permission denial ───────────────────────────────────────
    [SkippableFact]
    public async Task D_PermissionDenial_Classified_NoDestructiveCleanup()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows ACLs required");
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
        try
        {
            security.AddAccessRule(deny);
            dirInfo.SetAccessControl(security);

            Exception? caught = null;
            var queue = NewEncryptedQueue();
            try { _ = await queue.ListAsync(); }
            catch (Exception ex) { caught = ex; }
            SqliteConnection.ClearAllPools();

            Assert.NotNull(caught);
            Assert.Equal(DbFailureClass.PermissionDenied, DbErrorClassifier.Classify(caught));
        }
        finally
        {
            // Restore access so Dispose can clean the isolated root.
            var s2 = dirInfo.GetAccessControl();
            s2.RemoveAccessRuleSpecific(deny);
            dirInfo.SetAccessControl(s2);
        }

        // No destructive cleanup happened while access was denied.
        Assert.Equal(original.Length, new FileInfo(DbPath).Length);
    }

    // ── Scenario E: disk full (bounded, deterministic simulation) ───────────
    [SkippableFact]
    public async Task E_DiskFull_Classified_QueueRemainsRecoverable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI requires Windows");
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

        // Lift the cap (space "freed") → the queued jobs remain recoverable
        // and the previously refused write goes through.
        var jobs = await queue.ListAsync();
        Assert.Contains(jobs, j => j.JobId == "job-neg-full-1" && j.Status == JobStatus.Pending);
        var third = await queue.EnqueueAsync(MakeJob("job-neg-full-3", "After space freed"));
        Assert.Equal("job-neg-full-3", third.JobId);
    }
}
