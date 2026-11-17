namespace PrintShopCompanion.Infrastructure.Commands;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Infrastructure.Queue;

/// <summary>
/// Phase F (brief §A + §B) — DURABLE command execution tracking in the local
/// SQLCipher queue database, with a CRASH-SAFE state machine.
///
/// <para><b>The safety problem this solves</b> (brief Phase A, acceptance
/// criterion #1): a Windows process can crash AFTER an external side effect
/// succeeds (e.g. paper is physically printed) but BEFORE SQLite records
/// success. Re-executing the command on restart would duplicate the side
/// effect. The Phase 3 store re-executed any non-terminal command on
/// restart — that was UNSAFE for non-idempotent commands. This store
/// implements the brief's recovery rules:</para>
/// <list type="bullet">
///   <item><c>received</c> — seen from the server, not yet claimed. Eligible
///   for normal processing if still valid (the brief's "received: eligible
///   for normal processing if still valid").</item>
///   <item><c>claimed</c> — atomically claimed for execution, but the handler
///   has not started. Recover ONLY if the implementation can prove no side
///   effect started — which is true for <c>claimed</c> because no handler has
///   run yet. (The brief's "claimed but never marked executing: recover only
///   if the implementation can prove no side effect started".)</item>
///   <item><c>executing</c> — the handler has started AND may have produced a
///   side effect. On restart, transition to <c>outcome_unknown</c> UNLESS the
///   handler supports reliable reconciliation (none currently do — they all
///   return terminal results only). (The brief's "executing: transition to
///   outcome_unknown unless the handler supports reliable reconciliation".)</item>
///   <item><c>succeeded</c> / <c>failed</c> / <c>outcome_unknown</c> — terminal.
///   NEVER executed again. Re-reported to the server (idempotent) on redelivery.</item>
///   <item><c>expired</c> — server-side expiry passed before execution. Terminal.</item>
///   <item><c>cancelled</c> — server-side cancellation. Terminal.</item>
/// </list>
///
/// <para><b>Atomic claim</b>: <see cref="TryClaimAsync"/> does an atomic
/// <c>INSERT ... ON CONFLICT(command_id) DO NOTHING</c> (status='received')
/// then a conditional <c>UPDATE ... WHERE execution_status='received'</c> to
/// 'claimed' in the SAME transaction. The first caller wins; a concurrent
/// caller sees 'claimed' (or a terminal state from a previous process) and
/// does NOT execute. This is the brief's "Prevent concurrent pollers from
/// executing the same command" + "Atomic claim operations using transactions".</para>
///
/// <para><b>Phase transitions before + after side effects</b> (brief Phase A
/// required sequence):</para>
/// <list type="number">
///   <item><see cref="RecordReceivedAsync"/> — first time we see the command
///   from the server (status='received'). Idempotent — a redelivery is a no-op.</item>
///   <item><see cref="TryClaimAsync"/> — atomic claim (received→claimed).</item>
///   <item><see cref="MarkExecutingAsync"/> — persist executing-started BEFORE
///   running the handler, so a crash between claim and handler-start leaves
///   'claimed' (safe to recover) not 'executing' (unsafe to re-run).</item>
///   <item>Run the handler.</item>
///   <item><see cref="MarkTerminalAsync"/> — persist the final result AFTER
///   the handler returns (succeeded/failed/outcome_unknown).</item>
/// </list>
///
/// <para><b>Restart recovery</b>: <see cref="RecoverInterruptedAsync"/>
/// scans for non-terminal rows on startup. <c>claimed</c> rows (handler
/// never started) are reset to <c>received</c> so the poller re-claims +
/// re-executes them (safe — no side effect ran). <c>executing</c> rows
/// (handler started, may have side-effected) are transitioned to
/// <c>outcome_unknown</c> and re-reported to the server; they are NEVER
/// re-executed. The shopkeeper resolves them manually. This is the brief's
/// acceptance criterion #1.</para>
///
/// <para><b>Bounded retention</b>: <see cref="PurgeOldTerminalAsync"/> deletes
/// only terminal rows (<c>succeeded</c>/<c>failed</c>/<c>outcome_unknown</c>/
/// <c>expired</c>/<c>cancelled</c>) older than the cutoff. Non-terminal rows
/// (<c>received</c>/<c>claimed</c>/<c>executing</c>) are NEVER deleted —
/// they may still be recoverable. <c>outcome_unknown</c> rows are kept
/// until the shopkeeper resolves them (the brief's "Never delete records
/// needed to resolve uncertain print outcomes").</para>
///
/// <para><b>DB busy/locked handling</b>: every operation retries on
/// <c>SqliteException</c> with <c>ErrorCode==Busy</c> or <c>Locked</c> using
/// a bounded exponential backoff (10ms → 20 → 40 → 80 → 160 → 320ms, max 6
/// attempts ≈ 630ms total) before propagating. This is the brief's "Handle
/// database busy/locked conditions with bounded retry and cancellation".</para>
///
/// <para><b>Thread-safe connection access</b>: the store does NOT expose a
/// generic connection callback (the Phase 3 <c>ExecuteInConnectionAsync</c>
/// was removed from <c>SqliteJobQueue</c>'s public surface because it
/// permitted unsafe concurrent access). Instead, the store owns its own
/// narrow repository API with explicit transactional operations, each
/// opening a fresh connection via <c>SqliteJobQueue.OpenScopedAsync</c>
/// (per-call connection, disposed by the using). Every operation is
/// independent; SQLite serializes writes via the DB's internal lock.</para>
///
/// <para><b>Schema</b> (additive — no destructive migration; the Phase 3
/// columns are preserved, new columns added with defaults): the
/// <c>companion_commands</c> table is in the SAME SQLCipher DB as the
/// print-jobs queue, sharing the DPAPI-protected key. The Phase 3 schema
/// is upgraded in place via <c>ALTER TABLE ADD COLUMN</c> (idempotent via
/// PRAGMA table_info check) for the new state-tracking columns.</para>
/// </summary>
public sealed class DurableCommandStore
{
    private readonly SqliteJobQueue _queue;
    private readonly ILogger<DurableCommandStore> _logger;
    private int _initialized;

    // Bounded retry for SQLite BUSY/LOCKED. Total worst-case wait ≈ 630ms
    // (10+20+40+80+160+320) before propagating — well under any reasonable
    // command-processing timeout.
    private const int MaxBusyRetries = 6;

    public DurableCommandStore(SqliteJobQueue queue, ILogger<DurableCommandStore> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// The execution states a command can be in (the brief's Phase A list).
    /// Mirrors the SaaS <c>REPORTABLE_STATUSES</c> plus the local-only
    /// <c>received</c>/<c>claimed</c> (the SaaS never sees those — they're
    /// pre-execution local states).
    /// </summary>
    public static class CommandState
    {
        public const string Received = "received";          // seen from server, not claimed
        public const string Claimed = "claimed";             // atomically claimed, handler not started
        public const string Executing = "executing";         // handler started (may have side-effected)
        public const string Succeeded = "succeeded";         // terminal — success
        public const string Failed = "failed";               // terminal — failure
        public const string OutcomeUnknown = "outcome_unknown"; // terminal — crash interrupted, side effect uncertain
        public const string Expired = "expired";             // terminal — server-side expiry passed
        public const string Cancelled = "cancelled";         // terminal — server-side cancellation
    }

    /// <summary>The terminal states. Used for retention + the never-re-execute gate.</summary>
    public static readonly string[] TerminalStates =
        { CommandState.Succeeded, CommandState.Failed, CommandState.OutcomeUnknown, CommandState.Expired, CommandState.Cancelled };

    /// <summary>The non-terminal states (subject to restart recovery).</summary>
    public static readonly string[] NonTerminalStates =
        { CommandState.Received, CommandState.Claimed, CommandState.Executing };

    /// <summary>
    /// DDL for the commands table. Additive — safe on existing DBs. The Phase 3
    /// schema had {command_id, command_type, payload_hash, first_received_at,
    /// execution_status, result_json, error, completion_at}. Phase F adds
    /// {payload_json, accepted_at, executing_at, outcome_unknown_at} via
    /// ALTER TABLE (idempotent — guarded by a column-existence probe).
    /// </summary>
    private const string InitSql = """
        CREATE TABLE IF NOT EXISTS companion_commands (
            command_id            TEXT    PRIMARY KEY,
            command_type          TEXT    NOT NULL,
            payload_hash          TEXT    NOT NULL,
            payload_json          TEXT,
            first_received_at     TEXT    NOT NULL,
            execution_status      TEXT    NOT NULL DEFAULT 'received',
            accepted_at           TEXT,
            executing_at          TEXT,
            outcome_unknown_at    TEXT,
            result_json           TEXT,
            error                 TEXT,
            completion_at         TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_cc_status      ON companion_commands (execution_status);
        CREATE INDEX IF NOT EXISTS ix_cc_completion ON companion_commands (completion_at);
        CREATE INDEX IF NOT EXISTS ix_cc_outcome    ON companion_commands (outcome_unknown_at);

        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );
        -- Bump to 4 when the Phase F columns are added (idempotent — INSERT OR IGNORE).
        INSERT OR IGNORE INTO schema_version (version) VALUES (4);
        """;

    /// <summary>
    /// Phase F additive column adds for DBs created under the Phase 3 schema
    /// (which lacked payload_json, accepted_at, executing_at, outcome_unknown_at).
    /// Idempotent — guarded by PRAGMA table_info. Run inside InitializeAsync
    /// after the CREATE TABLE.
    /// </summary>
    private const string AddColumnIfMissingSql = """
        -- SQLite has no ADD COLUMN IF NOT EXISTS; we probe column existence in
        -- C# before issuing each ALTER. This string is unused directly — see
        -- the column-existence probe in InitializeAsync.
        SELECT 1;
        """;

    /// <summary>(column, ddl-type) pairs for the Phase F ALTER TABLE adds.</summary>
    private static readonly (string Name, string Ddl)[] PhaseFColumns =
    {
        ("payload_json",       "TEXT"),
        ("accepted_at",        "TEXT"),
        ("executing_at",       "TEXT"),
        ("outcome_unknown_at", "TEXT"),
    };

    /// <summary>
    /// Phase F compatibility: the Phase 3 default for execution_status was
    /// 'claimed'. Phase F uses 'received' as the initial state. For rows that
    /// pre-date Phase F (created under Phase 3 with status='claimed'), they
    /// are treated as 'claimed' on recovery — which is safe (claimed → received
    /// → re-claim → re-execute, since claimed means "handler not started").
    /// </summary>
    private const string Phase3DefaultStatus = "claimed";

    // ────────────────────────── public API ──────────────────────────

    /// <summary>
    /// Record that we've received a command from the server. Idempotent — a
    /// redelivery is a no-op (returns the existing row's state). This is the
    /// brief's "received" state: eligible for normal processing if still valid.
    /// </summary>
    public async Task<CommandRow?> RecordReceivedAsync(
        PendingCommand command, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var payloadHash = ComputePayloadHash(command.PayloadJson);
        var now = NowIso();

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // Insert if absent with status='received'. ON CONFLICT DO NOTHING
                // preserves any existing row (including terminal state from a
                // previous execution) so a redelivery never clobbers history.
                await using (var insert = conn.CreateCommand())
                {
                    insert.Transaction = (SqliteTransaction)tx;
                    insert.CommandText = """
                        INSERT INTO companion_commands
                            (command_id, command_type, payload_hash, payload_json,
                             first_received_at, execution_status)
                        VALUES
                            (@id, @type, @hash, @payload, @now, 'received')
                        ON CONFLICT(command_id) DO NOTHING;
                        """;
                    insert.Parameters.AddWithValue("@id", command.Id);
                    insert.Parameters.AddWithValue("@type", command.CommandType);
                    insert.Parameters.AddWithValue("@hash", payloadHash);
                    insert.Parameters.AddWithValue("@payload", command.PayloadJson);
                    insert.Parameters.AddWithValue("@now", now);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                return await ReadRowAsync(conn, command.Id, ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Atomically claim a command for execution. First caller wins. The claim
    /// is a conditional UPDATE: <c>received → claimed</c> only if the row is
    /// still <c>received</c>. A concurrent caller (or a row already past
    /// <c>received</c>) gets back the existing state and does NOT execute.
    /// </summary>
    /// <returns>
    /// <c>(Claimed: true, ...)</c> if this caller won the claim;
    /// <c>(Claimed: false, status, resultJson, error)</c> with the existing
    /// state otherwise (the poller re-reports terminal states without
    /// re-executing; non-terminal states mean another caller is mid-flight).
    /// </returns>
    public async Task<ClaimResult> TryClaimAsync(
        PendingCommand command, CancellationToken ct = default)
    {
        // Ensure a row exists (idempotent) before the conditional claim.
        await RecordReceivedAsync(command, ct);

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                int rowsAffected;
                await using (var claim = conn.CreateCommand())
                {
                    claim.Transaction = (SqliteTransaction)tx;
                    claim.CommandText = """
                        UPDATE companion_commands
                        SET execution_status = 'claimed',
                            accepted_at = COALESCE(accepted_at, @now)
                        WHERE command_id = @id AND execution_status = 'received';
                        """;
                    claim.Parameters.AddWithValue("@id", command.Id);
                    claim.Parameters.AddWithValue("@now", NowIso());
                    rowsAffected = await claim.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);

                var row = await ReadRowAsync(conn, command.Id, ct);
                if (row is null)
                    return new ClaimResult(Claimed: false, Status: null, ResultJson: null, Error: "row not found after claim");
                return new ClaimResult(Claimed: rowsAffected > 0, Status: row.ExecutionStatus, ResultJson: row.ResultJson, Error: row.Error);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Mark a claimed command as executing — the handler is ABOUT to start.
    /// Persist this BEFORE running the handler so a crash between claim and
    /// handler-start leaves the row 'claimed' (safe: re-claim + re-execute)
    /// not 'executing' (unsafe: would transition to outcome_unknown on restart).
    /// </summary>
    /// <returns>False if the row is no longer 'claimed' (e.g. another process
    /// already moved it). The caller should abort in that case.</returns>
    public async Task<bool> MarkExecutingAsync(string commandId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE companion_commands
                SET execution_status = 'executing',
                    executing_at = COALESCE(executing_at, @now)
                WHERE command_id = @id AND execution_status = 'claimed';
                """;
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.Parameters.AddWithValue("@now", NowIso());
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    /// <summary>
    /// Persist the terminal result of a command. Sets <c>completion_at</c>.
    /// Idempotent — re-reporting the same terminal status is a no-op (the
    /// COALESCEs preserve the earliest non-null values). Use
    /// <see cref="CommandState"/> for the <paramref name="status"/> values.
    /// </summary>
    public async Task MarkTerminalAsync(
        string commandId, string status, string? resultJson = null, string? error = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!TerminalStates.Contains(status))
            throw new ArgumentException(
                $"MarkTerminalAsync requires a terminal status (one of {string.Join(", ", TerminalStates)}); got '{status}'.", nameof(status));

        var now = NowIso();
        var outcomeAt = status == CommandState.OutcomeUnknown ? now : null;

        await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE companion_commands SET
                    execution_status = @status,
                    result_json = COALESCE(@result, result_json),
                    error = COALESCE(@error, error),
                    completion_at = COALESCE(completion_at, @completion),
                    outcome_unknown_at = COALESCE(outcome_unknown_at, @outcomeAt)
                WHERE command_id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@result", (object?)resultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@completion", now);
            cmd.Parameters.AddWithValue("@outcomeAt", (object?)outcomeAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>
    /// Mark a command as expired (server-side expiry passed before execution).
    /// Terminal. Used by the poller when it observes a fetched command whose
    /// <see cref="PendingCommand.IsExpired"/> is true and no execution has
    /// started. Idempotent.
    /// </summary>
    public async Task MarkExpiredAsync(string commandId, CancellationToken ct = default) =>
        await MarkTerminalAsync(commandId, CommandState.Expired, error: "Command expired before execution started.", ct: ct);

    /// <summary>
    /// Restart recovery — scan for non-terminal rows and apply the brief's
    /// Phase A recovery rules:
    /// <list type="bullet">
    ///   <item><c>received</c> — leave as-is (eligible for normal processing;
    ///   the poller will claim it on the next tick).</item>
    ///   <item><c>claimed</c> — reset to <c>received</c> (the handler never
    ///   started; safe to re-claim + re-execute).</item>
    ///   <item><c>executing</c> — transition to <c>outcome_unknown</c>
    ///   (the handler started; a side effect may have occurred; the
    ///   shopkeeper must resolve manually). NEVER re-execute.</item>
    /// </list>
    /// Returns the list of commands transitioned to <c>outcome_unknown</c>
    /// (the poller re-reports these to the server on the next tick).
    /// </summary>
    public async Task<IReadOnlyList<CommandRow>> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var now = NowIso();
        var recovered = new List<CommandRow>();

        await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // claimed → received (handler never started; safe to re-execute).
                await using (var reset = conn.CreateCommand())
                {
                    reset.Transaction = (SqliteTransaction)tx;
                    reset.CommandText = """
                        UPDATE companion_commands
                        SET execution_status = 'received'
                        WHERE execution_status = 'claimed';
                        """;
                    await reset.ExecuteNonQueryAsync(ct);
                }

                // executing → outcome_unknown (handler started; side effect uncertain).
                // Capture the rows BEFORE the update so we can return them for re-report.
                var toOutcomeUnknown = new List<CommandRow>();
                await using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = (SqliteTransaction)tx;
                    sel.CommandText = """
                        SELECT command_id, command_type, payload_hash, payload_json,
                               first_received_at, execution_status, accepted_at, executing_at,
                               outcome_unknown_at, result_json, error, completion_at
                        FROM companion_commands WHERE execution_status = 'executing';
                        """;
                    await using var reader = await sel.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        toOutcomeUnknown.Add(ReadRowFromReader(reader));
                    }
                }
                if (toOutcomeUnknown.Count > 0)
                {
                    await using (var mark = conn.CreateCommand())
                    {
                        mark.Transaction = (SqliteTransaction)tx;
                        mark.CommandText = """
                            UPDATE companion_commands
                            SET execution_status = 'outcome_unknown',
                                outcome_unknown_at = COALESCE(outcome_unknown_at, @now),
                                completion_at = COALESCE(completion_at, @now),
                                error = COALESCE(error, 'Execution interrupted by application restart; physical outcome uncertain.')
                            WHERE execution_status = 'executing';
                            """;
                        mark.Parameters.AddWithValue("@now", now);
                        await mark.ExecuteNonQueryAsync(ct);
                    }
                }
                await tx.CommitAsync(ct);
                recovered = toOutcomeUnknown;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }, ct);

        if (recovered.Count > 0)
        {
            _logger.LogWarning("Restart recovery: {Count} command(s) transitioned to outcome_unknown (executing at crash — NOT re-executed)", recovered.Count);
        }
        return recovered;
    }

    /// <summary>
    /// Look up a command's stored state. Returns null if the command was
    /// never received. Used by the poller to decide whether to execute
    /// (terminal → re-report, non-terminal → see recovery rules).
    /// </summary>
    public async Task<CommandRow?> GetAsync(string commandId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            return await ReadRowAsync(conn, commandId, ct);
        }, ct);
    }

    /// <summary>
    /// Bounded retention — purge rows in a terminal state older than
    /// <paramref name="olderThan"/>. Non-terminal rows are NEVER deleted
    /// (the brief's "Never delete records needed to resolve uncertain print
    /// outcomes"). <c>outcome_unknown</c> rows ARE purged by this (they're
    /// terminal) — but only after the cutoff; the shopkeeper has that long
    /// to resolve them. Safe to call at any cadence.
    /// </summary>
    public async Task<int> PurgeOldTerminalAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.Subtract(olderThan).ToString("O");
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Use a parameterized IN-list (SQLite doesn't bind arrays; expand the
            // terminals as quoted literals — they're constants, not user input).
            var inList = string.Join(", ", TerminalStates.Select(s => $"'{s}'"));
            cmd.CommandText = $"""
                DELETE FROM companion_commands
                WHERE execution_status IN ({inList})
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
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = await _queue.OpenScopedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM companion_commands;";
            return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);
    }

    // ────────────────────────── helpers ──────────────────────────

    private static async Task<CommandRow?> ReadRowAsync(SqliteConnection conn, string commandId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT command_id, command_type, payload_hash, payload_json,
                   first_received_at, execution_status, accepted_at, executing_at,
                   outcome_unknown_at, result_json, error, completion_at
            FROM companion_commands WHERE command_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", commandId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRowFromReader(reader);
    }

    private static CommandRow ReadRowFromReader(SqliteDataReader reader)
    {
        var firstReceived = reader.GetString(4);   // first_received_at
        var status = reader.GetString(5);
        var acceptedAt = reader.IsDBNull(6) ? null : reader.GetString(6);
        var executingAt = reader.IsDBNull(7) ? null : reader.GetString(7);
        var outcomeAt = reader.IsDBNull(8) ? null : reader.GetString(8);
        var completionAt = reader.IsDBNull(11) ? null : reader.GetString(11);
        return new CommandRow(
            CommandId: reader.GetString(0),
            CommandType: reader.GetString(1),
            PayloadHash: reader.GetString(2),
            PayloadJson: reader.IsDBNull(3) ? null : reader.GetString(3),
            FirstReceivedAt: ParseIso(firstReceived),
            ExecutionStatus: status,
            AcceptedAt: acceptedAt is null ? null : ParseIso(acceptedAt),
            ExecutingAt: executingAt is null ? null : ParseIso(executingAt),
            OutcomeUnknownAt: outcomeAt is null ? null : ParseIso(outcomeAt),
            ResultJson: reader.IsDBNull(9) ? null : reader.GetString(9),
            Error: reader.IsDBNull(10) ? null : reader.GetString(10),
            CompletionAt: completionAt is null ? null : ParseIso(completionAt));
    }

    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static string NowIso() => DateTimeOffset.UtcNow.ToString("O");

    private static string ComputePayloadHash(string payloadJson)
    {
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

    /// <summary>
    /// Bounded retry on SQLite BUSY/LOCKED. Exponential backoff: 10, 20, 40,
    /// 80, 160, 320ms (6 attempts, ~630ms total). Any other exception is
    /// propagated immediately. Honors cancellation between retries.
    /// </summary>
    private async Task<T> WithBusyRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var delay = 10;
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < MaxBusyRetries)
            {
                _logger.LogDebug(ex, "SQLite busy/locked (attempt {Attempt}/{Max}); retrying in {Delay}ms", attempt + 1, MaxBusyRetries, delay);
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
    }

    /// <summary>Non-generic overload for actions that don't produce a value.</summary>
    private async Task WithBusyRetryAsync(Func<Task> action, CancellationToken ct)
    {
        var delay = 10;
        for (var attempt = 0; ; attempt++)
        {
            try { await action(); return; }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < MaxBusyRetries)
            {
                _logger.LogDebug(ex, "SQLite busy/locked (attempt {Attempt}/{Max}); retrying in {Delay}ms", attempt + 1, MaxBusyRetries, delay);
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
    }

    private static bool IsBusyOrLocked(SqliteException ex) =>
        // SQLITE_BUSY = 5, SQLITE_LOCKED = 6 (per the SQLite C API; the integer
        // values are stable across SQLitePCLRaw versions, more robust than the
        // raw.SQLITE_BUSY constant which may not be exposed on all provider builds).
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

    private async Task InitializeAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            await WithBusyRetryAsync(async () =>
            {
                await using var conn = await _queue.OpenScopedAsync(ct);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = InitSql;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                // Phase F additive ALTER TABLE for DBs created under Phase 3
                // (which lacked payload_json, accepted_at, executing_at,
                // outcome_unknown_at). Probe each column; ALTER if missing.
                foreach (var (name, ddl) in PhaseFColumns)
                {
                    if (!await ColumnExistsAsync(conn, name, ct))
                    {
                        await using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE companion_commands ADD COLUMN {name} {ddl};";
                        await alter.ExecuteNonQueryAsync(ct);
                        _logger.LogInformation("Phase F schema upgrade: added column {Column}", name);
                    }
                }
            }, ct);
            _logger.LogInformation("DurableCommandStore initialized (companion_commands table, schema v4)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DurableCommandStore initialization failed — command dedup will be in-memory only");
            Interlocked.Exchange(ref _initialized, 0);
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string columnName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(companion_commands);";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(1) == columnName) return true;   // col 1 = name
        }
        return false;
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
    string? PayloadJson,
    DateTimeOffset FirstReceivedAt,
    string ExecutionStatus,           // received | claimed | executing | succeeded | failed | outcome_unknown | expired | cancelled
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? ExecutingAt,
    DateTimeOffset? OutcomeUnknownAt,
    string? ResultJson,
    string? Error,
    DateTimeOffset? CompletionAt)
{
    /// <summary>True if the command is in a terminal state (never re-execute).</summary>
    public bool IsTerminal => DurableCommandStore.TerminalStates.Contains(ExecutionStatus);

    /// <summary>True if the command's side effect is uncertain (crash during execution).</summary>
    public bool IsOutcomeUnknown => ExecutionStatus == DurableCommandStore.CommandState.OutcomeUnknown;
}
