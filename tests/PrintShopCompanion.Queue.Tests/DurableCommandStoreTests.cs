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
/// Phase F (brief §B) — the 13 required durable-command tests against a REAL
/// temp SQLite database (unencrypted for test isolation). These validate
/// actual SQLite behavior, not mocked repository interfaces.
///
/// <para>The 13 required scenarios (brief §B "Required tests"):</para>
/// <list type="number">
///   <item>New command insertion.</item>
///   <item>Duplicate command ID.</item>
///   <item>Atomic claim.</item>
///   <item>Two concurrent claim attempts.</item>
///   <item>Restart after receipt.</item>
///   <item>Restart after claim.</item>
///   <item>Restart after execution starts.</item>
///   <item>Terminal command redelivery.</item>
///   <item>Expired command.</item>
///   <item>Database lock and recovery.</item>
///   <item>Payload and result persistence.</item>
///   <item>Retention behavior.</item>
///   <item>Uncertain command is never automatically re-executed.</item>
/// </list>
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

    // ── 1. New command insertion ──────────────────────────────────────────

    [Fact]
    public async Task Test1_New_command_insertion_creates_a_received_row()
    {
        var cmd = Cmd("c1", payload: """{"foo":"bar"}""");
        var row = await _store.RecordReceivedAsync(cmd);
        row.Should().NotBeNull();
        row!.CommandId.Should().Be("c1");
        row.CommandType.Should().Be("refresh_printers");
        row.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.Received);
        row.PayloadJson.Should().Be("""{"foo":"bar"}""");
        row.PayloadHash.Should().NotBeNullOrEmpty();
        (await _store.CountAsync()).Should().Be(1);
    }

    // ── 2. Duplicate command ID ───────────────────────────────────────────

    [Fact]
    public async Task Test2_Duplicate_command_id_is_idempotent_on_RecordReceived()
    {
        var cmd = Cmd("c2");
        var first = await _store.RecordReceivedAsync(cmd);
        var second = await _store.RecordReceivedAsync(cmd);
        second!.CommandId.Should().Be(first!.CommandId);
        second.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.Received);
        (await _store.CountAsync()).Should().Be(1, "a duplicate insert is a no-op");
    }

    [Fact]
    public async Task Test2b_Duplicate_command_id_after_terminal_is_NOT_reset_to_received()
    {
        // Critical: a redelivery of a TERMINAL command must NOT reset it to
        // 'received' (which would re-execute it). ON CONFLICT DO NOTHING
        // preserves the terminal state.
        var cmd = Cmd("c2b");
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);
        await _store.MarkExecutingAsync(cmd.Id);
        await _store.MarkTerminalAsync(cmd.Id, DurableCommandStore.CommandState.Succeeded, """{"ok":true}""");

        var row = await _store.RecordReceivedAsync(cmd);
        row!.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.Succeeded,
            "a redelivery of a terminal command must NOT reset it to received (would re-execute)");
    }

    // ── 3. Atomic claim ────────────────────────────────────────────────────

    [Fact]
    public async Task Test3_Atomic_claim_moves_received_to_claimed_and_first_call_wins()
    {
        var cmd = Cmd("c3");
        await _store.RecordReceivedAsync(cmd);
        var claim = await _store.TryClaimAsync(cmd);
        claim.Claimed.Should().BeTrue("the first claim wins");
        claim.Status.Should().Be(DurableCommandStore.CommandState.Claimed);

        var row = await _store.GetAsync(cmd.Id);
        row!.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.Claimed);
        row.AcceptedAt.Should().NotBeNull();
    }

    // ── 4. Two concurrent claim attempts ──────────────────────────────────

    [Fact]
    public async Task Test4_Two_concurrent_claim_attempts_only_one_wins()
    {
        var cmd = Cmd("c4");
        await _store.RecordReceivedAsync(cmd);
        var results = await Task.WhenAll(_store.TryClaimAsync(cmd), _store.TryClaimAsync(cmd));
        var wins = results.Count(r => r.Claimed);
        wins.Should().Be(1, "exactly one concurrent claim wins; the other sees 'claimed'");
        var loser = results.First(r => !r.Claimed);
        loser.Status.Should().Be(DurableCommandStore.CommandState.Claimed,
            "the loser sees the winner's 'claimed' state — it does NOT execute");
    }

    // ── 5. Restart after receipt ─────────────────────────────────────────

    [Fact]
    public async Task Test5_Restart_after_receipt_leaves_received_eligible_for_processing()
    {
        var cmd = Cmd("c5");
        await _store.RecordReceivedAsync(cmd);
        var restarted = new DurableCommandStore(_queue,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());

        var outcomeUnknown = await restarted.RecoverInterruptedAsync();
        outcomeUnknown.Should().BeEmpty("a 'received' row is NOT transitioned to outcome_unknown");

        var claim = await restarted.TryClaimAsync(cmd);
        claim.Claimed.Should().BeTrue("a 'received' command is eligible for normal processing after restart");
    }

    // ── 6. Restart after claim ────────────────────────────────────────────

    [Fact]
    public async Task Test6_Restart_after_claim_resets_claimed_to_received_safe_to_re_execute()
    {
        // The brief's Phase A: "claimed but never marked executing: recover only
        // if the implementation can prove no side effect started." 'claimed'
        // means the handler never started → safe to re-execute.
        var cmd = Cmd("c6");
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);

        var restarted = new DurableCommandStore(_queue,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());
        var outcomeUnknown = await restarted.RecoverInterruptedAsync();
        outcomeUnknown.Should().BeEmpty("'claimed' is NOT outcome_unknown — it's safe to re-execute");

        var row = await restarted.GetAsync(cmd.Id);
        row!.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.Received,
            "RecoverInterrupted resets 'claimed' to 'received' so the poller re-claims + re-executes (safe — no side effect ran)");

        var claim = await restarted.TryClaimAsync(cmd);
        claim.Claimed.Should().BeTrue();
    }

    // ── 7. Restart after execution starts ─────────────────────────────────

    [Fact]
    public async Task Test7_Restart_after_execution_starts_transitions_to_outcome_unknown_NEVER_re_executes()
    {
        // The brief's Phase A acceptance criterion #1: "Interrupted
        // non-idempotent commands cannot be silently re-executed."
        var cmd = Cmd("c7", type: "test_print");
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);
        await _store.MarkExecutingAsync(cmd.Id);

        var restarted = new DurableCommandStore(_queue,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());
        var outcomeUnknown = await restarted.RecoverInterruptedAsync();
        outcomeUnknown.Should().HaveCount(1, "the 'executing' row is transitioned to outcome_unknown");
        outcomeUnknown[0].CommandId.Should().Be("c7");

        var row = await restarted.GetAsync(cmd.Id);
        row!.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.OutcomeUnknown);
        row.IsTerminal.Should().BeTrue();
        row.IsOutcomeUnknown.Should().BeTrue();
        row.OutcomeUnknownAt.Should().NotBeNull();

        var claim = await restarted.TryClaimAsync(cmd);
        claim.Claimed.Should().BeFalse("an outcome_unknown command is NEVER re-executed");
        claim.Status.Should().Be(DurableCommandStore.CommandState.OutcomeUnknown);
    }

    // ── 8. Terminal command redelivery ────────────────────────────────────

    [Fact]
    public async Task Test8_Terminal_command_redelivery_does_not_re_execute()
    {
        var cmd = Cmd("c8");
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);
        await _store.MarkExecutingAsync(cmd.Id);
        await _store.MarkTerminalAsync(cmd.Id, DurableCommandStore.CommandState.Succeeded, """{"ok":true}""");

        var late = await _store.TryClaimAsync(cmd);
        late.Claimed.Should().BeFalse("a terminal command is never re-claimed");
        late.Status.Should().Be(DurableCommandStore.CommandState.Succeeded);
        late.ResultJson.Should().Be("""{"ok":true}""");
    }

    // ── 9. Expired command ────────────────────────────────────────────────

    [Fact]
    public async Task Test9_Expired_command_is_marked_expired_not_executed()
    {
        var cmd = Cmd("c9", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);
        await _store.MarkExpiredAsync(cmd.Id);

        var row = await _store.GetAsync(cmd.Id);
        row!.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.Expired);
        row.IsTerminal.Should().BeTrue();
        row.CompletionAt.Should().NotBeNull();
    }

    // ── 10. Database lock and recovery ────────────────────────────────────

    [Fact]
    public async Task Test10_Database_lock_retries_with_bounded_backoff_then_recovers()
    {
        var cmd = Cmd("c10");
        await _store.RecordReceivedAsync(cmd);

        // Hold a write transaction on a separate connection to force BUSY.
        await using var blockingConn = new Microsoft.Data.Sqlite.SqliteConnection(_queue.ConnectionStringForTests);
        await blockingConn.OpenAsync();
        await using (var blockTx = (Microsoft.Data.Sqlite.SqliteTransaction)await blockingConn.BeginTransactionAsync())
        {
            await using var hold = blockingConn.CreateCommand();
            hold.Transaction = blockTx;
            hold.CommandText = "UPDATE companion_commands SET execution_status = execution_status WHERE command_id = 'c10';";
            await hold.ExecuteNonQueryAsync();
            // blockTx NOT committed — write lock stays held.

            // The store's claim attempt hits BUSY + retries; with the lock held
            // indefinitely it eventually exhausts retries + throws (does NOT hang).
            var act = async () => await _store.TryClaimAsync(cmd);
            await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(
                "after bounded retries the store propagates the BUSY exception (does NOT hang indefinitely)");
        }
        // blockTx disposed (rolled back) → lock released.

        var claim = await _store.TryClaimAsync(cmd);
        claim.Claimed.Should().BeTrue("the store recovers after the lock is released");
    }

    // ── 11. Payload and result persistence ────────────────────────────────

    [Fact]
    public async Task Test11_Payload_and_result_are_persisted_and_round_tripped()
    {
        var cmd = Cmd("c11", type: "cancel_job", payload: """{"jobId":"job-uuid-001"}""");
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);
        await _store.MarkExecutingAsync(cmd.Id);
        await _store.MarkTerminalAsync(cmd.Id, DurableCommandStore.CommandState.Succeeded,
            """{"jobId":"job-uuid-001","cancelled":true}""");

        var row = await _store.GetAsync(cmd.Id);
        row!.PayloadJson.Should().Be("""{"jobId":"job-uuid-001"}""");
        row.ResultJson.Should().Be("""{"jobId":"job-uuid-001","cancelled":true}""");
        row.CommandType.Should().Be("cancel_job");
    }

    // ── 12. Retention behavior ────────────────────────────────────────────

    [Fact]
    public async Task Test12_Retention_purges_old_terminal_rows_but_keeps_non_terminal()
    {
        // (a) An old terminal 'succeeded' row — should be purged.
        var oldOk = Cmd("c12a");
        await _store.RecordReceivedAsync(oldOk);
        await _store.TryClaimAsync(oldOk);
        await _store.MarkExecutingAsync(oldOk.Id);
        await _store.MarkTerminalAsync(oldOk.Id, DurableCommandStore.CommandState.Succeeded, "{}");
        await BackdateCompletionAsync(oldOk.Id, DateTimeOffset.UtcNow.AddDays(-31));

        // (b) An old terminal 'outcome_unknown' row — should be purged (it's terminal;
        // the shopkeeper had 30 days to resolve it).
        var oldUncertain = Cmd("c12b");
        await _store.RecordReceivedAsync(oldUncertain);
        await _store.TryClaimAsync(oldUncertain);
        await _store.MarkExecutingAsync(oldUncertain.Id);
        await _store.MarkTerminalAsync(oldUncertain.Id, DurableCommandStore.CommandState.OutcomeUnknown, error: "uncertain");
        await BackdateCompletionAsync(oldUncertain.Id, DateTimeOffset.UtcNow.AddDays(-31));

        // (c) A recent terminal row — should NOT be purged (within the cutoff).
        var recentOk = Cmd("c12c");
        await _store.RecordReceivedAsync(recentOk);
        await _store.TryClaimAsync(recentOk);
        await _store.MarkExecutingAsync(recentOk.Id);
        await _store.MarkTerminalAsync(recentOk.Id, DurableCommandStore.CommandState.Succeeded, "{}");

        // (d) A non-terminal 'executing' row — should NEVER be purged (may still recover).
        var nonTerminal = Cmd("c12d");
        await _store.RecordReceivedAsync(nonTerminal);
        await _store.TryClaimAsync(nonTerminal);
        await _store.MarkExecutingAsync(nonTerminal.Id);

        var purged = await _store.PurgeOldTerminalAsync(TimeSpan.FromDays(30));
        purged.Should().Be(2, "the two 31-day-old terminal rows (succeeded + outcome_unknown) are purged");

        (await _store.GetAsync(recentOk.Id)).Should().NotBeNull("a recent terminal row is within the cutoff");
        (await _store.GetAsync(nonTerminal.Id)).Should().NotBeNull("a non-terminal row is NEVER purged");
        (await _store.GetAsync(oldOk.Id)).Should().BeNull("the old succeeded row was purged");
        (await _store.GetAsync(oldUncertain.Id)).Should().BeNull("the old outcome_unknown row was purged (terminal; 30-day resolution window elapsed)");
    }

    // ── 13. Uncertain command is never automatically re-executed ──────────

    [Fact]
    public async Task Test13_Uncertain_command_is_never_automatically_re_executed()
    {
        // The brief's acceptance criterion #1, restated as an explicit test.
        var cmd = Cmd("c13", type: "test_print");
        await _store.RecordReceivedAsync(cmd);
        await _store.TryClaimAsync(cmd);
        await _store.MarkExecutingAsync(cmd.Id);

        var restarted = new DurableCommandStore(_queue,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableCommandStore>());

        var recovered = await restarted.RecoverInterruptedAsync();
        recovered.Should().HaveCount(1);

        var claimAfterRecovery = await restarted.TryClaimAsync(cmd);
        claimAfterRecovery.Claimed.Should().BeFalse(
            "an outcome_unknown command is NEVER re-executed, even on redelivery");
        claimAfterRecovery.Status.Should().Be(DurableCommandStore.CommandState.OutcomeUnknown);

        // Even an explicit RecordReceived (simulating the SaaS re-sending it)
        // does NOT reset the terminal state.
        var row = await restarted.RecordReceivedAsync(cmd);
        row!.ExecutionStatus.Should().Be(DurableCommandStore.CommandState.OutcomeUnknown,
            "ON CONFLICT DO NOTHING preserves the terminal outcome_unknown state across redelivery");
    }

    // ── helper ────────────────────────────────────────────────────────────

    private async Task BackdateCompletionAsync(string commandId, DateTimeOffset when)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_queue.ConnectionStringForTests);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE companion_commands SET completion_at = @old WHERE command_id = @id;";
        cmd.Parameters.AddWithValue("@old", when.ToString("O"));
        cmd.Parameters.AddWithValue("@id", commandId);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* test isolation */ }
    }
}
