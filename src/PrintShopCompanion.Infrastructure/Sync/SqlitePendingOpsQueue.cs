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

    public SqlitePendingOpsQueue(IOptions<StorageOptions> storage, LocalStorageLayout layout, ILogger<SqlitePendingOpsQueue> logger)
    {
        // v0.5: use LocalStorageLayout.QueueDbPath (new %LocalAppData% path).
        // The pending ops queue shares the same SQLite DB as the job queue
        // (they use the same connection string + the same SQLCipher key).
        // Fall back to appsettings override or the old %AppData% path.
        var path = storage.Value.QueueDbPath;
        if (string.IsNullOrWhiteSpace(path))
            path = layout.QueueDbPath;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _logger = logger;
    }

    private async Task InitAsync()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
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
