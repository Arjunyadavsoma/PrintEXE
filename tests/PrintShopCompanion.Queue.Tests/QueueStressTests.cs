namespace PrintShopCompanion.Queue.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// Phase 9 (Stage 9): queue stress tests. Isolated temporary databases only.
/// These are not micro-benchmarks — they prove CORRECTNESS under volume and
/// repetition: 1,000 queued jobs, mixed states, repeated restarts, concurrent
/// writers, and repeated recovery attempts must all behave.
/// </summary>
public class QueueStressTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PrintExeStressTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_root, "Database", "queue.db");

    public QueueStressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PrintJobRecord MakeJob(int i, JobStatus status = JobStatus.Pending) =>
        new()
        {
            JobId = $"stress-{i:D6}",
            ShopId = "shop-test",
            DeviceId = "device-test",
            Title = $"Stress job {i}",
            Status = status,
            QueuedAt = DateTimeOffset.UtcNow,
        };

    private SqliteJobQueue NewQueue() =>
        new(Options.Create(new StorageOptions { QueueDbPath = DbPath }), NewLayout(),
            keyStore: null, NullLogger<SqliteJobQueue>.Instance);

    private LocalStorageLayout NewLayout() =>
        new(Options.Create(new StorageOptions()), rootOverride: _root);

    private async Task<long> CountAsync(SqliteJobQueue queue)
    {
        await using var conn = await queue.OpenScopedAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jobs;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None));
    }

    // ── 1,000 queued jobs: volume + mixed states + throughput sanity ────────
    [Fact]
    public async Task ThousandJobs_enqueue_dequeue_and_mixed_states_stay_correct()
    {
        var queue = NewQueue();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var status = i % 10 == 0 ? JobStatus.Printed : (i % 7 == 0 ? JobStatus.Failed : JobStatus.Pending);
            await queue.EnqueueAsync(MakeJob(i, status));
        }
        var enqueueMs = sw.ElapsedMilliseconds;

        Assert.Equal(1000, await CountAsync(queue));
        // Duplicate pass must be idempotent (no growth).
        for (var i = 0; i < 1000; i++) await queue.EnqueueAsync(MakeJob(i, JobStatus.Pending));
        Assert.Equal(1000, await CountAsync(queue));

        // Dequeue drains ONLY pending jobs (terminal states are skipped) and
        // every pending job comes out exactly once. NOTE: DequeueAsync marks
        // rows in-flight, so the pending count must be taken BEFORE the drain.
        int pendingBefore;
        await using (var conn = await queue.OpenScopedAsync(CancellationToken.None))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM jobs WHERE status = 0;"; // Pending
            pendingBefore = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        var seen = new HashSet<string>();
        while (true)
        {
            var job = await queue.DequeueAsync(CancellationToken.None);
            if (job is null) break;
            Assert.True(seen.Add(job.JobId), $"duplicate dequeue of {job.JobId}");
            Assert.NotEqual(JobStatus.Printed, job.Status);
        }
        // Exactly the pending rows were drained — none lost, none duplicated.
        Assert.Equal(pendingBefore, seen.Count);

        // Volume sanity for CI hardware (local run observed < 3s): generous
        // ceiling so slow runners stay green — this is a correctness gate
        // first, a rough throughput guard second.
        Assert.True(enqueueMs < 60_000, $"enqueue of 1000 jobs took {enqueueMs}ms");
    }

    // ── Repeated application restarts: no drift, no loss ────────────────────
    [Fact]
    public async Task TenRestarts_preserve_every_job()
    {
        for (var round = 0; round < 10; round++)
        {
            var queue = NewQueue();
            await queue.EnqueueAsync(MakeJob(round));
            // Simulate restart: dispose by dropping references; a new instance
            // must see ALL previously enqueued jobs.
            var next = NewQueue();
            Assert.Equal(round + 1, await CountAsync(next));
        }
    }

    // ── Concurrent writers: the semaphore gate keeps counts exact ───────────
    [Fact]
    public async Task ConcurrentEnqueue_is_serialized_and_lossless()
    {
        var queue = NewQueue();
        var tasks = new List<Task>();
        for (var w = 0; w < 10; w++)
        {
            var worker = w;
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < 10; i++)
                    await queue.EnqueueAsync(MakeJob(worker * 100 + i));
            }));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(100, await CountAsync(queue));
    }

    // ── Repeated RECOVERY attempts: guard stops destructive loops ───────────
    [Fact]
    public async Task RepeatedRecoveryAttempts_stay_safe_and_visible()
    {
        var state = new DatabaseRecoveryState();
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        for (var round = 0; round < 5; round++)
        {
            // Simulate process death before each corruption: a live pool keeps
            // page caches that would hide the on-disk corruption.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await File.WriteAllTextAsync(DbPath, $"CORRUPT-ROUND-{round}");
            var queue = new SqliteJobQueue(
                Options.Create(new StorageOptions { QueueDbPath = DbPath }), NewLayout(),
                keyStore: null, NullLogger<SqliteJobQueue>.Instance, state);
            try
            {
                await queue.EnqueueAsync(MakeJob(round));
            }
            catch (InvalidOperationException)
            {
                // Repeat guard tripped — expected from the 4th recovery on.
                break;
            }
            Assert.True(state.RecoveryCount >= round + 1);
        }
        // Guard bounded the destructive work at 3 recoveries.
        Assert.True(state.RecoveryCount <= 3);
        // Every successful recovery is journaled with honest job accounting.
        Assert.All(state.RecentEntries(), e =>
        {
            Assert.Equal(DbFailureClass.Corruption.ToString(), e.FailureClass);
            Assert.Null(e.RecoverableJobs); // garbage files are unreadable → unknown
        });
    }

    // ── Large-PDF cleanup: retention deletes expired terminal-job files ────
    [Fact]
    public async Task RetentionCleanup_deletes_expired_terminal_job_files_and_keeps_the_db()
    {
        var layout = NewLayout();
        Directory.CreateDirectory(layout.JobsCompletedDir);
        var expiredPdf = Path.Combine(layout.JobsCompletedDir, "expired.pdf");
        var freshPdf = Path.Combine(layout.JobsCompletedDir, "fresh.pdf");
        await File.WriteAllTextAsync(expiredPdf, "expired pdf content");
        await File.WriteAllTextAsync(freshPdf, "fresh pdf content");

        var queue = NewQueue();
        var job = MakeJob(1, JobStatus.Failed); // terminal → eligible
        job.LocalPdfPath = expiredPdf;
        job.LocalDeleteAfter = DateTimeOffset.UtcNow.AddMinutes(-1); // deadline passed
        await queue.EnqueueAsync(job);
        job.Status = JobStatus.Failed;
        await queue.UpdateAsync(job);

        var monitor = new FakeOptionsMonitor(new StorageOptions
        {
            QueueDbPath = DbPath,
            LocalRetentionMinutes = 60,
        });
        var retention = new FileRetentionManager(
            queue, layout, monitor, NullLogger<FileRetentionManager>.Instance);
        retention.Start(CancellationToken.None);
        await Task.Delay(1500); // cleanup runs once immediately on Start

        Assert.False(File.Exists(expiredPdf));  // expired terminal file removed
        Assert.True(File.Exists(freshPdf));     // the other file is not managed by this job
        Assert.Equal(1, await CountAsync(queue)); // DB rows unaffected
        retention.StopAsync().Wait(2000);
    }

    private sealed class FakeOptionsMonitor(StorageOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue => value;
        public StorageOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }
}
