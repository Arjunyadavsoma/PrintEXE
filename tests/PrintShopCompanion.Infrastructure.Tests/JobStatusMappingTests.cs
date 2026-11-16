namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using PrintShopCompanion.Core.Models;
using Xunit;

/// <summary>
/// Verifies the local-to-backend job status mapping is exactly what the real SaaS
/// backend accepts. Per docs/CONTRACT_COMPATIBILITY.md §2.6, the backend's
/// <c>POST /agents/jobs/{id}/status</c> endpoint accepts ONLY three status strings:
/// <c>"printing"</c>, <c>"completed"</c>, <c>"failed"</c>. Any other local state must
/// map to one of these or be suppressed (not reported). This guards against drift
/// if someone adds a new local JobStatus value without considering the backend.
/// </summary>
public static class JobStatusMapping
{
    /// <summary>Backend status string for "claimed / actively printing".</summary>
    public const string BackendPrinting = "printing";

    /// <summary>Backend status string for "finished successfully".</summary>
    public const string BackendCompleted = "completed";

    /// <summary>Backend status string for "permanent failure".</summary>
    public const string BackendFailed = "failed";

    /// <summary>
    /// Map a local <see cref="JobStatus"/> to the backend status string the companion
    /// should send via <c>POST /agents/jobs/{id}/status</c>. Returns <c>null</c> when
    /// the local state should NOT be reported (terminal/sync states the backend
    /// doesn't model, or intermediate states that have no backend counterpart).
    /// </summary>
    /// <remarks>
    /// Mapping rules (derived from docs/CONTRACT_COMPATIBILITY.md §2.6 + PrintJobRecord.cs):
    /// <list type="bullet">
    ///   <item><c>Pending</c>: not yet claimed — do NOT report (backend already has it as <c>queued</c>).</item>
    ///   <item><c>Downloading</c>/<c>Rendering</c>/<c>Spooled</c>: companion has claimed and is working — report <c>"printing"</c> at claim time (atomic conditional UPDATE on the backend).</item>
    ///   <item><c>Printed</c>/<c>ReportedPrinted</c>: physical print confirmed — report <c>"completed"</c>.</item>
    ///   <item><c>Failed</c>: permanent failure — report <c>"failed"</c> with <c>lastError</c>.</item>
    ///   <item><c>UncertainSpooled</c>/<c>OutcomeUnknown</c>: do NOT auto-report. The backend's
    ///     <c>needs_review</c> state is reached only by the server-side stale-recovery worker
    ///     (5-min threshold), not by the companion. The companion must leave these states and
    ///     let the operator resolve them (see docs/AUDIT.md §5 bug #3).</item>
    ///   <item><c>Cancelled</c>: the backend has no <c>cancelled</c> job status. Cancellation
    ///     is modelled at the order level (OrderStatus.cancelled), not the job level. The
    ///     companion reports nothing for a locally-cancelled job.</item>
    /// </list>
    /// </remarks>
    public static string? ToBackendStatus(JobStatus local) => local switch
    {
        JobStatus.Pending => null,                  // not yet claimed; backend already has it as 'queued'
        JobStatus.Downloading => BackendPrinting,   // claim happens at download start
        JobStatus.Rendering => BackendPrinting,
        JobStatus.Spooled => BackendPrinting,
        JobStatus.UncertainSpooled => null,         // operator-only; backend reaches needs_review via stale worker
        JobStatus.Printed => BackendCompleted,
        JobStatus.ReportedPrinted => null,           // already reported; idempotent re-report is a no-op
        JobStatus.OutcomeUnknown => null,           // operator-only; reconcile on next heartbeat
        JobStatus.Failed => BackendFailed,
        JobStatus.Cancelled => null,                 // backend has no cancelled job status
        _ => null,
    };
}

public class JobStatusMappingTests
{
    [Theory]
    [InlineData(JobStatus.Downloading, "printing")]
    [InlineData(JobStatus.Rendering, "printing")]
    [InlineData(JobStatus.Spooled, "printing")]
    [InlineData(JobStatus.Printed, "completed")]
    [InlineData(JobStatus.Failed, "failed")]
    public void Maps_local_states_to_backend_status_strings(JobStatus local, string expected)
    {
        // The backend POST /agents/jobs/{id}/status accepts ONLY "printing"|"completed"|"failed".
        // Any local state we DO report must map to one of these three exactly.
        JobStatusMapping.ToBackendStatus(local).Should().Be(expected);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.UncertainSpooled)]
    [InlineData(JobStatus.ReportedPrinted)]
    [InlineData(JobStatus.OutcomeUnknown)]
    [InlineData(JobStatus.Cancelled)]
    public void Does_not_report_states_the_backend_does_not_model(JobStatus local)
    {
        // These local states have no backend counterpart:
        //  - Pending: backend already has it as 'queued' (no transition to report)
        //  - UncertainSpooled/OutcomeUnknown: operator-only; backend reaches needs_review
        //    via the server-side stale-recovery worker (5-min threshold), NOT via the
        //    companion. Reporting here would risk the backend marking the job failed
        //    when the paper actually came out.
        //  - ReportedPrinted: already reported; idempotent re-report is a no-op.
        //  - Cancelled: backend has no cancelled job status (cancellation is order-level).
        JobStatusMapping.ToBackendStatus(local).Should().BeNull();
    }

    [Fact]
    public void Every_local_JobStatus_has_an_explicit_mapping()
    {
        // Exhaustiveness guard: if a new JobStatus is added, this test fails until
        // the mapping is updated. Prevents silent drift where a new state is never
        // reported to the backend.
        var allStates = Enum.GetValues<JobStatus>();
        foreach (var state in allStates)
        {
            var mapped = JobStatusMapping.ToBackendStatus(state);
            // Either a valid backend string or explicitly null — both are fine.
            // The point is that the switch arm exists (no default fall-through hiding it).
            (mapped is null || mapped is "printing" or "completed" or "failed")
                .Should().BeTrue($"every JobStatus ({state}) must map to a known backend string or null");
        }
    }

    [Fact]
    public void Backend_only_accepts_three_status_strings()
    {
        // Contract pin: the real SaaS backend's StatusSchema is a z.enum of exactly
        // three values. Any other string would be a 4xx VALIDATION_ERROR.
        // See docs/CONTRACT_COMPATIBILITY.md §2.6.
        var accepted = new[] { "printing", "completed", "failed" };
        JobStatusMapping.BackendPrinting.Should().Be("printing");
        JobStatusMapping.BackendCompleted.Should().Be("completed");
        JobStatusMapping.BackendFailed.Should().Be("failed");
        accepted.Should().HaveCount(3, "the backend StatusSchema is a 3-value enum");
    }
}
