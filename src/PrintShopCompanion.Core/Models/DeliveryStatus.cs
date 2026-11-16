namespace PrintShopCompanion.Core.Models;

/// <summary>
/// The delivery state machine — tracks the PDF's journey from the
/// customer upload to the shopkeeper's local PC. Independent of
/// <see cref="PrintingStatus"/> (a job can be delivered but not yet
/// printed) and <see cref="StorageStatus"/> (a job can be locally
/// received but the cloud copy may still exist).
/// </summary>
public enum DeliveryStatus
{
    /// <summary>
    /// The job was just created on the server (customer uploaded, file in
    /// Supabase Storage, but no delivery attempt has started yet).
    /// </summary>
    Created,

    /// <summary>
    /// The server is attempting direct delivery (WebRTC) to an online agent.
    /// Only set when the agent is connected AND supports direct transfer.
    /// v0.5.0: this state is set transiently but always falls back to
    /// CloudStaged (WebRTC not yet shipped).
    /// </summary>
    DirectAttempt,

    /// <summary>
    /// The PDF is in Supabase Storage, waiting for an agent to download it
    /// (agent is offline, or direct transfer is not supported).
    /// This is the default fallback state in v0.5.0.
    /// </summary>
    CloudStaged,

    /// <summary>
    /// The agent is actively downloading the PDF (either via WebRTC
    /// DataChannel or via the presigned cloud URL).
    /// </summary>
    Transferring,

    /// <summary>
    /// The agent has confirmed local receipt — the PDF is on the shop PC,
    /// SHA-256 verified, atomically persisted to Jobs/queued/.
    /// This is the gate for cloud deletion: cloudDeleteAfter = receivedAt + 15 min.
    /// </summary>
    Received,

    /// <summary>
    /// Delivery failed permanently (e.g. SHA-256 mismatch, download retry
    /// limit exceeded, agent rejected the transfer due to insufficient
    /// disk space). The job is NOT auto-retried — the shopkeeper must
    /// manually re-dispatch from the dashboard.
    /// </summary>
    DeliveryFailed,
}

/// <summary>
/// The storage state machine — tracks where the PDF bytes physically
/// live (cloud, local, or both). Independent of delivery + printing.
/// </summary>
public enum StorageStatus
{
    /// <summary>
    /// The customer's browser is uploading the PDF to Supabase Storage.
    /// </summary>
    Uploading,

    /// <summary>
    /// The PDF is in Supabase Storage, ready for an agent to download.
    /// </summary>
    CloudAvailable,

    /// <summary>
    /// The agent has confirmed local receipt (POST /agents/jobs/{id}/received).
    /// The cloud copy is still in Supabase Storage but scheduled for deletion
    /// (cloudDeleteAfter = receivedAt + 15 min).
    /// </summary>
    LocalVerified,

    /// <summary>
    /// The cloud cleanup worker has picked up the job but hasn't deleted
    /// the Supabase object yet (it's within the cleanup window).
    /// </summary>
    CloudDeletePending,

    /// <summary>
    /// The Supabase object has been deleted. The local copy may still exist
    /// (until localDeleteAfter fires).
    /// </summary>
    CloudDeleted,

    /// <summary>
    /// The local PDF has been deleted (60 min after terminal printing state).
    /// The PrintJobRecord row in SQLite is preserved (business history).
    /// </summary>
    LocalDeleted,
}

/// <summary>
/// The printing state machine (companion-side) — tracks the print job
/// lifecycle from the moment the JobProcessor dequeues it to the moment
/// the spooler confirms physical print. Independent of delivery + storage.
/// </summary>
/// <remarks>
/// This is the v0.5 rename of the v0.4.7 <see cref="JobStatus"/> enum.
/// The old enum is kept as an alias for backward compat with the existing
/// SQLite schema + JobProcessor code.
/// </remarks>
public enum PrintingStatus
{
    /// <summary>The job is in the local SQLite queue, awaiting dequeue.</summary>
    Queued,

    /// <summary>
    /// The companion has atomically claimed the job (POST /status {status:"printing"})
    /// but hasn't yet submitted it to the Windows spooler.
    /// </summary>
    Claimed,

    /// <summary>
    /// The job has been submitted to the Windows spooler. The companion
    /// is polling for spooler confirmation (up to PrintConfirmTimeout).
    /// </summary>
    Printing,

    /// <summary>
    /// The spooler confirmed physical print. The cloud copy is scheduled
    /// for deletion; the local copy is scheduled for deletion 60 min later.
    /// </summary>
    Completed,

    /// <summary>
    /// The print failed permanently (e.g. printer offline, paper jam,
    /// max retries exceeded). The shopkeeper must manually intervene.
    /// </summary>
    Failed,

    /// <summary>
    /// The job was cancelled (either by the shopkeeper or because another
    /// agent claimed it first).
    /// </summary>
    Cancelled,

    /// <summary>
    /// The companion submitted to the spooler but cannot confirm whether
    /// the paper physically came out (e.g. crash between Submit and the
    /// first status poll, or spooler job was purged). NEVER auto-reprint.
    /// Operator must explicitly mark the job as resolved.
    /// </summary>
    UncertainSpooled,

    /// <summary>
    /// The companion reported Completed to the backend but never got a 2xx
    /// response. The next heartbeat will re-fetch the job's status from
    /// the backend and reconcile.
    /// </summary>
    OutcomeUnknown,

    /// <summary>
    /// The companion reported Completed AND the backend confirmed receipt
    /// (2xx response). This is the true terminal success state.
    /// </summary>
    ReportedCompleted,
}
