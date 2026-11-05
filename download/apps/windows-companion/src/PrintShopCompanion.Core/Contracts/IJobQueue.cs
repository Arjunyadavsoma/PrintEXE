namespace PrintShopCompanion.Core.Contracts;

using PrintShopCompanion.Core.Models;

/// <summary>
/// Durable, idempotent, append-only queue of <see cref="PrintJobRecord"/>s.
/// Backed by SQLite. The companion always persists a job before it processes
/// it; the processor pulls from this queue, not from the network.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Append a job. Idempotent on <see cref="PrintJobRecord.JobId"/>:
    /// inserting a job whose ID already exists returns the existing
    /// record without mutating it. This is the safety net against the
    /// "print twice because of reconnect" failure mode.
    /// </summary>
    Task<PrintJobRecord> EnqueueAsync(PrintJobRecord job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the next job that is ready to be processed (status = Pending or
    /// a retryable Failed job whose next attempt is due). The job is atomically
    /// transitioned to <see cref="JobStatus.Downloading"/> (or left as
    /// <see cref="JobStatus.Pending"/> if the caller has not yet downloaded the PDF)
    /// and is invisible to other dequeue calls until <see cref="ReleaseAsync"/>
    /// or <see cref="MarkCompletedAsync"/> is called.
    /// </summary>
    Task<PrintJobRecord?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>Update mutable fields on a queued job.</summary>
    Task UpdateAsync(PrintJobRecord job, CancellationToken cancellationToken = default);

    /// <summary>Mark a job as Printed; sets PrintedAt and transitions to <see cref="JobStatus.Printed"/>.</summary>
    Task MarkPrintedAsync(string jobId, DateTimeOffset printedAt, int spoolerJobId, CancellationToken cancellationToken = default);

    /// <summary>Mark a job as ReportedPrinted; transitions to <see cref="JobStatus.ReportedPrinted"/>.</summary>
    Task MarkReportedPrintedAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as Failed and schedule a retry after exponential backoff.
    /// If <paramref name="permanent"/> is true, transitions directly to
    /// <see cref="JobStatus.Failed"/> with no retry.
    /// </summary>
    Task MarkFailedAsync(string jobId, string error, bool permanent, CancellationToken cancellationToken = default);

    /// <summary>Release the job back to Pending without changing AttemptCount.</summary>
    Task ReleaseAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Operator-driven cancel.</summary>
    Task CancelAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Snapshot of all jobs for the UI. Sorted by QueuedAt descending.</summary>
    Task<IReadOnlyList<PrintJobRecord>> ListAsync(JobStatus? filterStatus = null, CancellationToken cancellationToken = default);

    /// <summary>Replay any jobs left in a transient "in-flight" state after a crash.</summary>
    Task ReplayInFlightAsync(CancellationToken cancellationToken = default);

    /// <summary>Drop jobs older than the TTL whose status is terminal.</summary>
    Task PurgeTerminalAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    /// <summary>Mark a job UncertainSpooled — never auto-reprinted from this state.</summary>
    Task MarkUncertainAsync(string jobId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Mark a job OutcomeUnknown — heartbeat will attempt reconciliation.</summary>
    Task MarkOutcomeUnknownAsync(string jobId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Operator-driven resolution: marks an uncertain / unknown job as Printed. Never automatic.</summary>
    Task MarkResolvedByOperatorAsync(string jobId, CancellationToken cancellationToken = default);
}
