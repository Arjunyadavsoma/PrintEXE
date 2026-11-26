using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Diagnostics;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>Minimal IOptionsMonitor stub (file-local copy of the pattern used by WindowsRuntimeSecurityTests).</summary>
internal sealed class StubOptionsMonitor<TValue> : Microsoft.Extensions.Options.IOptionsMonitor<TValue>
    where TValue : class, new()
{
    private readonly TValue _value;
    public StubOptionsMonitor(TValue value) => _value = value;
    public TValue CurrentValue => _value;
    public TValue Get(string? name) => _value;
    public System.IDisposable? OnChange(Action<TValue?, string?> listener) => null;
}

/// <summary>
/// Decision D-07 — the diagnostics aggregator. Verifies the degradation path
/// (backend unreachable → error recorded, not a crash) and the live path
/// (queue counts + last successful print from a real SQLite queue).
/// </summary>
public sealed class DiagnosticsServiceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psh-diag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SqliteJobQueue NewQueue(string root)
    {
        var layout = new LocalStorageLayout(
            Options.Create(new StorageOptions()),
            rootOverride: root); // v0.7.1: named arg — was misused as legacyRootOverride
        return new SqliteJobQueue(
            Options.Create(new StorageOptions { QueueDbPath = Path.Combine(root, "queue.db") }),
            layout,
            keyStore: null, // plain SQLite on the neutral TFM (Linux); SQLCipher path covered on Windows CI
            NullLogger<SqliteJobQueue>.Instance);
    }

    private static DiagnosticsService NewService(string root, string baseUrl, SqliteJobQueue queue)
    {
        var backend = new HttpPrintShopBackend(
            new HttpClient { BaseAddress = new Uri(baseUrl) },
            new StubOptionsMonitor<BackendOptions>(new BackendOptions { BaseUrl = baseUrl }),
            NullLogger<HttpPrintShopBackend>.Instance,
            new LocalStorageLayout(
                Options.Create(new StorageOptions()),
                rootOverride: root)); // v0.7.1: named arg — was misused as legacyRootOverride
        return new DiagnosticsService(
            new StubOptionsMonitor<BackendOptions>(new BackendOptions { BaseUrl = baseUrl }),
            backend,
            queue,
            new LocalStorageLayout(
                Options.Create(new StorageOptions()),
                rootOverride: root), // v0.7.1: named arg — was misused as legacyRootOverride
            NullLogger<DiagnosticsService>.Instance,
            queueStats: queue);
    }

    private static async Task EnqueueAsync(SqliteJobQueue q, string jobId, JobStatus status, DateTimeOffset? printedAt = null)
    {
        var rec = new PrintShopCompanion.Core.Models.PrintJobRecord
        {
            JobId = jobId, ShopId = "shop", DeviceId = "dev", Title = jobId,
            Copies = 1, Status = status,
            CreatedAt = DateTimeOffset.UtcNow, QueuedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await q.EnqueueAsync(rec);
        if (printedAt is not null)
            await q.MarkPrintedAsync(jobId, printedAt.Value, spoolerJobId: 0);
    }

    [Fact]
    public async Task Unreachable_backend_degrades_to_errors_not_crash()
    {
        var root = NewTempDir();
        var queue = NewQueue(root);
        // Port 1 is un-routable on loopback in practice; the heartbeat fails fast.
        var svc = NewService(root, "http://127.0.0.1:1", queue);
        var snap = await svc.CollectAsync();

        Assert.False(snap.CloudReachable);
        Assert.False(snap.Healthy);
        Assert.Contains(snap.Errors, e => e.Contains("heartbeat", StringComparison.OrdinalIgnoreCase));
        Assert.True(snap.DatabaseHealthy);
    }

    [Fact]
    public async Task Queue_counts_and_last_print_are_reported()
    {
        var root = NewTempDir();
        var queue = NewQueue(root);
        await EnqueueAsync(queue, "J-printed", JobStatus.Printed, DateTimeOffset.UtcNow.AddMinutes(-5));
        await EnqueueAsync(queue, "J-pending", JobStatus.Pending);
        await EnqueueAsync(queue, "J-uncertain", JobStatus.UncertainSpooled);

        var svc = NewService(root, "http://127.0.0.1:1", queue);
        var snap = await svc.CollectAsync();

        Assert.Equal(1, snap.QueueCounts.GetValueOrDefault(PrintShopCompanion.Core.Models.JobStatus.Pending));
        Assert.Equal(1, snap.QueueCounts.GetValueOrDefault(PrintShopCompanion.Core.Models.JobStatus.UncertainSpooled));
        Assert.Equal(1, snap.QueueCounts.GetValueOrDefault(PrintShopCompanion.Core.Models.JobStatus.Printed));
        Assert.NotNull(snap.LastSuccessfulPrintAt);
    }

    [Fact]
    public async Task Export_writes_a_zip_with_diagnostics_json()
    {
        var root = NewTempDir();
        var queue = NewQueue(root);
        var svc = NewService(root, "http://127.0.0.1:1", queue);
        var zipPath = Path.Combine(root, "diag.zip");
        await svc.ExportDiagnosticsAsync(zipPath);

        Assert.True(File.Exists(zipPath));
        Assert.True(new FileInfo(zipPath).Length > 0);
        using var stream = File.OpenRead(zipPath);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.FullName == "diagnostics.json");
        var entry = archive.GetEntry("diagnostics.json")!;
        using var reader = new StreamReader(entry.Open());
        var json = await reader.ReadToEndAsync();
        Assert.Contains("\"CloudReachable\"", json);
    }
}
