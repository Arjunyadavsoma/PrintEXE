namespace PrintShopCompanion.Infrastructure.Sync;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Storage;

/// <summary>
/// SQLite-backed pending operations queue. Stores operations that need to be
/// pushed to the backend when connectivity returns. Survives crashes.
/// </summary>
public sealed class SqlitePendingOpsQueue : IPendingOpsQueue
{
    private readonly string _connectionString;
    private readonly ILogger<SqlitePendingOpsQueue> _logger;
    private int _initialized;

    public SqlitePendingOpsQueue(
        IOptions<StorageOptions> storage,
        LocalStorageLayout layout,
        QueueKeyStore? keyStore,
        ILogger<SqlitePendingOpsQueue> logger)
    {
        // P0 FIX: Initialize SQLCipher BEFORE checking IsEncryptionAvailable.
        // SqlCipherInitializer.Initialize() is idempotent (Interlocked.CompareExchange)
        // and thread-safe, so it's safe to call from both SqliteJobQueue AND
        // SqlitePendingOpsQueue constructors. Without this call, if the DI
        // container constructs SqlitePendingOpsQueue BEFORE SqliteJobQueue,
        // IsEncryptionAvailable would be false and the queue would throw
        // even though SQLCipher is available.
        SqlCipherInitializer.Initialize(logger);

        // v0.5: use LocalStorageLayout.QueueDbPath (new %LocalAppData% path).
        // The pending ops queue shares the SAME SQLite DB as the job queue
        // (same file, same SQLCipher key, same connection string). They are
        // separate tables within the same database file: `jobs` for the job
        // queue, `pending_ops` for the pending operations queue. This is safe
        // because SQLite supports concurrent access to the same file with
        // Cache=Shared + WAL mode.
        var path = storage.Value.QueueDbPath;
        if (string.IsNullOrWhiteSpace(path))
            path = layout.QueueDbPath;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        // v0.5: SQLCipher encryption — same fail-closed logic as SqliteJobQueue.
        // The pending ops queue MUST use the same encryption key as the job
        // queue because they share the same database file. If the key store
        // is provided, we set the password; if not (tests), we proceed without
        // encryption.
        if (keyStore is not null && SqlCipherInitializer.IsEncryptionAvailable)
        {
            try
            {
                var keyHex = keyStore.GetKeyForConnectionString();
                builder.Password = keyHex;
                logger.LogInformation("SQLCipher encryption enabled for pending_ops in queue.db at {Path}", path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to load the SQLCipher encryption key for the pending ops queue. " +
                    "The queue database cannot be opened without encryption.", ex);
            }
        }
        else if (keyStore is not null && !SqlCipherInitializer.IsEncryptionAvailable)
        {
            // User-facing first (the App's error dialog shows this text); dev
            // detail after the "Technical details" marker.
            throw new InvalidOperationException(
                "PrintEXE cannot open its encrypted job database: the encryption component (SQLCipher) " +
                "is missing or failed to load in this installation. Please reinstall PrintEXE from the " +
                "official release and start it again. If it still fails after reinstalling, send the log " +
                "folder to support. Technical details: the pending-ops queue requires the SQLCipher " +
                "provider (e_sqlcipher), which was not available at startup; see companion.log.");
        }

        _connectionString = builder.ToString();
        _logger = logger;
    }

    private async Task InitAsync()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            // Same pragma set as the job queue (this file IS queue.db — WAL
            // persists at file level; busy_timeout is per-connection).
            await Queue.SqliteConnectionPragmas.ApplyToAsync(conn);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS pending_ops (
                    op_id           TEXT PRIMARY KEY,
                    operation_type  TEXT NOT NULL,
                    target_id       TEXT NOT NULL,
                    payload_json    TEXT NOT NULL DEFAULT '{}',
                    created_at      TEXT NOT NULL,
                    attempts        INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at TEXT,
                    last_error      TEXT,
                    status          TEXT NOT NULL DEFAULT 'pending'
                );
                CREATE INDEX IF NOT EXISTS ix_pending_status ON pending_ops (status);
                CREATE INDEX IF NOT EXISTS ix_pending_next ON pending_ops (next_attempt_at);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public int GetPendingCount()
    {
        InitAsync().GetAwaiter().GetResult();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending_ops WHERE status IN ('pending', 'in_flight', 'retry')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<PendingOp> GetPendingOps(int limit = 10)
    {
        InitAsync().GetAwaiter().GetResult();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM pending_ops
             WHERE status IN ('pending', 'retry')
               AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
             ORDER BY created_at ASC
             LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<PendingOp>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PendingOp
            {
                OpId = (string)reader["op_id"],
                OperationType = (string)reader["operation_type"],
                TargetId = (string)reader["target_id"],
                PayloadJson = reader["payload_json"] is DBNull ? "{}" : (string)reader["payload_json"],
                CreatedAt = DateTimeOffset.Parse((string)reader["created_at"], CultureInfo.InvariantCulture),
                Attempts = Convert.ToInt32(reader["attempts"]),
                NextAttemptAt = reader["next_attempt_at"] is DBNull ? null : DateTimeOffset.Parse((string)reader["next_attempt_at"], CultureInfo.InvariantCulture),
                LastError = reader["last_error"] is DBNull ? null : (string)reader["last_error"],
                Status = (string)reader["status"]
            });
        }
        return list;
    }

    /// <summary>
    /// Every op regardless of status (pending / retry / in_flight / failed),
    /// newest first — the Sync Center page's lists. GetPendingOps deliberately
    /// filters to *dispatchable* rows, which is why the Sync Center previously
    /// showed ZERO failed ops even when failures existed (its UI filtered the
    /// dispatchable result set for status=="failed" — never true).
    /// </summary>
    public IReadOnlyList<PendingOp> GetAllOps(int limit = 100)
    {
        InitAsync().GetAwaiter().GetResult();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM pending_ops
             WHERE status IN ('pending', 'retry', 'in_flight', 'failed')
             ORDER BY created_at DESC
             LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<PendingOp>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PendingOp
            {
                OpId = (string)reader["op_id"],
                OperationType = (string)reader["operation_type"],
                TargetId = (string)reader["target_id"],
                PayloadJson = reader["payload_json"] is DBNull ? "{}" : (string)reader["payload_json"],
                CreatedAt = DateTimeOffset.Parse((string)reader["created_at"], CultureInfo.InvariantCulture),
                Attempts = Convert.ToInt32(reader["attempts"]),
                NextAttemptAt = reader["next_attempt_at"] is DBNull ? null : DateTimeOffset.Parse((string)reader["next_attempt_at"], CultureInfo.InvariantCulture),
                LastError = reader["last_error"] is DBNull ? null : (string)reader["last_error"],
                Status = (string)reader["status"]
            });
        }
        return list;
    }

    public void Enqueue(string operationType, string targetId, object? payload = null)
    {
        InitAsync().GetAwaiter().GetResult();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_ops (op_id, operation_type, target_id, payload_json, created_at, status)
            VALUES (@op_id, @operation_type, @target_id, @payload_json, @created_at, 'pending');
            """;
        cmd.Parameters.AddWithValue("@op_id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@operation_type", operationType);
        cmd.Parameters.AddWithValue("@target_id", targetId);
        cmd.Parameters.AddWithValue("@payload_json", payload is null ? "{}" : JsonSerializer.Serialize(payload));
        cmd.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Queued pending op: {Type} for {Target}", operationType, targetId);
    }

    public void MarkInFlight(string opId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pending_ops SET status = 'in_flight' WHERE op_id = @op_id;";
        cmd.Parameters.AddWithValue("@op_id", opId);
        cmd.ExecuteNonQuery();
    }

    public void MarkCompleted(string opId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pending_ops SET status = 'completed', last_error = NULL WHERE op_id = @op_id;";
        cmd.Parameters.AddWithValue("@op_id", opId);
        cmd.ExecuteNonQuery();
    }

    public void MarkFailed(string opId, string error)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pending_ops SET status = 'failed', last_error = @error WHERE op_id = @op_id;";
        cmd.Parameters.AddWithValue("@op_id", opId);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.ExecuteNonQuery();
    }

    public void MarkRetry(string opId, string error)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Compute backoff: 5s, 15s, 45s, 2m, 5m (capped)
        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT attempts FROM pending_ops WHERE op_id = @op_id;";
        sel.Parameters.AddWithValue("@op_id", opId);
        var attempts = Convert.ToInt32(sel.ExecuteScalar() ?? 0);
        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(3, attempts) * 5, 300));
        var nextAttempt = DateTimeOffset.UtcNow + backoff;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pending_ops
               SET status = 'retry',
                   attempts = attempts + 1,
                   last_error = @error,
                   next_attempt_at = @next
             WHERE op_id = @op_id;
            """;
        cmd.Parameters.AddWithValue("@op_id", opId);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@next", nextAttempt.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
