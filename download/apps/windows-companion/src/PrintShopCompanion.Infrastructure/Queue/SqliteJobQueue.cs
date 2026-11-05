namespace PrintShopCompanion.Infrastructure.Queue;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// SQLite-backed durable job queue.
///
/// <para><b>Idempotency.</b> <see cref="EnqueueAsync"/> inserts with <c>ON CONFLICT (job_id) DO NOTHING</c>;
/// re-enqueuing a job already on disk returns the original record unchanged. This is the
/// "never print twice" guarantee at the queue layer.</para>
///
/// <para><b>Crash safety.</b> Every status transition is wrapped in a transaction. A job that
/// was dequeued but never marked Printed/Failed stays in the <c>Downloaded</c> status;
/// <see cref="ReplayInFlightAsync"/> returns all such jobs to <c>Pending</c> on startup.</para>
///
/// <para><b>Retry.</b> Failed jobs back off exponentially with jitter; <see cref="DequeueAsync"/>
/// will only return a Failed job whose <c>next_attempt_at</c> has passed.</para>
/// </summary>
public sealed class SqliteJobQueue : IJobQueue
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteJobQueue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _initialized;

    private const string InitSql = """
        CREATE TABLE IF NOT EXISTS jobs (
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
            local_pdf_path      TEXT,
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

        CREATE INDEX IF NOT EXISTS ix_jobs_status      ON jobs (status);
        CREATE INDEX IF NOT EXISTS ix_jobs_next_retry  ON jobs (next_attempt_at);

        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );
        INSERT OR IGNORE INTO schema_version (version) VALUES (1);
        """;

    private const string EnqueueSql = """
        INSERT INTO jobs (
            job_id, shop_id, device_id, title, printer_name, copies, page_range,
            paper_size, color, duplex, pdf_url, status, created_at, queued_at, updated_at
        ) VALUES (
            @job_id, @shop_id, @device_id, @title, @printer_name, @copies, @page_range,
            @paper_size, @color, @duplex, @pdf_url, @status, @created_at, @queued_at, @updated_at
        )
        ON CONFLICT(job_id) DO NOTHING;
        """;

    public SqliteJobQueue(IOptions<StorageOptions> storage, ILogger<SqliteJobQueue> logger)
    {
        var path = storage.Value.QueueDbPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrintShop", "Companion", "queue.db");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _logger = logger;
    }

    /// <summary>Open a connection and ensure the schema exists. Called lazily on first use.</summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            await using var c = new SqliteConnection(_connectionString);
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = InitSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<PrintJobRecord> EnqueueAsync(PrintJobRecord job, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = EnqueueSql;
            AddParam(cmd, "@job_id", job.JobId);
            AddParam(cmd, "@shop_id", job.ShopId);
            AddParam(cmd, "@device_id", job.DeviceId);
            AddParam(cmd, "@title", job.Title);
            AddParam(cmd, "@printer_name", (object?)job.PrinterName ?? DBNull.Value);
            AddParam(cmd, "@copies", job.Copies);
            AddParam(cmd, "@page_range", (object?)job.PageRange ?? DBNull.Value);
            AddParam(cmd, "@paper_size", (object?)job.PaperSize ?? DBNull.Value);
            AddParam(cmd, "@color", job.Color.HasValue ? (object)(job.Color.Value ? 1 : 0) : DBNull.Value);
            AddParam(cmd, "@duplex", (object?)job.Duplex ?? DBNull.Value);
            AddParam(cmd, "@pdf_url", job.PdfUrl?.ToString() ?? (object)DBNull.Value);
            AddParam(cmd, "@status", (int)job.Status);
            AddParam(cmd, "@created_at", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@queued_at", job.QueuedAt.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@updated_at", job.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

            var inserted = await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            if (inserted == 0)
            {
                _logger.LogInformation("Duplicate enqueue ignored for job {JobId}", job.JobId);
                var existing = await ReadAsync(conn, job.JobId, ct);
                return existing ?? job;
            }
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PrintJobRecord?> DequeueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Pick: a Pending job, or a (Downloaded/Spooled) in-flight that needs resumption,
            // or a Failed job whose next_attempt_at has passed.
            await using var sel = conn.CreateCommand();
            sel.Transaction = (SqliteTransaction)tx;
            sel.CommandText = """
                SELECT * FROM jobs
                 WHERE status = @pending
                    OR status = @downloaded
                    OR status = @spooled
                    OR (status = @failed AND next_attempt_at IS NOT NULL
                        AND next_attempt_at <= @now)
                 ORDER BY queued_at ASC
                 LIMIT 1;
                """;
            AddParam(sel, "@pending", (int)JobStatus.Pending);
            AddParam(sel, "@downloaded", (int)JobStatus.Downloaded);
            AddParam(sel, "@spooled", (int)JobStatus.Spooled);
            AddParam(sel, "@failed", (int)JobStatus.Failed);
            AddParam(sel, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            await using var reader = await sel.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var job = ReadRecord(reader);
            await reader.CloseAsync();

            // Bump attempt_count if we are picking a Pending job (first attempt of a fresh delivery).
            if (job.Status == JobStatus.Pending)
            {
                job.AttemptCount += 1;
                job.Status = JobStatus.Downloaded;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await WriteStatusAsync(conn, tx, job, ct);
            }
            await tx.CommitAsync(ct);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(PrintJobRecord job, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await WriteStatusAsync(conn, tx, job, ct);
            await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkPrintedAsync(string jobId, DateTimeOffset printedAt, int spoolerJobId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                UPDATE jobs
                   SET status = @printed,
                       printed_at = @printed_at,
                       spooler_job_id = @spooler_job_id,
                       last_error = NULL,
                       updated_at = @updated_at,
                       next_attempt_at = NULL
                 WHERE job_id = @job_id;
                """;
            AddParam(cmd, "@job_id", jobId);
            AddParam(cmd, "@printed", (int)JobStatus.Printed);
            AddParam(cmd, "@printed_at", printedAt.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@spooler_job_id", spoolerJobId);
            AddParam(cmd, "@updated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkReportedPrintedAsync(string jobId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE jobs SET status = @reported, updated_at = @now
                 WHERE job_id = @job_id AND status = @printed;
                """;
            AddParam(cmd, "@reported", (int)JobStatus.ReportedPrinted);
            AddParam(cmd, "@job_id", jobId);
            AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkFailedAsync(string jobId, string error, bool permanent, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Read current attempt count.
            await using var sel = conn.CreateCommand();
            sel.Transaction = (SqliteTransaction)tx;
            sel.CommandText = "SELECT attempt_count FROM jobs WHERE job_id = @job_id;";
            AddParam(sel, "@job_id", jobId);
            var current = Convert.ToInt32(await sel.ExecuteScalarAsync(ct) ?? 0);

            DateTimeOffset? nextAttempt = null;
            int newStatus = (int)JobStatus.Failed;
            if (!permanent)
            {
                var backoff = ComputeBackoff(current);
                nextAttempt = DateTimeOffset.UtcNow + backoff;
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                UPDATE jobs
                   SET status = @status,
                       last_error = @error,
                       next_attempt_at = @next,
                       updated_at = @now
                 WHERE job_id = @job_id;
                """;
            AddParam(cmd, "@status", newStatus);
            AddParam(cmd, "@error", error);
            AddParam(cmd, "@next", (object?)nextAttempt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@job_id", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogWarning("Job {JobId} marked failed (permanent={Permanent}, attempts={Attempts}, nextRetry={Next})",
                jobId, permanent, current + 1, nextAttempt);
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseAsync(string jobId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE jobs
                   SET status = @pending,
                       updated_at = @now,
                       next_attempt_at = NULL
                 WHERE job_id = @job_id
                   AND status IN (@downloaded, @spooled);
                """;
            AddParam(cmd, "@pending", (int)JobStatus.Pending);
            AddParam(cmd, "@downloaded", (int)JobStatus.Downloaded);
            AddParam(cmd, "@spooled", (int)JobStatus.Spooled);
            AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@job_id", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(string jobId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE jobs
                   SET status = @cancelled,
                       updated_at = @now,
                       next_attempt_at = NULL
                 WHERE job_id = @job_id;
                """;
            AddParam(cmd, "@cancelled", (int)JobStatus.Cancelled);
            AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@job_id", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PrintJobRecord>> ListAsync(JobStatus? filterStatus = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (filterStatus is { } s)
        {
            cmd.CommandText = "SELECT * FROM jobs WHERE status = @s ORDER BY queued_at DESC;";
            AddParam(cmd, "@s", (int)s);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM jobs ORDER BY queued_at DESC;";
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PrintJobRecord>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadRecord(reader));
        return list;
    }

    public async Task ReplayInFlightAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Anything that was being processed when the previous process died.
            cmd.CommandText = """
                UPDATE jobs
                   SET status = @pending,
                       updated_at = @now,
                       next_attempt_at = NULL
                 WHERE status IN (@downloaded, @spooled);
                """;
            AddParam(cmd, "@pending", (int)JobStatus.Pending);
            AddParam(cmd, "@downloaded", (int)JobStatus.Downloaded);
            AddParam(cmd, "@spooled", (int)JobStatus.Spooled);
            AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n > 0) _logger.LogInformation("Replayed {Count} in-flight jobs back to Pending after crash", n);
        }
        finally { _gate.Release(); }
    }

    public async Task PurgeTerminalAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM jobs
             WHERE status IN (@printed, @reported, @failed, @cancelled)
               AND updated_at < @cutoff;
            """;
        AddParam(cmd, "@printed", (int)JobStatus.Printed);
        AddParam(cmd, "@reported", (int)JobStatus.ReportedPrinted);
        AddParam(cmd, "@failed", (int)JobStatus.Failed);
        AddParam(cmd, "@cancelled", (int)JobStatus.Cancelled);
        AddParam(cmd, "@cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0) _logger.LogInformation("Purged {Count} terminal jobs older than {Ttl}", n, olderThan);
    }

    // ────────────────────────── helpers ──────────────────────────

    private static TimeSpan ComputeBackoff(int attempt)
    {
        // Exponential backoff with jitter: 5s, 15s, 45s, 2m, 5m, capped at 10m.
        var baseSeconds = Math.Min(Math.Pow(3, attempt) * 5, 600);
        var jitter = Random.Shared.NextDouble() * 0.3 * baseSeconds;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }

    private async Task WriteStatusAsync(SqliteConnection conn, SqliteTransaction tx, PrintJobRecord job, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE jobs SET
                printer_name   = @printer_name,
                copies         = @copies,
                page_range     = @page_range,
                paper_size     = @paper_size,
                color          = @color,
                duplex         = @duplex,
                local_pdf_path = @local_pdf_path,
                pdf_sha256    = @pdf_sha256,
                status         = @status,
                spooler_job_id = @spooler_job_id,
                attempt_count  = @attempts,
                last_error     = @error,
                updated_at     = @now,
                printed_at     = @printed_at,
                next_attempt_at= @next
             WHERE job_id = @job_id;
            """;
        AddParam(cmd, "@job_id", job.JobId);
        AddParam(cmd, "@printer_name", (object?)job.PrinterName ?? DBNull.Value);
        AddParam(cmd, "@copies", job.Copies);
        AddParam(cmd, "@page_range", (object?)job.PageRange ?? DBNull.Value);
        AddParam(cmd, "@paper_size", (object?)job.PaperSize ?? DBNull.Value);
        AddParam(cmd, "@color", job.Color.HasValue ? (object)(job.Color.Value ? 1 : 0) : DBNull.Value);
        AddParam(cmd, "@duplex", (object?)job.Duplex ?? DBNull.Value);
        AddParam(cmd, "@local_pdf_path", (object?)job.LocalPdfPath ?? DBNull.Value);
        AddParam(cmd, "@pdf_sha256", (object?)job.PdfSha256 ?? DBNull.Value);
        AddParam(cmd, "@status", (int)job.Status);
        AddParam(cmd, "@spooler_job_id", (object?)job.SpoolerJobId ?? DBNull.Value);
        AddParam(cmd, "@attempts", job.AttemptCount);
        AddParam(cmd, "@error", (object?)job.LastError ?? DBNull.Value);
        AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AddParam(cmd, "@printed_at", (object?)job.PrintedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        AddParam(cmd, "@next", (object?)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<PrintJobRecord?> ReadAsync(SqliteConnection conn, string jobId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE job_id = @job_id;";
        AddParam(cmd, "@job_id", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    private static PrintJobRecord ReadRecord(SqliteDataReader r)
    {
        bool? color = r["color"] is DBNull ? null : Convert.ToInt32(r["color"]) != 0;
        return new PrintJobRecord
        {
            JobId = (string)r["job_id"],
            ShopId = (string)r["shop_id"],
            DeviceId = (string)r["device_id"],
            Title = (string)r["title"],
            PrinterName = r["printer_name"] is DBNull ? null : (string)r["printer_name"],
            Copies = Convert.ToInt32(r["copies"]),
            PageRange = r["page_range"] is DBNull ? null : (string)r["page_range"],
            PaperSize = r["paper_size"] is DBNull ? null : (string)r["paper_size"],
            Color = color,
            Duplex = r["duplex"] is DBNull ? null : (string)r["duplex"],
            PdfUrl = r["pdf_url"] is DBNull ? null : Uri.TryCreate((string)r["pdf_url"], UriKind.Absolute, out var u) ? u : null,
            LocalPdfPath = r["local_pdf_path"] is DBNull ? null : (string)r["local_pdf_path"],
            PdfSha256 = r["pdf_sha256"] is DBNull ? null : (string)r["pdf_sha256"],
            Status = (JobStatus)Convert.ToInt32(r["status"]),
            SpoolerJobId = r["spooler_job_id"] is DBNull ? null : Convert.ToInt32(r["spooler_job_id"]),
            AttemptCount = Convert.ToInt32(r["attempt_count"]),
            LastError = r["last_error"] is DBNull ? null : (string)r["last_error"],
            CreatedAt = DateTimeOffset.Parse((string)r["created_at"], CultureInfo.InvariantCulture),
            QueuedAt = DateTimeOffset.Parse((string)r["queued_at"], CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse((string)r["updated_at"], CultureInfo.InvariantCulture),
            PrintedAt = r["printed_at"] is DBNull ? null : DateTimeOffset.Parse((string)r["printed_at"], CultureInfo.InvariantCulture),
        };
    }

    private static void AddParam(SqliteCommand cmd, string name, object value)
    {
        cmd.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }
}
