namespace PrintShopCompanion.Core.Contracts;

using PrintShopCompanion.Core.Models;

/// <summary>
/// Front-end for the PrintShop backend. Both the HTTP REST API and the
/// SignalR/Socket.IO transport are funneled through this interface so the
/// companion code does not depend on either protocol directly.
/// </summary>
public interface IPrintShopBackend
{
    /// <summary>True once we have a stored device token and have validated it within the last 5 minutes.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Current shop info, or null if not paired.</summary>
    ShopInfo? CurrentShop { get; }

    /// <summary>Current device registration, or null if not paired.</summary>
    DeviceRegistration? CurrentDevice { get; }

    /// <summary>
    /// Exchange a one-time magic-link token for a long-lived device token.
    /// </summary>
    /// <param name="pairingToken">The <c>token</c> query-param of the magic link.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The persisted device token, plus the device registration record.</returns>
    Task<PairingResult> PairAsync(string pairingToken, CancellationToken cancellationToken = default);

    /// <summary>Refresh the device token if a refresh token is available.</summary>
    Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Heartbeat: updates LastSeenAt on the backend.</summary>
    Task<HeartbeatResult> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch the next batch of pending jobs for this device.
    /// The companion is responsible for acking each one via
    /// <see cref="AcknowledgeJobAsync"/> so the backend can move it off its queue.
    /// </summary>
    Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledge that the companion has received and persisted a job.
    /// Must be called once per <paramref name="jobId"/> before processing starts.
    /// The backend will not redeliver an acked job unless it is explicitly requeued.
    /// </summary>
    Task AcknowledgeJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Report a job's terminal status. Set exactly one of <paramref name="printedAt"/> /
    /// <paramref name="failedAt"/>. Idempotent on the backend per (jobId, final-state).
    /// </summary>
    Task ReportJobStatusAsync(
        string jobId,
        DateTimeOffset? printedAt,
        DateTimeOffset? failedAt,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download the PDF bytes for a job. Honours an internal short-lived cache
    /// keyed by <paramref name="jobId"/> and <paramref name="expectedSha256"/>
    /// to detect mismatches between the signed URL and the bytes returned.
    /// </summary>
    Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>Revoke (locally) the device token. Clears the in-memory and persisted credentials.</summary>
    Task RevokeDeviceAsync(CancellationToken cancellationToken = default);
}

public sealed record PairingResult(
    DeviceToken Token,
    DeviceRegistration Registration,
    ShopInfo Shop);

public sealed record HeartbeatResult(
    DateTimeOffset ServerTime,
    bool TokenStillValid,
    string? RecommendedBackendVersion);

public sealed record DownloadedPdf(
    string LocalPath,
    string Sha256,
    long SizeBytes);
