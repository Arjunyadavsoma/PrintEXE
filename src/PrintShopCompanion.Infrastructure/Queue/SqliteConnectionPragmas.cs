namespace PrintShopCompanion.Infrastructure.Queue;

using Microsoft.Data.Sqlite;

/// <summary>
/// Connection pragmas applied to every hot SQLite connection in the companion.
///
/// <para><b>Why (Phase 0 audit):</b> the queue DB previously ran with the
/// default rollback journal and the default zero busy timeout. A concurrent
/// writer (job queue vs durable command store — same file, Cache=Shared)
/// could surface <c>SQLITE_BUSY</c> immediately instead of waiting, and a
/// crash mid-write could leave a stale journal to roll back. The store-side
/// C# retry (~630ms total) was the only mitigation.</para>
///
/// <para><c>journal_mode=WAL</c> is a persistent per-FILE property — setting
/// it once covers every other connection to the same queue.db (including
/// <see cref="Sync.SqlitePendingOpsQueue"/> and <see cref="Commands.DurableCommandStore"/>).
/// <c>busy_timeout</c> and <c>synchronous</c> are per-connection, so every
/// opener applies them.</para>
///
/// <para><b>Phase 5 fix (fail-visible):</b> the previous implementation caught
/// and suppressed ALL exceptions from pragma execution. A connection whose
/// required pragmas silently failed (e.g. FK enforcement not enabled, busy
/// timeout not applied) was handed to callers as if it were safe — exactly
/// the durability hole this class exists to close. Now every pragma is
/// APPLIED and then VERIFIED with its query form; any failure or mismatch
/// throws, cancellation always propagates, and <see cref="OpenWithPragmasAsync"/>
/// disposes the connection when initialization fails. SQLCipher settings and
/// queue durability are NOT weakened by this change — a connection that
/// cannot be made safe is refused instead of silently degraded.</para>
/// </summary>
internal static class SqliteConnectionPragmas
{
    /// <summary>Milliseconds SQLite waits on a locked write before returning SQLITE_BUSY.</summary>
    public const int BusyTimeoutMs = 5000;

    /// <summary>
    /// How long to keep retrying the WAL switch when another connection is
    /// mid-write. WAL is persistent, so this only matters on the very first
    /// open of a fresh file; a bounded retry absorbs the normal collision,
    /// after which we fail visibly instead of degrading to rollback-journal
    /// mode silently.
    /// </summary>
    private static readonly TimeSpan WalSwitchRetryWindow = TimeSpan.FromSeconds(2);

    /// <summary>SQLite result codes treated as transient contention for the WAL switch.</summary>
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <summary>
    /// Open a connection, apply the standard pragmas, and VERIFY them.
    /// Returns the opened connection; the caller owns disposal. If opening
    /// or pragma initialization fails in any way, the connection is disposed
    /// and the exception propagates — never a silently-degraded connection.
    /// </summary>
    public static async Task<SqliteConnection> OpenWithPragmasAsync(
        string connectionString, System.Threading.CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await ApplyToAsync(conn, ct);
            return conn;
        }
        catch
        {
            // Initialization failed — the connection must never be handed
            // back half-configured. Disposal failure must not mask the
            // original exception.
            try { await conn.DisposeAsync(); }
            catch
            {
                // Swallowing HERE is safe and deliberate: the original
                // exception below is what the caller needs.
            }
            throw;
        }
    }

    /// <summary>
    /// Apply the per-connection pragmas to an ALREADY-open connection and
    /// verify each one. Throws (does not swallow) if any pragma cannot be
    /// applied or does not verify. Cancellation propagates.
    /// </summary>
    public static async Task ApplyToAsync(SqliteConnection conn, System.Threading.CancellationToken ct = default)
    {
        // journal_mode=WAL: readers don't block the writer; the write lock is
        // held only for the duration of a transaction. Persistent per-file.
        await ApplyAndVerifyAsync(conn, ct,
            set: "PRAGMA journal_mode=WAL;",
            verify: "PRAGMA journal_mode;",
            expected: r => string.Equals(r as string, "wal", System.StringComparison.OrdinalIgnoreCase),
            description: "journal_mode=WAL",
            retryOnBusy: true);

        // NORMAL in WAL mode is the recommended durable-but-fast setting:
        // consistent across power loss, only risks the last checkpoint.
        await ApplyAndVerifyAsync(conn, ct,
            set: "PRAGMA synchronous=NORMAL;",
            verify: "PRAGMA synchronous;",
            expected: r => Convert.ToInt64(r!) == 1, // SQLite: 0=OFF, 1=NORMAL, 2=FULL
            description: "synchronous=NORMAL");

        // Wait up to 5s on a locked DB before surfacing SQLITE_BUSY — absorbs
        // the writer collisions between the job queue and command store.
        await ApplyAndVerifyAsync(conn, ct,
            set: $"PRAGMA busy_timeout={BusyTimeoutMs};",
            verify: "PRAGMA busy_timeout;",
            expected: r => Convert.ToInt64(r!) == BusyTimeoutMs,
            description: $"busy_timeout={BusyTimeoutMs}");

        // Enforce declared FK constraints (queue tables reference jobs).
        await ApplyAndVerifyAsync(conn, ct,
            set: "PRAGMA foreign_keys=ON;",
            verify: "PRAGMA foreign_keys;",
            expected: r => Convert.ToInt64(r!) == 1,
            description: "foreign_keys=ON");
    }

    private static async Task ApplyAndVerifyAsync(
        SqliteConnection conn,
        System.Threading.CancellationToken ct,
        string set,
        string verify,
        Func<object?, bool> expected,
        string description,
        bool retryOnBusy = false)
    {
        var deadline = retryOnBusy
            ? System.DateTime.UtcNow + WalSwitchRetryWindow
            : System.DateTime.MinValue;

        while (true)
        {
            object? result;
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = set;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = verify;
                    result = await cmd.ExecuteScalarAsync(ct);
                }
            }
            catch (SqliteException ex)
                when (retryOnBusy
                    && (ex.SqliteErrorCode == SqliteBusy || ex.SqliteErrorCode == SqliteLocked)
                    && System.DateTime.UtcNow < deadline)
            {
                // Another connection holds the file — bounded retry for the
                // persistent WAL switch only. Cancellation inside the delay
                // propagates (Task.Delay honors ct).
                await System.Threading.Tasks.Task.Delay(50, ct);
                continue;
            }

            if (!expected(result))
            {
                throw new System.InvalidOperationException(
                    $"SQLite pragma '{description}' could not be applied/verified on this connection " +
                    $"(verify '{verify}' returned '{result?.ToString() ?? "null"}'). " +
                    "Refusing to hand back a connection that is not safe to use. " +
                    "If this persists, the queue database may be locked by another process or corrupt.");
            }
            return;
        }
    }
}
