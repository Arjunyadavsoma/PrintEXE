using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Infrastructure.Printers;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>
/// Decision D-03 — the tri-state spooler outcome state machine. These tests
/// encode the exact bug the Phase 0 audit found in HasPrintedAsync: a job
/// that was never observed in a queue must NOT be declared printed, while a
/// job observed-then-purged IS honest evidence of success.
/// </summary>
public sealed class SpoolerOutcomeTrackerTests
{
    [Fact]
    public void Never_seen_job_stays_stillQueued_even_when_absent_from_queues()
    {
        var tracker = new SpoolerOutcomeTracker();
        var outcome = tracker.Observe(42, foundInQueue: false, reportedPrinted: false);
        Assert.Equal(SpoolerOutcome.StillQueued, outcome);
    }

    [Fact]
    public void Seen_job_still_queued_reports_stillQueued()
    {
        var tracker = new SpoolerOutcomeTracker();
        Assert.Equal(SpoolerOutcome.StillQueued, tracker.Observe(7, foundInQueue: true, reportedPrinted: false));
    }

    [Fact]
    public void Seen_then_gone_is_purgedAfterSeen()
    {
        var tracker = new SpoolerOutcomeTracker();
        tracker.Observe(7, foundInQueue: true, reportedPrinted: false);
        var outcome = tracker.Observe(7, foundInQueue: false, reportedPrinted: false);
        Assert.Equal(SpoolerOutcome.PurgedAfterSeen, outcome);
    }

    [Fact]
    public void Spooler_reported_printed_is_printed()
    {
        var tracker = new SpoolerOutcomeTracker();
        var outcome = tracker.Observe(9, foundInQueue: true, reportedPrinted: true);
        Assert.Equal(SpoolerOutcome.Printed, outcome);
    }

    [Fact]
    public void Terminal_classifications_are_sticky()
    {
        var tracker = new SpoolerOutcomeTracker();
        tracker.Observe(11, foundInQueue: true, reportedPrinted: true);
        // A later poll must not downgrade the classification.
        var outcome = tracker.Observe(11, foundInQueue: false, reportedPrinted: false);
        Assert.Equal(SpoolerOutcome.Printed, outcome);
    }

    [Fact]
    public void Different_jobs_are_tracked_independently()
    {
        var tracker = new SpoolerOutcomeTracker();
        tracker.Observe(1, foundInQueue: true, reportedPrinted: false);
        // Job 2 was never seen — its absence must not inherit job 1's seen state.
        Assert.Equal(SpoolerOutcome.StillQueued, tracker.Observe(2, foundInQueue: false, reportedPrinted: false));
        Assert.Equal(SpoolerOutcome.PurgedAfterSeen, tracker.Observe(1, foundInQueue: false, reportedPrinted: false));
    }
}
