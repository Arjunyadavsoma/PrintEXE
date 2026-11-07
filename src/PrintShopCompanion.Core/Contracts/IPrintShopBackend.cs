namespace PrintShopCompanion.Core.Contracts;

using PrintShopCompanion.Core.Models;

/// <summary>
/// Front-end for the PrintShop backend. Both the HTTP REST API and the
/// SignalR/Socket.IO transport are funneled through this interface so the
/// companion code does not depend on either protocol directly.
///
/// <para><b>v0.4.7 contract:</b> the companion authenticates with a device
/// token (NOT JWT) sent as <c>Authorization: Bearer &lt;deviceToken&gt;</c>.
/// The token is obtained by the shopkeeper on the web dashboard
/// (<c>POST /api/v1/agents/register</c>) and pasted into PrintEXE.</para>
/// </summary>
public interface IPrintShopBackend
{
    /// <summary>True once we have a stored device token.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Current shop info, or null if not paired.</summary>
    ShopInfo? CurrentShop { get; }

    /// <summary>Current device registration, or null if not paired.</summary>
    DeviceRegistration? CurrentDevice { get; }

    /// <summary>
    /// Store the device token obtained from the web dashboard's "Register
    /// new agent" flow. The shopkeeper pastes the token into PrintEXE;
    /// PrintEXE stores it (DPAPI-encrypted) and uses it as the Bearer
    /// credential on every subsequent call.
    /// <para>This replaces the v0.4.6 <c>PairAsync</c> magic-link flow —
    /// the production backend uses paste-token, not magic-link.</para>
    /// </summary>
    Task PairWithDeviceTokenAsync(string deviceToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchange the stored device token for a short-lived JWT (15 min TTL).
    /// The JWT is used ONLY for Socket.IO auth (emit <c>auth {token: jwt}</c>
    /// after connecting). REST calls continue to use the device token
    /// directly. The JWT is HS256-signed by the backend and PrintEXE does
    /// NOT need to verify it — the Socket.IO server does that.
    /// </summary>
    Task<string> ExchangeDeviceTokenForJwtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Heartbeat: refreshes presence on the backend. Send every 60 seconds.
    /// </summary>
    Task<HeartbeatResult> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch the next batch of pending jobs for this device (up to 10).
    /// The companion is responsible for claiming each one via
    /// <see cref="ReportJobStatusAsync"/> with <c>status:"printing"</c>.
    /// </summary>
    Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Report a job's status. Atomic claim semantics: sending
    /// <c>status:"printing"</c> is a conditional UPDATE that succeeds only
    /// if the job is still in <c>queued</c> state. If another agent claimed
    /// it first, the response has <c>skipped:true</c> and the companion
    /// moves on to the next job.
    /// <para>Set <paramref name="status"/> to one of:
    /// <c>"printing"</c> (claim), <c>"completed"</c> (success), <c>"failed"</c> (failure).</para>
    /// <para>For <c>"failed"</c>, set <paramref name="errorMessage"/> to a
    /// short description (e.g. "paper jam").</para>
    /// </summary>
    Task<JobStatusReport> ReportJobStatusAsync(
        string jobId,
        string status,
        string? printerName,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download the PDF bytes for a job. The <paramref name="pdfUrl"/> is a
    /// presigned S3/Supabase URL with 15-minute TTL — no auth needed (the
    /// signature is in the URL). The companion downloads directly from
    /// the URL, not through the API.
    /// </summary>
    Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>Revoke (locally) the device token. Clears the in-memory and persisted credentials.</summary>
    Task RevokeDeviceAsync(CancellationToken cancellationToken = default);

    /// <summary>Incremental sync pull (cursor-based).</summary>
    Task<SyncPullResult> SyncPullAsync(string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Submit client-side changes (only Customer entity in v1).</summary>
    Task<SyncPushResult> SyncPushAsync(object changes, CancellationToken cancellationToken = default);

    /// <summary>Check sync state (cursor, last sync time, online status).</summary>
    Task<SyncStatusResult> SyncStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record HeartbeatResult(
    DateTimeOffset ServerTime,
    bool TokenStillValid,
    string? RecommendedBackendVersion);

public sealed record DownloadedPdf(
    string LocalPath,
    string Sha256,
    long SizeBytes);

/// <summary>
/// Result of POST /agents/jobs/{id}/status. The companion uses
/// <see cref="Applied"/> to decide whether to print the job, and
/// <see cref="Skipped"/> to decide whether to silently move on (another
/// agent claimed it, or the job is already completed).
/// </summary>
public sealed record JobStatusReport(
    string? JobId,
    string? Status,
    bool Applied,
    bool Skipped,
    string? Reason);

public sealed record SyncPullResult(
    IReadOnlyList<SyncChange> Changes,
    string? NextCursor,
    bool HasMore);

public sealed record SyncChange(
    string Entity,
    string Id,
    string Action,
    string DataJson,
    int Revision,
    DateTimeOffset? UpdatedAt);

public sealed record SyncPushResult(
    IReadOnlyList<SyncPushResultItem> Results);

public sealed record SyncPushResultItem(
    string? Id,
    bool Accepted,
    int? ServerRevision,
    bool Conflict);

public sealed record SyncStatusResult(
    string? DeviceId,
    string? ShopId,
    string? SyncCursor,
    DateTimeOffset? LastSyncAt,
    string? AgentName,
    bool IsOnline);
