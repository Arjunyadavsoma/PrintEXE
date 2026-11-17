namespace PrintShopCompanion.Infrastructure.Commands;

using System.Collections.Concurrent;

/// <summary>
/// v1 contract §7 + Phase 8 §11 — in-process dedup store for command ids.
/// Prevents re-executing a command the poller has already processed when
/// the backend re-serves it (e.g. our terminal report hadn't reached the
/// server before the next poll tick).
///
/// <para><b>Bounded:</b> keeps at most <see cref="MaxEntries"/> entries; the
/// oldest are evicted. The backend's own (commandId, status) idempotency is
/// the durable guarantee — this store is a process-local fast-path to avoid
/// re-executing side effects (re-printing a test page, re-cancelling a job).</para>
///
/// <para><b>Not persisted:</b> cleared on app restart. On restart, the
/// companion re-fetches any commands the server still considers pending
/// and re-reports the terminal status (idempotent on the backend). Side
/// effects that already completed (a printed test page) are not re-run
/// because the executor's own idempotency guards (e.g. the test-print job's
/// stable <c>test-print-&lt;commandId&gt;</c> JobId) prevent duplicate work.</para>
/// </summary>
public sealed class CommandDedupStore
{
    /// <summary>Maximum entries before the oldest are evicted.</summary>
    public const int MaxEntries = 1000;

    private readonly ConcurrentDictionary<string, ProcessedCommand> _byId = new();
    private readonly LinkedList<string> _lru = new();
    private readonly object _lruGate = new();

    /// <summary>Record a processed command's terminal status + result.</summary>
    public void Record(string commandId, string status, string? resultJson, string? error)
    {
        var entry = new ProcessedCommand(commandId, status, resultJson, error, DateTimeOffset.UtcNow);
        _byId[commandId] = entry;
        lock (_lruGate)
        {
            // Move to front (most-recent). LinkedList<string>.Remove(value) is O(n)
            // but the list is capped at MaxEntries (1000) so this is bounded.
            _lru.Remove(commandId);
            _lru.AddFirst(commandId);
            while (_lru.Count > MaxEntries)
            {
                var oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                _byId.TryRemove(oldest, out _);
            }
        }
    }

    /// <summary>Look up a previously-processed command. Returns false if not seen.</summary>
    public bool TryGet(string commandId, out ProcessedCommand? result) =>
        _byId.TryGetValue(commandId, out result);

    /// <summary>Number of entries currently tracked (for diagnostics/tests).</summary>
    public int Count => _byId.Count;
}

/// <summary>One processed command's terminal result.</summary>
public sealed record ProcessedCommand(
    string CommandId,
    string Status,
    string? ResultJson,
    string? Error,
    DateTimeOffset ProcessedAt);
