namespace PrintShopCompanion.Infrastructure.Printers;

using System.Collections.Concurrent;
using PrintShopCompanion.Core.Contracts;

/// <summary>
/// Pure, platform-neutral state machine behind the tri-state spooler outcome
/// model (decision D-03). <see cref="SpoolerOutcome"/> replaces the old
/// boolean poll whose "not found in any queue → assume printed" rule produced
/// false-positive completions:
/// <list type="bullet">
///   <item>A job that was <b>never observed</b> in a queue must not be
///   declared printed — the submission may have failed before the spooler
///   registered it, or the id may be wrong. Outcome stays
///   <see cref="SpoolerOutcome.StillQueued"/> so the confirm loop's timeout
///   parks the job as <see cref="Core.Models.JobStatus.UncertainSpooled"/>.</item>
///   <item>A job that <b>was observed</b> and later disappeared was purged by
///   the spooler after completion — a normal Windows behavior — and is
///   honestly classified <see cref="SpoolerOutcome.PurgedAfterSeen"/>,
///   which the caller maps to printed (with the observation as evidence).</item>
/// </list>
///
/// <para>The tracker is in-memory (agent restart clears it — the job processor's
/// crash-recovery path already handles restart cases separately via the
/// backend claim semantics). Thread-safe. Unit-tested on Linux.</para>
/// </summary>
public sealed class SpoolerOutcomeTracker
{
    private readonly ConcurrentDictionary<int, byte> _seen = new();
    private readonly ConcurrentDictionary<int, SpoolerOutcome> _terminal = new();

    /// <summary>Record an observation for this poll cycle: was the job found in a queue, and did it print?</summary>
    public SpoolerOutcome Observe(int spoolerJobId, bool foundInQueue, bool reportedPrinted)
    {
        if (_terminal.TryGetValue(spoolerJobId, out var settled))
            return settled;

        if (reportedPrinted)
        {
            _terminal[spoolerJobId] = SpoolerOutcome.Printed;
            return SpoolerOutcome.Printed;
        }

        if (foundInQueue)
        {
            _seen[spoolerJobId] = 1;
            return SpoolerOutcome.StillQueued;
        }

        // Not found now. Only a previously-seen job can be "purged after success".
        return _seen.ContainsKey(spoolerJobId)
            ? Settle(spoolerJobId, SpoolerOutcome.PurgedAfterSeen)
            : SpoolerOutcome.StillQueued;
    }

    /// <summary>Explicitly settle a job (e.g. the spooler reported a terminal error and the caller decides the class).</summary>
    public SpoolerOutcome Settle(int spoolerJobId, SpoolerOutcome outcome)
    {
        _terminal[spoolerJobId] = outcome;
        return outcome;
    }

    /// <summary>True if the job was observed in a queue at least once.</summary>
    public bool WasSeen(int spoolerJobId) => _seen.ContainsKey(spoolerJobId);
}
