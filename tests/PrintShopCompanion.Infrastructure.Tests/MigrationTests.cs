namespace PrintShopCompanion.Infrastructure.Tests;

using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// xUnit collection definition for the Migration test collection.
/// Forces all tests in this collection to run sequentially (not in parallel).
/// </summary>
[CollectionDefinition("Migration", DisableParallelization = true)]
public class MigrationCollection { }

/// <summary>
/// v0.4.7 → v0.5 migration tests. Creates a real v0.4.7-style database
/// (plain SQLite, old schema) and verifies that MigrateFromV047Async
/// correctly reads all rows, creates the new database, and preserves every
/// field.
///
/// These tests run on Linux (without DPAPI/SQLCipher) by passing
/// keyStore: null — the migration logic is the same, just without the
/// encryption step.
///
/// Tests are serialized via [Collection("Migration")] because they share
/// the same legacy queue.db path and would interfere with each other if
/// run in parallel.
///
/// P0 FIX: All test paths are inside the assigned temp directory — tests
/// NEVER touch the real %AppData% or %LocalAppData% paths. The
/// legacyRootOverride parameter on LocalStorageLayout redirects all
/// legacy path computations to the temp directory.
/// </summary>
[Collection("Migration")]
public class MigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _legacyRoot;
    private readonly LocalStorageLayout _layout;

    public MigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MigrationTests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);

        // P0 FIX: use legacyRootOverride to point the legacy paths at a temp
        // directory INSIDE _tempDir, so tests never touch the real
        // %AppData%\PrintShop\Companion\queue.db. This prevents test-induced
        // data loss on machines with a real PrintShop install.
        _legacyRoot = Path.Combine(_tempDir, "Legacy");
        var newDbPath = Path.Combine(_tempDir, "Database", "queue.db");
        _layout = new LocalStorageLayout(
            Options.Create(new StorageOptions { QueueDbPath = newDbPath }),
            legacyRootOverride: _legacyRoot);
    }

    private string OldDbPath => _layout.LegacyQueueDbPath!;

    public void Dispose()
    {
        // Clear all SQLite connection pools so file handles are released
        // before we try to delete the test files.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // P0 FIX: everything is inside _tempDir — including the legacy root
        // (via legacyRootOverride). We only delete _tempDir, never the real
        // %AppData% or %LocalAppData%.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Create a real v0.4.7-style database at the legacy path with the OLD
    /// schema (no v0.5 columns). Insert test jobs with realistic data.
    /// </summary>
    private void CreateV047Database(params PrintJobRecord[] jobs)
    {
        // Ensure the directory exists and the file is fresh.
        Directory.CreateDirectory(Path.GetDirectoryName(OldDbPath)!);
        // Clean up ALL migration artifacts from previous tests:
        if (File.Exists(OldDbPath)) File.Delete(OldDbPath);
        if (File.Exists(OldDbPath + ".v047.bak")) File.Delete(OldDbPath + ".v047.bak");
        var markerDir = Path.GetDirectoryName(_layout.QueueDbPath) ?? _layout.DatabaseDir;
        var marker = Path.Combine(markerDir, ".migrated");
        if (File.Exists(marker)) File.Delete(marker);
        if (File.Exists(_layout.QueueDbPath)) File.Delete(_layout.QueueDbPath);
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = OldDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = "DROP TABLE IF EXISTS jobs;";
        dropCmd.ExecuteNonQuery();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE jobs (
                job_id              TEXT    PRIMARY KEY,
                shop_id             TEXT    NOT NULL,
                device_id           TEXT    NOT NULL,
                title               TEXT    NOT NULL,
                printer_name        TEXT,
                copies              INTEGER NOT NULL DEFAULT 1,
                page_range          TEXT,
                paper_size          TEXT,
                color               INTEGER,
                duplex              TEXT,
                pdf_url             TEXT,
                local_pdf_path       TEXT,
                pdf_sha256          TEXT,
                status              INTEGER NOT NULL,
                spooler_job_id      INTEGER,
                attempt_count       INTEGER NOT NULL DEFAULT 0,
                last_error          TEXT,
                created_at          TEXT    NOT NULL,
                queued_at           TEXT    NOT NULL,
                updated_at          TEXT    NOT NULL,
                printed_at          TEXT,
                next_attempt_at     TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        foreach (var job in jobs)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO jobs (job_id, shop_id, device_id, title, printer_name, copies,
                    page_range, paper_size, color, duplex, pdf_url, local_pdf_path, pdf_sha256,
                    status, spooler_job_id, attempt_count, last_error,
                    created_at, queued_at, updated_at, printed_at, next_attempt_at)
                VALUES (@job_id, @shop_id, @device_id, @title, @printer_name, @copies,
                    @page_range, @paper_size, @color, @duplex, @pdf_url, @local_pdf_path, @pdf_sha256,
                    @status, @spooler_job_id, @attempt_count, @last_error,
                    @created_at, @queued_at, @updated_at, @printed_at, @next_attempt_at);
                """;
            AddParam(insert, "@job_id", job.JobId);
            AddParam(insert, "@shop_id", job.ShopId);
            AddParam(insert, "@device_id", job.DeviceId);
            AddParam(insert, "@title", job.Title);
            AddParam(insert, "@printer_name", job.PrinterName);
            AddParam(insert, "@copies", job.Copies);
            AddParam(insert, "@page_range", job.PageRange);
            AddParam(insert, "@paper_size", job.PaperSize);
            AddParam(insert, "@color", job.Color);
            AddParam(insert, "@duplex", job.Duplex);
            AddParam(insert, "@pdf_url", job.PdfUrl?.ToString());
            AddParam(insert, "@local_pdf_path", job.LocalPdfPath);
            AddParam(insert, "@pdf_sha256", job.PdfSha256);
            AddParam(insert, "@status", (int)job.Status);
            AddParam(insert, "@spooler_job_id", job.SpoolerJobId);
            AddParam(insert, "@attempt_count", job.AttemptCount);
            AddParam(insert, "@last_error", job.LastError);
            AddParam(insert, "@created_at", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            AddParam(insert, "@queued_at", job.QueuedAt.ToString("O", CultureInfo.InvariantCulture));
            AddParam(insert, "@updated_at", job.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            AddParam(insert, "@printed_at", job.PrintedAt?.ToString("O", CultureInfo.InvariantCulture));
            AddParam(insert, "@next_attempt_at", null);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Migration_test_paths_are_isolated_from_real_appdata()
    {
        // P0 REGRESSION TEST: verify that OldDbPath and _layout.QueueDbPath
        // are both inside _tempDir — never the real %AppData% or %LocalAppData%.
        // If this test fails, the test infrastructure is broken and could
        // delete real user data.
        OldDbPath.Should().StartWith(_tempDir,
            "the legacy DB path must be inside the test temp directory, not the real %AppData%");
        _layout.QueueDbPath.Should().StartWith(_tempDir,
            "the new DB path must be inside the test temp directory, not the real %LocalAppData%");
        _layout.LegacyRoot.Should().StartWith(_tempDir,
            "the legacy root must be inside the test temp directory");
    }

    private static void AddParam(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static PrintJobRecord MakeJob(string id, string title, JobStatus status) => new()
    {
        JobId = id,
        ShopId = "shop_test",
        DeviceId = "dev_test",
        Title = title,
        Copies = 1,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
    };

    [Fact]
    public async Task Migration_preserves_pending_jobs()
    {
        // Clean up any existing sentinel or old DB from previous tests.
        _layout.MarkMigrationComplete();  // Force-create the sentinel so migration is skipped.
        // Then delete it to force re-migration.
        // Delete any existing new DB.

        CreateV047Database(MakeJob("J-001", "document.pdf", JobStatus.Pending));
        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);

        await queue.ReplayInFlightAsync();

        var all = await queue.ListAsync();
        all.Should().HaveCount(1);
        all[0].JobId.Should().Be("J-001");
        all[0].Title.Should().Be("document.pdf");
        all[0].Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task Migration_preserves_completed_jobs()
    {

        var printedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var job = MakeJob("J-002", "completed.pdf", JobStatus.Printed);
        job.PrintedAt = printedAt;
        job.SpoolerJobId = 42;
        CreateV047Database(job);

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue.ReplayInFlightAsync();

        var all = await queue.ListAsync();
        all.Should().HaveCount(1);
        all[0].Status.Should().Be(JobStatus.Printed);
        all[0].SpoolerJobId.Should().Be(42);
        all[0].PrintedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Migration_preserves_failed_jobs()
    {

        var job = MakeJob("J-003", "failed.pdf", JobStatus.Failed);
        job.LastError = "Printer offline";
        CreateV047Database(job);

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue.ReplayInFlightAsync();

        var all = await queue.ListAsync();
        all.Should().HaveCount(1);
        all[0].Status.Should().Be(JobStatus.Failed);
        all[0].LastError.Should().Be("Printer offline");
    }

    [Fact]
    public async Task Migration_preserves_multiple_jobs()
    {

        CreateV047Database(
            MakeJob("J-001", "first.pdf", JobStatus.Pending),
            MakeJob("J-002", "second.pdf", JobStatus.Printed),
            MakeJob("J-003", "third.pdf", JobStatus.Failed),
            MakeJob("J-004", "fourth.pdf", JobStatus.Cancelled)
        );

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue.ReplayInFlightAsync();

        var all = await queue.ListAsync();
        all.Should().HaveCount(4);
        all.Select(j => j.JobId).Should().BeEquivalentTo(new[] { "J-001", "J-002", "J-003", "J-004" });
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {

        CreateV047Database(MakeJob("J-001", "doc.pdf", JobStatus.Pending));

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });

        // First run — migrates.
        var queue1 = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue1.ReplayInFlightAsync();
        var firstCount = (await queue1.ListAsync()).Count;
        firstCount.Should().Be(1);

        // Second run — should NOT re-migrate (sentinel file exists).
        var queue2 = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue2.ReplayInFlightAsync();
        var secondCount = (await queue2.ListAsync()).Count;
        secondCount.Should().Be(1, "re-running the migration should not duplicate jobs");
    }

    [Fact]
    public async Task Migration_handles_missing_source()
    {

        // No old database created — migration should be a no-op.
        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue.ReplayInFlightAsync();
        var all = await queue.ListAsync();
        all.Should().BeEmpty("no source database means no jobs to migrate");
    }

    [Fact]
    public async Task Migration_preserves_pdf_url_and_sha256()
    {

        var job = new PrintJobRecord
        {
            JobId = "J-005",
            ShopId = "shop_test",
            DeviceId = "dev_test",
            Title = "with_url.pdf",
            Copies = 1,
            Status = JobStatus.Pending,
            PdfUrl = new Uri("https://example.com/file.pdf"),
            PdfSha256 = "abc123def456",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
        };
        CreateV047Database(job);

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue.ReplayInFlightAsync();

        var all = await queue.ListAsync();
        all[0].PdfUrl.Should().NotBeNull();
        all[0].PdfUrl!.ToString().Should().Be("https://example.com/file.pdf");
        all[0].PdfSha256.Should().Be("abc123def456");
    }

    [Fact]
    public async Task Migration_backup_is_preserved()
    {

        CreateV047Database(MakeJob("J-001", "doc.pdf", JobStatus.Pending));

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        var queue = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue.ReplayInFlightAsync();

        // The old database should have been renamed to queue.db.v047.bak
        File.Exists(OldDbPath + ".v047.bak").Should().BeTrue("the backup must be preserved for rollback");
        File.Exists(OldDbPath).Should().BeFalse("the original must have been renamed (not deleted)");
    }

    [Fact]
    public async Task Migration_does_not_overwrite_existing_destination()
    {
        // Create a v0.4.7 source.
        CreateV047Database(MakeJob("J-old", "old.pdf", JobStatus.Pending));

        var opts = Options.Create(new StorageOptions { QueueDbPath = _layout.QueueDbPath });
        // First queue: triggers migration (old DB → new DB with J-old).
        var queue1 = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue1.ReplayInFlightAsync();
        // Then enqueue a new job.
        await queue1.EnqueueAsync(MakeJob("J-new", "new.pdf", JobStatus.Pending));
        var count1 = (await queue1.ListAsync()).Count;
        count1.Should().Be(2, "migration adds J-old and then we enqueue J-new");

        // A second queue instance should NOT re-migrate (sentinel exists).
        var queue2 = new SqliteJobQueue(opts, _layout, keyStore: null, NullLogger<SqliteJobQueue>.Instance);
        await queue2.ReplayInFlightAsync();
        var count2 = (await queue2.ListAsync()).Count;
        count2.Should().Be(2, "re-running should not duplicate or lose jobs");
    }
}
