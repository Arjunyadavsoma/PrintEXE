namespace PrintShopCompanion.Infrastructure.Tests;

using Microsoft.Data.Sqlite;
using PrintShopCompanion.Infrastructure.Queue;
using Xunit;

/// <summary>
/// Phase 5 — SqliteConnectionPragmas fail-visible regression tests.
///
/// <para>The previous implementation caught and suppressed ALL exceptions
/// from pragma execution: a connection whose foreign_keys or busy_timeout
/// could not be applied was handed to callers as if safe. These tests pin
/// the repaired contract:</para>
///
/// <list type="bullet">
/// <item>every required pragma is applied AND verified (query form),</item>
/// <item>a failed pragma throws visibly (never swallowed),</item>
/// <item>a WAL switch blocked by a concurrent writer fails visibly after a
/// bounded retry instead of silently degrading to rollback-journal mode,</item>
/// <item>cancellation propagates (including out of the retry loop),</item>
/// <item><see cref="SqliteConnectionPragmas.OpenWithPragmasAsync"/> never
/// returns a connection it could not fully initialize (dispose-on-failure),
/// and does not swallow open failures,</item>
/// <item>SQLCipher-encrypted queue DBs cannot be opened as plaintext SQLite
/// (Windows-only — encryption requires the DPAPI-backed key store).</item>
/// </list>
///
/// <para>SQLCipher strength and queue durability are NOT weakened: these
/// tests demand MORE (visible failure) than the old silent suppression.</para>
/// </summary>
public class SqliteConnectionPragmasSafetyTests
{
    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "p4pragmas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<long> QueryIntAsync(SqliteConnection conn, string pragma)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result!);
    }

    // ── 1. happy path: every pragma applied and verified ────────────────
    [Fact]
    public async Task OpenWithPragmas_Verifies_All_Required_Pragmas()
    {
        var root = NewTempDir();
        var conn = await SqliteConnectionPragmas.OpenWithPragmasAsync($"Data Source={Path.Combine(root, "queue.db")}");
        await using (conn)
        {
            Assert.Equal(1, await QueryIntAsync(conn, "PRAGMA foreign_keys;"));
            Assert.Equal(SqliteConnectionPragmas.BusyTimeoutMs, await QueryIntAsync(conn, "PRAGMA busy_timeout;"));
            Assert.Equal(1, await QueryIntAsync(conn, "PRAGMA synchronous;")); // NORMAL
            await using var mode = conn.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (await mode.ExecuteScalarAsync())!.ToString(), ignoreCase: true);
        }
    }

    // ── 2. failed pragma is VISIBLE (old code swallowed everything) ─────
    [Fact]
    public async Task ApplyTo_Closed_Connection_Throws_Visibly()
    {
        var root = NewTempDir();
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(root, "queue.db")};Pooling=False");
        // NOT opened — the first pragma must fail loudly, not be suppressed.
        await Assert.ThrowsAnyAsync<Exception>(
            () => SqliteConnectionPragmas.ApplyToAsync(conn, CancellationToken.None));
    }

    // ── 3. WAL switch blocked by a concurrent writer → visible failure ──
    [Fact]
    public async Task Wal_Switch_Under_Exclusive_Lock_Fails_Visibly()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "queue.db");
        await using (var holder = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await holder.OpenAsync();
            // Fresh file starts in rollback-journal mode; hold an exclusive
            // transaction so no other connection can switch journal_mode.
            await using (var tx = holder.CreateCommand())
            {
                tx.CommandText = "BEGIN EXCLUSIVE;";
                await tx.ExecuteNonQueryAsync();
                await using (var keep = holder.CreateCommand())
                {
                    keep.CommandText = "CREATE TABLE IF NOT EXISTS keepalive (id INTEGER);";
                    await keep.ExecuteNonQueryAsync();
                }

                // The second connection's journal_mode=WAL must NOT silently
                // return a non-WAL connection. Either SQLite surfaces BUSY
                // (retried until the bounded window, then rethrown) or the
                // verification catches the un-switched mode — either way the
                // failure is visible.
                var ex = await Assert.ThrowsAnyAsync<Exception>(
                    () => SqliteConnectionPragmas.OpenWithPragmasAsync($"Data Source={dbPath};Pooling=False"));
                Assert.True(
                    ex is InvalidOperationException
                    || (ex is SqliteException se && se.SqliteErrorCode is 5 or 6),
                    $"expected a visible WAL-switch failure, got {ex.GetType().Name}: {ex.Message}");
            }
        }
        // After the exclusive transaction ends, a normal open succeeds —
        // the visible failure did not corrupt or wedge the database.
        await using var conn = await SqliteConnectionPragmas.OpenWithPragmasAsync($"Data Source={dbPath};Pooling=False");
        Assert.Equal(1, await QueryIntAsync(conn, "PRAGMA foreign_keys;"));
    }

    // ── 4. cancellation propagates out of the retry loop ────────────────
    [Fact]
    public async Task Cancellation_Propagates_During_Wal_Retry()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "queue.db");
        await using (var holder = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await holder.OpenAsync();
            await using (var tx = holder.CreateCommand())
            {
                tx.CommandText = "BEGIN EXCLUSIVE;";
                await tx.ExecuteNonQueryAsync();
                await using (var keep = holder.CreateCommand())
                {
                    keep.CommandText = "CREATE TABLE IF NOT EXISTS keepalive (id INTEGER);";
                    await keep.ExecuteNonQueryAsync();
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                // 150ms cancel beats the 2s retry window: the cancelled token
                // must surface as OperationCanceledException, NOT be swallowed
                // by the old catch-all (which would retry for 2s then throw).
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => SqliteConnectionPragmas.OpenWithPragmasAsync($"Data Source={dbPath};Pooling=False", cts.Token));
            }
        }
    }

    // ── 5. open failure is not swallowed and no connection is returned ──
    [Fact]
    public async Task OpenWithPragmas_Unopenable_Target_Throws_Visibly()
    {
        // A directory cannot be opened as a SQLite file — the factory must
        // rethrow the open failure (and internally dispose the connection).
        var root = NewTempDir();
        await Assert.ThrowsAnyAsync<Exception>(
            () => SqliteConnectionPragmas.OpenWithPragmasAsync($"Data Source={root}", CancellationToken.None));
    }

    // ── 6. SQLCipher: encrypted queue DB is NOT readable as plaintext ───
    // Phase 7 (Stage 4): guards became SKIPS — never pass as empty bodies.
    [SkippableFact]
    public async Task SqlCipher_Queue_Db_Cannot_Be_Opened_As_Plaintext()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI + e_sqlcipher are Windows-only here; executed on windows-latest CI");
        // The invariant this test verifies requires the SQLCipher provider to
        // actually be loadable. If the native bundle is unavailable in this
        // environment, the production constructor fails closed (that is the
        // correct behavior) — skip instead of testing the wrong thing.
        Skip.IfNot(PrintShopCompanion.Infrastructure.Storage.SqlCipherInitializer.IsEncryptionAvailable,
            "SQLCipher native provider unavailable in this testhost");
        var root = NewTempDir();
        var layout = new PrintShopCompanion.Infrastructure.Storage.LocalStorageLayout(
            Microsoft.Extensions.Options.Options.Create(new PrintShopCompanion.Core.Options.StorageOptions()), root);
        var keyStore = new PrintShopCompanion.Infrastructure.Storage.QueueKeyStore(
            layout, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrintShopCompanion.Infrastructure.Storage.QueueKeyStore>.Instance);
        var queue = new SqliteJobQueue(
            Microsoft.Extensions.Options.Options.Create(new PrintShopCompanion.Core.Options.StorageOptions { QueueDbPath = Path.Combine(root, "queue.db") }),
            layout, keyStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteJobQueue>.Instance);

        await queue.EnqueueAsync(new PrintShopCompanion.Core.Models.PrintJobRecord
        {
            JobId = "job-plain-1",
            ShopId = "shop-1",
            DeviceId = "dev-1",
            Title = "plaintext-check",
        });
        await using (var scoped = await queue.OpenScopedAsync(CancellationToken.None))
        {
            await using var cmd = scoped.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM jobs;";
            Assert.Equal(1L, await cmd.ExecuteScalarAsync());
        }

        // The same file opened WITHOUT the SQLCipher key must NOT present a
        // readable plaintext database — the file is ciphertext and SQLite
        // cannot parse its header. A successful read here would mean the
        // encryption silently degraded to plaintext.
        await using (var plain = new SqliteConnection($"Data Source={Path.Combine(root, "queue.db")};Pooling=False"))
        {
            plain.Open();
            await using var cmd = plain.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            await Assert.ThrowsAnyAsync<SqliteException>(() => cmd.ExecuteScalarAsync());
        }
    }
}
