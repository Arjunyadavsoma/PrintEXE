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
/// </summary>
internal static class SqliteConnectionPragmas
{
    /// <summary>Milliseconds SQLite waits on a locked write before returning SQLITE_BUSY.</summary>
    public const int BusyTimeoutMs = 5000;

    /// <summary>
    /// Open a connection and apply the standard pragmas. Returns the opened
    /// connection; the caller owns disposal.
    /// </summary>
    public static async Task<SqliteConnection> OpenWithPragmasAsync(
        string connectionString, System.Threading.CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);

        // journal_mode=WAL: readers don't block the writer; the write lock is
        // held only for the duration of a transaction. Persistent per-file.
        await ExecutePragmaAsync(conn, "PRAGMA journal_mode=WAL;", ct);
        // NORMAL in WAL mode is the recommended durable-but-fast setting:
        // consistent across power loss, only risks the last checkpoint.
        await ExecutePragmaAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
        // Wait up to 5s on a locked DB before surfacing SQLITE_BUSY — absorbs
        // the writer collisions between the job queue and command store.
        await ExecutePragmaAsync(conn, $"PRAGMA busy_timeout={BusyTimeoutMs};", ct);
        // Enforce declared FK constraints (queue tables reference jobs).
        await ExecutePragmaAsync(conn, "PRAGMA foreign_keys=ON;", ct);
        return conn;
    }

    /// <summary>Apply the per-connection pragmas to an ALREADY-open connection.</summary>
    public static async Task ApplyToAsync(SqliteConnection conn, System.Threading.CancellationToken ct = default)
    {
        await ExecutePragmaAsync(conn, "PRAGMA journal_mode=WAL;", ct);
        await ExecutePragmaAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
        await ExecutePragmaAsync(conn, $"PRAGMA busy_timeout={BusyTimeoutMs};", ct);
        await ExecutePragmaAsync(conn, "PRAGMA foreign_keys=ON;", ct);
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection conn, string pragma, System.Threading.CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = pragma;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // journal_mode can legitimately fail while another connection
            // holds the DB (returns the current mode instead of switching).
            // busy_timeout/foreign_keys are safe — a failure here means the
            // connection is unusable anyway and the caller will see it.
        }
    }
}
