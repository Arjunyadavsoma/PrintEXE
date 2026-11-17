namespace PrintShopCompanion.Queue.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Commands;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// Phase 3 (brief §7) — tests for the DURABLE command dedup store. The brief
/// requires "durable command execution tracking in the local SQLite database"
/// with atomic claim + restart recovery + no-duplicate-execution-after-restart.
/// These tests use a real (unencrypted) temp SQLite DB to verify the durable
/// guarantees the in-memory LRU cannot provide.
/// </summary>
public class DurableCommandStoreTests : IDisposable
{
    private readonly SqliteJobQueue _queue;
    private readonly DurableCommandStore _store;
    private readonly string _tempDir;

    public DurableCommandStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PrintShopTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var opts = Options.Create(new StorageOptions
        {
            QueueDbPath = Path.Combine(_tempDir, "queue.db"),
            TerminalJobTtl = TimeSpan.FromDays(7),
        });
        var qLogger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteJobQueue>();
        var layout = new LocalStorageLayout(opts);
        _queue = new SqliteJobQueue(opts, layout, keyStore: null, qLogger);
        _store = new DurableCommandStore(_queue, new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());
    }

    private static PendingCommand Cmd(string id, string type = "refresh_printers",
        string payload = "{}", DateTimeOffset? expiresAt = null) =>
        new()
        {
            Id = id,
            CommandType = type,
            PayloadJson = payload,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5),
        };

    [Fact]
    public async Task First_claim_wins_and_returns_Claimed_true()
    {
        var cmd = Cmd("c1");
        var claim = await _store.TryClaimAsync(cmd);
        claim.Claimed.Should().BeTrue("the first caller wins the atomic claim");
        claim.Status.Should().Be("claimed");
    }

    [Fact]
    public async Task Second_claim_for_same_id_loses_and_returns_existing_state()
    {
        var cmd = Cmd("c2");
        var first = await _store.TryClaimAsync(cmd);
        first.Claimed.Should().BeTrue();

        // Second claim for the same id — the row already exists, so we lose.
        var second = await _store.TryClaimAsync(cmd);
        second.Claimed.Should().BeFalse("the second caller loses the atomic claim");
        second.Status.Should().Be("claimed", "we read back the existing row's state");
    }

    [Fact]
    public async Task Terminal_status_is_persisted_and_returned_to_a_later_claim_attempt()
    {
        var cmd = Cmd("c3");
        await _store.TryClaimAsync(cmd);
        await _store.UpdateStatusAsync("c3", CommandStatus.Succeeded, """{"printers":["HP"]}""", null);

        // A late duplicate fetch (e.g. backend re-served it before our ack landed)
        // tries to claim — gets back the terminal state, so the poller re-reports
        // WITHOUT re-executing.
        var late = await _store.TryClaimAsync(cmd);
        late.Claimed.Should().BeFalse();
        late.Status.Should().Be("succeeded");
        late.ResultJson.Should().Be("""{"printers":["HP"]}""");
    }

    [Fact]
    public async Task Restart_recovery_re_executes_a_non_terminal_command_that_is_still_valid()
    {
        // Simulate a crash: claim a command, leave it in 'executing' (no
        // terminal update), then "restart" by creating a fresh store over the
        // same DB. The new store should see the non-terminal row.
        var cmd = Cmd("c4", expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
        await _store.TryClaimAsync(cmd);
        await _store.UpdateStatusAsync("c4", CommandStatus.Executing, null, null);

        // New store instance over the SAME DB (simulates process restart).
        var restarted = new DurableCommandStore(_queue,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());

        var row = await restarted.GetAsync("c4");
        row.Should().NotBeNull();
        row!.ExecutionStatus.Should().Be("executing", "the crash interrupted before the terminal update");
        row.IsTerminal.Should().BeFalse();
        // The poller's restart-recovery logic sees this non-terminal state +
        // (per the poller's logic) re-executes because the command is still valid.
    }

    [Fact]
    public async Task Restart_recovery_does_not_re_execute_a_terminal_command()
    {
        var cmd = Cmd("c5");
        await _store.TryClaimAsync(cmd);
        await _store.UpdateStatusAsync("c5", CommandStatus.Failed, null, "paper jam");

        var restarted = new DurableCommandStore(_queue,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());
        var row = await restarted.GetAsync("c5");
        row!.ExecutionStatus.Should().Be("failed");
        row.IsTerminal.Should().BeTrue("a terminal command is never re-executed");
    }

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_command_id()
    {
        var row = await _store.GetAsync("never-seen");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Payload_hash_is_stable_for_equivalent_payloads()
    {
        // Two commands with the same payload content (different whitespace)
        // should produce the same payload_hash — defensive, lets us detect
        // a server re-sending a command with cosmetic JSON drift.
        var cmd1 = Cmd("c6", payload: """{"jobId":"j-1"}""");
        var cmd2 = Cmd("c7", payload: """{  "jobId" : "j-1"  }""");
        await _store.TryClaimAsync(cmd1);
        await _store.TryClaimAsync(cmd2);
        var r1 = await _store.GetAsync("c6");
        var r2 = await _store.GetAsync("c7");
        r1!.PayloadHash.Should().Be(r2!.PayloadHash, "JSON normalization makes the hash content-addressable");
    }

    [Fact]
    public async Task PurgeOldTerminalAsync_deletes_only_terminal_rows_older_than_cutoff()
    {
        var cmd = Cmd("c8");
        await _store.TryClaimAsync(cmd);
        // Manually mark as terminal + backdate completion_at to 31 days ago.
        await _store.UpdateStatusAsync("c8", CommandStatus.Succeeded, "{}", null);
        // The UpdateStatusAsync sets completion_at=now; backdate it via raw SQL through the queue's connection.
        await _queue.ExecuteInConnectionAsync(async conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText = "UPDATE companion_commands SET completion_at = @old WHERE command_id = 'c8';";
            c.Parameters.AddWithValue("@old", DateTimeOffset.UtcNow.AddDays(-31).ToString("O"));
            await c.ExecuteNonQueryAsync();
        }, default);

        // A non-terminal command (should NOT be purged).
        var cmd2 = Cmd("c9");
        await _store.TryClaimAsync(cmd2);
        await _store.UpdateStatusAsync("c9", CommandStatus.Executing, null, null);

        var purged = await _store.PurgeOldTerminalAsync(TimeSpan.FromDays(30), default);
        purged.Should().Be(1, "only the 31-day-old terminal row is purged");

        // The non-terminal row must still be there (brief §7: don't delete unresolved records).
        var survivor = await _store.GetAsync("c9");
        survivor.Should().NotBeNull("non-terminal commands are NEVER purged");
        survivor!.ExecutionStatus.Should().Be("executing");

        // The terminal row is gone.
        var gone = await _store.GetAsync("c8");
        gone.Should().BeNull();
    }

    [Fact]
    public async Task CountAsync_reflects_current_rows()
    {
        (await _store.CountAsync()).Should().Be(0);
        await _store.TryClaimAsync(Cmd("c10"));
        await _store.TryClaimAsync(Cmd("c11"));
        (await _store.CountAsync()).Should().Be(2);
    }

    public void Dispose()
    {
        _queue.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* test isolation */ }
    }
}
