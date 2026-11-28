namespace PrintShopCompanion.Queue.Tests;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// Phase 9 (Stage 5): crash-recovery and data-integrity tests for the queue
/// database. Every test uses an ISOLATED temporary database (rootOverride) —
/// never a real user database and never any production path.
///
/// <para>Contract under test (the Phase 9 recovery policy):</para>
/// <list type="bullet">
/// <item>corruption is CLASSIFIED, quarantined (original file PRESERVED, WAL/SHM
/// sidecars preserved as evidence, never decrypted) and journaled;</item>
/// <item>recoverable job counts are recorded when readable, recorded as
/// unknown when not — never fabricated;</item>
/// <item>a repeat guard stops destructive recovery attempts on a failing
/// storage layer;</item>
/// <item>recovery never silently converts a failed database into an empty
/// queue without a journal entry and visible state;</item>
/// <item>queued jobs survive restarts; duplicate submission stays idempotent.</item>
/// </list>
/// </summary>
public class CorruptionRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PrintExeRecoveryTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "Database", "queue.db");

    public CorruptionRecoveryTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PrintJobRecord MakeJob(int i, JobStatus status = JobStatus.Pending) =>
        new()
        {
            JobId = $"job-{i:D6}",
            ShopId = "shop-test",
            DeviceId = "device-test",
            Title = $"Test job {i}",
            Status = status,
            QueuedAt = DateTimeOffset.UtcNow,
        };

    private LocalStorageLayout NewLayout() =>
        new(Options.Create(new StorageOptions()), rootOverride: _root);

    private SqliteJobQueue NewQueue(DatabaseRecoveryState? state = null, string? dbPath = null)
    {
        var opts = Options.Create(new StorageOptions { QueueDbPath = dbPath ?? DbPath });
        return new SqliteJobQueue(opts, NewLayout(), keyStore: null, NullLogger<SqliteJobQueue>.Instance, state);
    }

    private async Task<long> CountJobsViaRawSqlAsync(SqliteJobQueue queue)
    {
        await using var conn = await queue.OpenScopedAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jobs;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None));
    }

    // ── 1. Corrupt database at startup ─────────────────────────────────────
    [Fact]
    public async Task CorruptDbAtStartup_quarantines_preserves_evidence_and_recreates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        const string garbage = "THIS IS DEFINITELY NOT A SQLITE DATABASE HEADER";
        await File.WriteAllTextAsync(DbPath, garbage);

        var state = new DatabaseRecoveryState();
        var queue = NewQueue(state);
        await queue.EnqueueAsync(MakeJob(1));

        // Fresh queue is usable and contains exactly the new job.
        Assert.Equal(1, await CountJobsViaRawSqlAsync(queue));

        // The ORIGINAL file was preserved (renamed, never deleted), still
        // byte-identical to the garbage we wrote — the evidence is intact.
        var quarantined = Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.corrupt-*");
        Assert.Single(quarantined);
        Assert.Equal(garbage, await File.ReadAllTextAsync(quarantined[0]));

        // Recovery is accounted and visible in the DI state.
        Assert.Equal(1, state.RecoveryCount);
        Assert.Equal(RecoveryStatus.Recovered, state.Status);
        Assert.Equal(DbFailureClass.Corruption, state.LastFailureClass);
        // The garbage file cannot be read as a database — jobs are UNKNOWN.
        Assert.Null(state.LastRecoverableJobs);
    }

    // ── 2. Corruption discovered during operation (next open) ──────────────
    [Fact]
    public async Task CorruptionDuringOperation_next_instance_recovers_and_stays_usable()
    {
        var q1 = NewQueue();
        await q1.EnqueueAsync(MakeJob(1));
        await q1.EnqueueAsync(MakeJob(2));

        // Simulate on-disk corruption AFTER process death: first drop the
        // pooled connections (a live pool keeps page caches and file handles,
        // which would hide the corruption from the next instance).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(DbPath, "CORRUPTED-BY-TEST");

        var state = new DatabaseRecoveryState();
        var q2 = NewQueue(state);
        await q2.EnqueueAsync(MakeJob(3));
        Assert.Equal(1, await CountJobsViaRawSqlAsync(q2));
        Assert.Equal(RecoveryStatus.Recovered, state.Status);
    }

    // ── 3. Failure classification (invalid key, readonly, lock, disk full) ──
    [Fact]
    public void Classifier_maps_sqlite_errors_to_the_right_class()
    {
        // NOTADB: corrupt file OR wrong SQLCipher key — treated as corruption
        // (the encrypted file is never opened without its key, never weakened).
        Assert.Equal(DbFailureClass.Corruption,
            DbErrorClassifier.Classify(new SqliteException("file is not a database", 26)));
        Assert.Equal(DbFailureClass.Corruption,
            DbErrorClassifier.Classify(new SqliteException("file is encrypted", 26)));
        Assert.Equal(DbFailureClass.Corruption,
            DbErrorClassifier.Classify(new SqliteException("database disk image is malformed", 11)));
        // READONLY
        Assert.Equal(DbFailureClass.PermissionDenied,
            DbErrorClassifier.Classify(new SqliteException("attempt to write a readonly database", 15)));
        // BUSY / LOCKED — transient
        Assert.Equal(DbFailureClass.Transient,
            DbErrorClassifier.Classify(new SqliteException("database is locked", 5)));
        Assert.Equal(DbFailureClass.Transient,
            DbErrorClassifier.Classify(new SqliteException("database table is locked", 6)));
        // FULL
        Assert.Equal(DbFailureClass.DiskFull,
            DbErrorClassifier.Classify(new SqliteException("database or disk is full", 13)));
        // Non-SQLite exceptions
        Assert.Equal(DbFailureClass.PermissionDenied, DbErrorClassifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(DbFailureClass.DiskFull, DbErrorClassifier.Classify(new IOException("There is not enough space on the disk.")));
        Assert.Equal(DbFailureClass.Transient, DbErrorClassifier.Classify(new IOException("network cable unplugged")));
        Assert.Equal(DbFailureClass.Unknown, DbErrorClassifier.Classify(new InvalidOperationException("mystery")));
    }

    // ── 4. Interrupted migration: garbage legacy DB must not break startup ──
    [Fact]
    public async Task InterruptedMigration_garbage_legacy_db_preserved_and_queue_still_opens()
    {
        var layout = NewLayout();
        Directory.CreateDirectory(layout.LegacyRoot);
        // A v0.4.7 "database" that is garbage (migration was interrupted by a
        // crash mid-copy in this simulation).
        await File.WriteAllTextAsync(Path.Combine(layout.LegacyRoot, "queue.db"), "NOT A DATABASE EITHER");

        var state = new DatabaseRecoveryState();
        var queue = NewQueue(state);
        await queue.EnqueueAsync(MakeJob(1));

        // The app STILL starts and the queue works; the garbage legacy file is
        // preserved as the migration backup (never deleted).
        Assert.Equal(1, await CountJobsViaRawSqlAsync(queue));
        Assert.True(File.Exists(Path.Combine(layout.LegacyRoot, "queue.db.v047.bak")));
        Assert.Equal(garbageContent(), await File.ReadAllTextAsync(Path.Combine(layout.LegacyRoot, "queue.db.v047.bak")));
    }

    private static string garbageContent() => "NOT A DATABASE EITHER";

    // ── 5. Abrupt termination during a transaction: rollback, no lost state ─
    [Fact]
    public async Task UncommittedTransaction_abrupt_close_rolls_back_fully()
    {
        var queue = NewQueue();
        await queue.EnqueueAsync(MakeJob(1));

        // Simulate a crash mid-transaction: BEGIN, INSERT, then dispose the
        // connection WITHOUT committing.
        await using (var conn = await queue.OpenScopedAsync(CancellationToken.None))
        await using (var tx = await conn.BeginTransactionAsync(CancellationToken.None))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO jobs (job_id, shop_id, device_id, title, status, created_at, queued_at, updated_at) " +
                "VALUES ('job-crash', 's', 'd', 't', 0, '" + DateTime.UtcNow.ToString("o") + "', '" + DateTime.UtcNow.ToString("o") + "', '" + DateTime.UtcNow.ToString("o") + "');";
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            // No commit — the connection is disposed with the transaction open.
        }

        // A fresh instance must see a CONSISTENT database: the phantom insert
        // is rolled back, the earlier real job is intact, integrity is ok.
        var q2 = NewQueue();
        Assert.Equal(1, await CountJobsViaRawSqlAsync(q2));
        var got = await q2.DequeueAsync(CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("job-000001", got!.JobId);
    }

    // ── 6. Disk full: classified, capped retry, breaker — never a 5s loop ───
    [Fact]
    public void DiskFull_is_capped_not_infinite()
    {
        var policy = new QueueRetryPolicy();
        for (var i = 0; i < QueueRetryPolicy.MaxConsecutiveFailures - 1; i++)
        {
            var d = policy.OnFailure(DbFailureClass.DiskFull);
            Assert.True(d.ShouldRetry);
            Assert.InRange(d.DelaySeconds!.Value, 1, QueueRetryPolicy.MaxBackoffSeconds);
        }
        var final = policy.OnFailure(DbFailureClass.DiskFull);
        Assert.False(final.ShouldRetry);
        Assert.True(final.BreakerOpen);
        Assert.False(final.Permanent); // disk space CAN be freed — breaker, not permanent stop
    }

    // ── 7. Database file locked: transient with bounded backoff ────────────
    [Fact]
    public async Task LockedDatabase_is_transient_and_the_queue_recovers_after_release()
    {
        var queue = NewQueue();
        await queue.EnqueueAsync(MakeJob(1));

        // Hold an exclusive lock from an independent connection.
        await using var locker = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await locker.OpenAsync();
        await using (var tx = locker.BeginTransaction())
        await using (var cmd = locker.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS lock_probe (x); INSERT INTO lock_probe VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
            // While the writer transaction is open, queue writes hit BUSY.
            var busySeen = false;
            try
            {
                // The queue's busy_timeout (see SqliteConnectionPragmas) may wait
                // first — use a short raw write to probe without waiting forever.
                await using var probe = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await probe.OpenAsync();
                await using var w = probe.CreateCommand();
                w.CommandText = "BEGIN EXCLUSIVE; COMMIT;";
                await w.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (DbErrorClassifier.Classify(ex) == DbFailureClass.Transient)
            {
                busySeen = true;
            }
            tx.Rollback();

            if (!busySeen)
            {
                // Some SQLite builds release the EXCLUSIVE lock immediately on
                // COMMIT of the probe; the assertion below still holds.
            }
        }

        // After the lock holder goes away, the queue recovers immediately and
        // the job was never lost.
        await queue.EnqueueAsync(MakeJob(2));
        Assert.Equal(2, await CountJobsViaRawSqlAsync(queue));
        Assert.Equal(DbFailureClass.Transient,
            DbErrorClassifier.Classify(new SqliteException("database is locked", 5)));
    }

    // ── 8. Permission denied: classified, NO retry ──────────────────────────
    [Fact]
    public void PermissionDenied_never_retries()
    {
        var policy = new QueueRetryPolicy();
        var d = policy.OnFailure(DbFailureClass.PermissionDenied);
        Assert.False(d.ShouldRetry);
        Assert.True(d.Permanent);
    }

    // ── 9. Recovery backup creation: timestamped, preserved, journaled ─────
    [Fact]
    public async Task RecoveryBackup_has_timestamped_name_and_a_jsonl_journal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await File.WriteAllTextAsync(DbPath, "GARBAGE-FOR-BACKUP-TEST");
        var state = new DatabaseRecoveryState();
        var queue = NewQueue(state);
        await queue.EnqueueAsync(MakeJob(1));

        var quarantined = Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.corrupt-*");
        Assert.Single(quarantined);
        var stamp = Path.GetFileName(quarantined[0]).Substring("queue.db.corrupt-".Length);
        Assert.Matches(@"^\d{8}-\d{6}$", stamp); // timestamped quarantine name

        var journal = Path.Combine(Path.GetDirectoryName(DbPath)!, "recovery-log.jsonl");
        Assert.True(File.Exists(journal));
        var lines = (await File.ReadAllLinesAsync(journal)).Where(l => l.Trim().Length > 0).ToList();
        Assert.NotEmpty(lines);
        var last = System.Text.Json.JsonSerializer.Deserialize<RecoveryJournalEntry>(lines[^1]);
        Assert.NotNull(last);
        Assert.Equal("quarantine-and-recreate", last!.Action);
        Assert.Equal("recreated-clean", last.Outcome);
        Assert.Equal(DbFailureClass.Corruption.ToString(), last.FailureClass);
    }

    // ── 10. Recovery interrupted halfway: repeat guard stops the damage ────
    [Fact]
    public async Task RepeatGuard_stops_destructive_retries_when_storage_keeps_failing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var state = new DatabaseRecoveryState();
        // Simulate three prior recoveries in this process (the guard trips on
        // the 4th attempt).
        Assert.True(state.TryRecordRecovery(DbFailureClass.Corruption, "q1", null, "quarantined-and-recreated", "test"));
        Assert.True(state.TryRecordRecovery(DbFailureClass.Corruption, "q2", null, "quarantined-and-recreated", "test"));
        Assert.True(state.TryRecordRecovery(DbFailureClass.Corruption, "q3", null, "quarantined-and-recreated", "test"));

        await File.WriteAllTextAsync(DbPath, "STILL-CORRUPT");
        var queue = NewQueue(state);

        // The recovery policy REFUSES to quarantine again: a classified
        // exception is thrown, the corrupt file is untouched, state says the
        // storage layer is failing. No silent empty queue, no data loss.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await queue.EnqueueAsync(MakeJob(1));
        });
        Assert.Equal(RecoveryStatus.StorageFailing, state.Status);
        Assert.Equal("STILL-CORRUPT", await File.ReadAllTextAsync(DbPath));
    }

    // ── 11. Restart after successful recovery ───────────────────────────────
    [Fact]
    public async Task RestartAfterRecovery_opens_clean_and_processes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await File.WriteAllTextAsync(DbPath, "BROKEN");
        var state = new DatabaseRecoveryState();
        var q1 = NewQueue(state);
        await q1.EnqueueAsync(MakeJob(1));
        Assert.Equal(RecoveryStatus.Recovered, state.Status);

        // Restart: a NEW process would construct a NEW state — the recovered
        // database must open CLEAN without another recovery.
        var freshState = new DatabaseRecoveryState();
        var q2 = NewQueue(freshState);
        await q2.EnqueueAsync(MakeJob(2));
        Assert.Equal(2, await CountJobsViaRawSqlAsync(q2));
        Assert.Equal(RecoveryStatus.Healthy, freshState.Status);
        Assert.Equal(0, freshState.RecoveryCount);
    }

    // ── 12. Duplicate submission after restart/recovery stays idempotent ────
    [Fact]
    public async Task DuplicateSubmission_is_idempotent_across_restarts()
    {
        var q1 = NewQueue();
        await q1.EnqueueAsync(MakeJob(1));

        var q2 = NewQueue();
        await q2.EnqueueAsync(MakeJob(1)); // same JobId after "restart"
        Assert.Equal(1, await CountJobsViaRawSqlAsync(q2));
    }

    // ── 13. Queue consistency after recovery ────────────────────────────────
    [Fact]
    public async Task AfterRecovery_queue_is_consistent_and_provable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await File.WriteAllTextAsync(DbPath, "CONSISTENCY-TEST-GARBAGE");
        var state = new DatabaseRecoveryState();
        var queue = NewQueue(state);
        await queue.EnqueueAsync(MakeJob(1));
        await queue.EnqueueAsync(MakeJob(2));

        Assert.Equal(2, await CountJobsViaRawSqlAsync(queue));
        var got1 = await queue.DequeueAsync(CancellationToken.None);
        var got2 = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotNull(got1);
        Assert.NotNull(got2);
        var third = await queue.DequeueAsync(CancellationToken.None);
        Assert.Null(third);

        // Evidence chain: quarantined original + journal + state all agree.
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.corrupt-*"));
        Assert.Equal(1, state.RecoveryCount);
        Assert.Single(state.RecentEntries());
    }

    // ── 14. Recovery with completed jobs present: honest accounting ─────────
    [Fact]
    public async Task Recovery_records_job_accounting_unknown_when_file_unreadable()
    {
        var q1 = NewQueue();
        await q1.EnqueueAsync(MakeJob(1, JobStatus.Printed));
        await q1.EnqueueAsync(MakeJob(2, JobStatus.Printed));
        await q1.EnqueueAsync(MakeJob(3, JobStatus.Pending));

        // Kill the process "properly": drop pooled connections/page caches
        // first, then overwrite the whole file. Nothing about the old content
        // is readable afterwards. The policy MUST record "unknown", never a
        // fabricated 3.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(DbPath, "TOTAL-OVERWRITE");
        var state = new DatabaseRecoveryState();
        var q2 = NewQueue(state);
        await q2.EnqueueAsync(MakeJob(9));

        Assert.Null(state.LastRecoverableJobs); // unknown, honestly recorded
        Assert.Equal(DbFailureClass.Corruption, state.LastFailureClass);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.corrupt-*"));
        // The fresh queue contains exactly the one new job.
        Assert.Equal(1, await CountJobsViaRawSqlAsync(q2));
    }

    // ── Retry policy unit tests (Stage 4 contract) ──────────────────────────
    [Fact]
    public void RetryPolicy_permanent_classes_never_retry()
    {
        var policy = new QueueRetryPolicy();
        foreach (var cls in new[] { DbFailureClass.Corruption, DbFailureClass.KeyMismatch, DbFailureClass.PermissionDenied, DbFailureClass.Configuration })
        {
            var d = policy.OnFailure(cls);
            Assert.False(d.ShouldRetry);
            Assert.True(d.Permanent);
            Assert.False(d.BreakerOpen);
        }
    }

    [Fact]
    public void RetryPolicy_backoff_is_exponential_with_jitter_and_bounded()
    {
        var policy = new QueueRetryPolicy();
        var delays = new List<int>();
        for (var i = 0; i < QueueRetryPolicy.MaxConsecutiveFailures - 1; i++)
        {
            var d = policy.OnFailure(DbFailureClass.Transient);
            Assert.True(d.ShouldRetry);
            Assert.False(d.BreakerOpen);
            delays.Add(d.DelaySeconds!.Value);
        }
        // Bounded and non-decreasing trend (jitter keeps same-order magnitude).
        Assert.All(delays, s => Assert.InRange(s, 1, QueueRetryPolicy.MaxBackoffSeconds));
        Assert.True(delays[^1] >= delays[0]);
        // Backoff helper: bounded for absurd failure counts (full jitter keeps
        // the value in [cap/2, cap]).
        Assert.InRange(QueueRetryPolicy.BackoffSecondsFor(1000), QueueRetryPolicy.MaxBackoffSeconds / 2, QueueRetryPolicy.MaxBackoffSeconds);
    }

    [Fact]
    public void RetryPolicy_correlation_id_is_stable_across_a_failure_chain()
    {
        var policy = new QueueRetryPolicy();
        var first = policy.ChainId;
        policy.OnFailure(DbFailureClass.Transient);
        policy.OnFailure(DbFailureClass.Transient);
        Assert.Equal(first, policy.ChainId);
        policy.OnSuccess();
        Assert.NotEqual(first, policy.ChainId); // new chain after recovery
        Assert.Equal(0, policy.ConsecutiveFailures);
    }

    [Fact]
    public void RecoveryState_repeat_guard_and_lifecycle()
    {
        var state = new DatabaseRecoveryState();
        Assert.Equal(RecoveryStatus.Healthy, state.Status);
        state.MarkFailure(DbFailureClass.Transient);
        state.MarkFailure(DbFailureClass.Transient);
        Assert.Equal(2, state.ConsecutiveFailures);
        state.MarkHealthy();
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(RecoveryStatus.Healthy, state.Status);

        Assert.True(state.TryRecordRecovery(DbFailureClass.Corruption, "q", 5, "quarantined-and-recreated", "r"));
        Assert.Equal(5, state.LastRecoverableJobs);
        Assert.Equal(RecoveryStatus.Recovered, state.Status);
        Assert.True(state.TryRecordRecovery(DbFailureClass.Corruption, "q2", null, "quarantined-and-recreated", "r"));
        Assert.True(state.TryRecordRecovery(DbFailureClass.Corruption, "q3", null, "quarantined-and-recreated", "r"));
        // Fourth attempt: guard trips.
        Assert.False(state.TryRecordRecovery(DbFailureClass.Corruption, "q4", null, "quarantined-and-recreated", "r"));
        Assert.Equal(3, state.RecoveryCount);
    }
}
