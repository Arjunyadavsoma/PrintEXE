using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

namespace PrintShopCompanion.Queue.Tests;

/// <summary>
/// Decision D-08 — priority dequeue semantics: higher priority first, FIFO
/// within the same priority; also verifies the v0.6 columns survive a
/// close-and-reopen round trip (the audit's original data-loss bug class).
/// </summary>
public sealed class QueuePriorityTests
{
    private static (SqliteJobQueue Queue, string Root) NewQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), "psh-priority-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // v0.7.1 fix: the temp dir is the ROOT override (named arg). Passing it
        // positionally made it the LEGACY root override, leaving Root at the
        // real %LocalAppData% — the second open then classified our own
        // queue.db as a v0.4.7 legacy DB and tried to move it on Windows CI.
        var layout = new LocalStorageLayout(
            Options.Create(new StorageOptions()),
            rootOverride: root);
        var queue = new SqliteJobQueue(
            Options.Create(new StorageOptions { QueueDbPath = Path.Combine(root, "queue.db") }),
            layout,
            keyStore: null,
            NullLogger<SqliteJobQueue>.Instance);
        return (queue, root);
    }

    private static PrintJobRecord Job(string id, int priority, int minutesAgo = 10) => new()
    {
        JobId = id, ShopId = "shop", DeviceId = "dev", Title = id,
        Copies = 1, Status = JobStatus.Pending, Priority = priority,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Higher_priority_dequeues_first()
    {
        var (queue, _) = NewQueue();
        await queue.EnqueueAsync(Job("normal-1", priority: 0, minutesAgo: 20));
        await queue.EnqueueAsync(Job("rush-1", priority: 10, minutesAgo: 10));
        await queue.EnqueueAsync(Job("normal-2", priority: 0, minutesAgo: 5));

        var first = await queue.DequeueAsync();
        Assert.Equal("rush-1", first!.JobId);
    }

    [Fact]
    public async Task Same_priority_is_fifo()
    {
        var (queue, _) = NewQueue();
        await queue.EnqueueAsync(Job("a", priority: 0, minutesAgo: 20));
        await queue.EnqueueAsync(Job("b", priority: 0, minutesAgo: 10));
        await queue.EnqueueAsync(Job("c", priority: 0, minutesAgo: 5));

        Assert.Equal("a", (await queue.DequeueAsync())!.JobId);
        Assert.Equal("b", (await queue.DequeueAsync())!.JobId);
        Assert.Equal("c", (await queue.DequeueAsync())!.JobId);
    }

    [Fact]
    public async Task Priority_round_trips_through_restart()
    {
        var (queue, root) = NewQueue();
        var rec = Job("rush-rt", priority: 10);
        rec.DueAt = DateTimeOffset.UtcNow.AddHours(2);
        await queue.EnqueueAsync(rec);

        // Reopen the same DB file — simulates a restart.
        var layout = new LocalStorageLayout(
            Options.Create(new StorageOptions()),
            rootOverride: root);
        var reopened = new SqliteJobQueue(
            Options.Create(new StorageOptions { QueueDbPath = Path.Combine(root, "queue.db") }),
            layout,
            keyStore: null,
            NullLogger<SqliteJobQueue>.Instance);

        var all = await reopened.ListAsync();
        var job = all.FirstOrDefault(j => j.JobId == "rush-rt");
        Assert.NotNull(job);
        Assert.Equal(10, job!.Priority);
        Assert.Equal(10, job.Priority);
        Assert.NotNull(job.DueAt);
    }
}
