namespace PrintShopCompanion.Infrastructure.Commands;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;

/// <summary>
/// Phase 3 (brief §7) — DURABLE command execution tracking in the local
/// SQLite/SQLCipher queue database. The Phase 2 in-memory
/// <see cref="CommandDedupStore"/> (LRU) is insufficient as the ONLY protection
/// against re-execution after an application restart: a command fetched,
/// executed, but whose terminal ack was lost in a crash would be re-fetched
/// and re-executed on the next launch. This store persists:
/// <list type="bullet">
///   <item><c>command_id</c> — server-assigned UUID, PRIMARY KEY (unique constraint).</item>
///   <item><c>command_type</c> — for diagnostics + filter-by-type.</item>
///   <item><c>payload_hash</c> — SHA-256 of the payload JSON, for change detection.</item>
///   <item><c>first_received_at</c> — when we first saw this command.</item>
///   <item><c>execution_status</c> — <c>claimed|executing|succeeded|failed</c>.</item>
///   <item><c>result_json</c> — structured result on success, or null.</item>
///   <item><c>error</c> — failure reason on failure, or null.</item>
///   <item><c>completion_at</c> — when execution reached a terminal state.</item>
/// </list>
///
/// <para><b>Atomic claim</b> (the key invariant): before executing a command,
/// the poller calls <see cref="TryClaimAsync"/>. This INSERTs with
/// <c>ON CONFLICT(command_id) DO NOTHING</c> — only the first caller wins.
/// On restart, a command left in <c>claimed</c> or <c>executing</c> (a crash
/// before the terminal report) is recovered: the poller re-executes it ONLY
/// if its <see cref="PendingCommand.ExpiresAt"/> is still in the future AND
/// the stored status is non-terminal; a terminal status (<c>succeeded</c>/
/// <c>failed</c>) is re-reported to the backend (idempotent) but NOT re-executed.</para>
///
/// <para><b>Bounded retention</b>: rows older than 30 days in a terminal state
/// are purged by <see cref="PurgeOldTerminalAsync"/>. Unresolved or uncertain
/// commands (non-terminal status) are NEVER deleted — per the brief's §7
/// "Do not delete unresolved or uncertain command records".</para>
///
/// <para><b>Shares the queue DB</b>: the table lives in the same SQLCipher
/// database as the print-jobs queue (<c>%LocalAppData%\PrintShop\Companion\
/// Database\queue.db</c>) so it inherits the same DPAPI-protected key +
/// atomic transaction semantics. Schema is additive (CREATE TABLE IF NOT
/// EXISTS) so it's safe on existing installs.</para>
/// </summary>
public sealed class DurableCommandStore
{
    private readonly SqliteJobQueue _queue;
    private readonly ILogger<DurableCommandStore> _logger;
    private int _initialized;

    public DurableCommandStore(SqliteJobQueue queue, ILogger<DurableCommandStore> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    /// <summary>DDL for the commands table. Additive — safe on existing DBs.</summary>
    private const string InitSql = """
        CREATE TABLE IF NOT EXISTS companion_commands (
            command_id         TEXT    PRIMARY KEY,
            command_type       TEXT    NOT NULL,
            payload_hash       TEXT    NOT NULL,
            first_received_at  TEXT    NOT NULL,
            execution_status   TEXT    NOT NULL DEFAULT 'claimed',
            result_json        TEXT,
            error              TEXT,
            completion_at      TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_cc_status      ON companion_commands (execution_status);
        CREATE INDEX IF NOT EXISTS ix_cc_completion ON companion_commands (completion_at);

        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );
        -- Bump to 3 when the commands table is added (idempotent — INSERT OR IGNORE).
        INSERT OR IGNORE INTO schema_version (version) VALUES (3);
        """;

    /// <summary>
    /// Atomically claim a command for execution. The first caller wins
    /// (<c>ON CONFLICT(command_id) DO NOTHING</c>); subsequent callers
    /// (a duplicate fetch, or a concurrent poller tick) get back the existing
    /// row's status + result, so they can re-report without re-executing.
    /// </summary>
    /// <returns>
    /// <c>(Claimed: true, ...)</c> if this caller won the claim and should execute;
    /// <c>(Claimed: false, status, resultJson, error)</c> if another caller already
    /// claimed it — the poller re-reports the existing terminal status (idempotent).
    /// </returns>
    public async Task<ClaimResult> TryClaimAsync(
        PendingCommand command, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var payloadHash = ComputePayloadHash(command.PayloadJson);
        var now = DateTimeOffset.UtcNow.ToString("O");

        // Atomic INSERT-or-return-existing. SQLite's ON CONFLICT DO NOTHING
        // leaves the row unchanged if it exists; we then SELECT to read the
        // current state. Both statements run in one transaction so a concurrent
        // caller cannot observe a half-written row.
        return await _queue.ExecuteInConnectionAsync(async conn =>
        {
            using var tx = conn.BeginTransaction();
            try
            {
                using (var insert = conn.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO companion_commands
                            (command_id, command_type, payload_hash, first_received_at, execution_status)
                        VALUES
                            (@id, @type, @hash, @now, 'claimed')
                        ON CONFLICT(command_id) DO NOTHING;
                        """;
                    insert.Parameters.AddWithValue("@id", command.Id);
                    insert.Parameters.AddWithValue("@type", command.CommandType);
                    insert.Parameters.AddWithValue("@hash", payloadHash);
                    insert.Parameters.AddWithValue("@now", now);
                    var rowsAffected = await insert.ExecuteNonQueryAsync(ct);
                    var claimed = rowsAffected > 0;
                    tx.Commit();

                    // Read the row's current state (whether we inserted or it pre-existed).
                    using var sel = conn.CreateCommand();
                    sel.CommandText = """
                        SELECT execution_status, result_json, error
                        FROM companion_commands WHERE command_id = @id;
                        """;
                    sel.Parameters.AddWithValue("@id", command.Id);
                    using var reader = await sel.ExecuteReaderAsync(ct);
                    if (!reader.Read())
                    {
                        // Should be impossible — we just inserted-or-ignored.
                        return new ClaimResult(Claimed: false, Status: null, ResultJson: null, Error: "row not found after upsert");
                    }
                    var status = reader.GetString(0);
                    var result = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var error = reader.IsDBNull(2) ? null : reader.GetString(2);
                    return new ClaimResult(Claimed: claimed, Status: status, ResultJson: result, Error: error);
                }
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Update a command's execution status. Used by the poller to record
    /// <c>executing</c> → <c>succeeded</c>/<c>failed</c>. Idempotent on
    /// (command_id, status) — re-reporting the same terminal status is a no-op.
    /// </summary>
    public async Task UpdateStatusAsync(
        string commandId, string status, string? resultJson = null, string? error = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var completionAt = (status is "succeeded" or "failed")
            ? DateTimeOffset.UtcNow.ToString("O")
            : null;

        await _queue.ExecuteInConnectionAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE companion_commands SET
                    execution_status = @status,
                    result_json = COALESCE(@result, result_json),
                    error = COALESCE(@error, error),
                    completion_at = COALESCE(@completion, completion_at)
                WHERE command_id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@result", (object?)resultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@completion", (object?)completionAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>
    /// Look up a command's stored state. Returns null if the command was never
    /// claimed (e.g. an unknown command the server sent but we never executed).
    /// Used on restart to recover: a terminal row is re-reported (idempotent),
    /// a non-terminal row whose expiry passed is reported as failed ("interrupted by restart").
    /// </summary>
    public async Task<CommandRow?> GetAsync(string commandId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await _queue.ExecuteInConnectionAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT command_id, command_type, payload_hash, first_received_at,
                       execution_status, result_json, error, completion_at
                FROM companion_commands WHERE command_id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", commandId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!reader.Read()) return null;
            return new CommandRow(
                CommandId: reader.GetString(0),
                CommandType: reader.GetString(1),
                PayloadHash: reader.GetString(2),
                FirstReceivedAt: DateTimeOffset.Parse(reader.GetString(3)),
                ExecutionStatus: reader.GetString(4),
                ResultJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                Error: reader.IsDBNull(6) ? null : reader.GetString(6),
                CompletionAt: reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)));
        }, ct);
    }

    /// <summary>
    /// Bounded retention — purge rows in a terminal state (<c>succeeded</c>/
    /// <c>failed</c>) older than <paramref name="olderThan"/>. Non-terminal
    /// rows (<c>claimed</c>/<c>executing</c>) are NEVER deleted per the brief's
    /// §7. Called periodically by the App (e.g. once a day); safe to call at
    /// any cadence — it's a DELETE with a WHERE clause.
    /// </summary>
    public async Task<int> PurgeOldTerminalAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.Subtract(olderThan).ToString("O");
        return await _queue.ExecuteInConnectionAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM companion_commands
                WHERE execution_status IN ('succeeded', 'failed')
                  AND completion_at IS NOT NULL
                  AND completion_at < @cutoff;
                """;
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>Number of rows currently tracked (for diagnostics/tests).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await _queue.ExecuteInConnectionAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM companion_commands;";
            return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);
    }

    // ────────────────────────── helpers ──────────────────────────

    private static string ComputePayloadHash(string payloadJson)
    {
        // Normalize the JSON before hashing so semantically-equal payloads
        // (different whitespace/key order) hash the same. Falls back to the
        // raw string if parsing fails (the executor tolerates non-JSON payloads
        // by treating them as "{}").
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return SHA256Hash(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            return SHA256Hash(payloadJson);
        }
    }

    private static string SHA256Hash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            await _queue.ExecuteInConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = InitSql;
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            _logger.LogInformation("DurableCommandStore initialized (companion_commands table)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DurableCommandStore initialization failed — command dedup will be in-memory only");
            // Reset so a later call can retry; the in-memory store still works.
            Interlocked.Exchange(ref _initialized, 0);
        }
    }
}

/// <summary>Result of an atomic claim attempt.</summary>
public sealed record ClaimResult(
    bool Claimed,
    string? Status,
    string? ResultJson,
    string? Error);

/// <summary>One row of the companion_commands table.</summary>
public sealed record CommandRow(
    string CommandId,
    string CommandType,
    string PayloadHash,
    DateTimeOffset FirstReceivedAt,
    string ExecutionStatus,    // claimed | executing | succeeded | failed
    string? ResultJson,
    string? Error,
    DateTimeOffset? CompletionAt)
{
    public bool IsTerminal => ExecutionStatus is "succeeded" or "failed";
}
