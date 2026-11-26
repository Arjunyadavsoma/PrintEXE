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
    /// v0.7.2 sync: the agent id the SERVER assigned (the PrintAgent uuid,
    /// returned by pair / heartbeat / pending-jobs responses). Socket.IO
    /// <c>agent.revoked</c>, <c>device.revoked</c> and
    /// <c>device.command.issued</c> payloads carry THIS id — not the local
    /// <c>ThisApp.DeviceId</c> GUID, which never matched and made revocation
    /// events silently unnoticeable before v0.7.2. Default null (test fakes);
    /// the HTTP client captures it on every authenticated call.
    /// </summary>
    string? ServerAgentId => null;

    /// <summary>Store the device token obtained from the web dashboard. The shopkeeper pastes the token into PrintEXE;
    /// PrintEXE stores it (DPAPI-encrypted) and uses it as the Bearer
    /// credential on every subsequent call.
    /// <para>This replaces the v0.4.6 <c>PairAsync</c> magic-link flow —
    /// the production backend uses paste-token, not magic-link.</para>
    /// </summary>
    Task PairWithDeviceTokenAsync(string deviceToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// v1 contract §2 — exchange a short-lived pairing ticket for a long-lived
    /// device token. The ticket is obtained by the shopkeeper on the website
    /// (<c>POST /companion/pairing/begin</c>, shopkeeper JWT) and pasted into
    /// PrintEXE (or delivered via the <c>printshop://pair?ticket=...</c> deep
    /// link). This method sends <c>POST /api/v1/companion/pair</c> with no
    /// Authorization header — the ticket IS the authorization.
    /// <para>On success, the returned <see cref="PairResult.DeviceToken"/> is
    /// DPAPI-persisted (via <see cref="PairWithDeviceTokenAsync"/>) before this
    /// method returns, so a crash immediately after cannot lose the credential.</para>
    /// <para>Throws <see cref="PairingTicketException"/> for the contract's
    /// ticket-specific error codes (TICKET_EXPIRED, TICKET_CONSUMED, VALIDATION_ERROR,
    /// UNAUTHORIZED) so the UI can map each to a distinct user-facing state.</para>
    /// </summary>
    Task<PairResult> PairWithTicketAsync(
        string ticket,
        string deviceName,
        string? version = null,
        string? os = null,
        string? printerName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only shop snapshot pulled with the DEVICE credential
    /// (<c>GET /api/v1/companion/shop-snapshot</c>). Used when the companion
    /// is paired but has no shopkeeper session: session-scoped
    /// <c>shops/me/*</c> endpoints are closed to device JWTs, so the device
    /// reads its own shop's queue through its device token (Universal Print
    /// model). Returns null when the token is rejected (revoked device).
    /// </summary>
    Task<ShopSnapshot?> GetShopSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// v1 contract §6 — fetch pending remote commands for this device.
    /// <c>GET /api/v1/companion/commands</c>. The backend returns only
    /// commands with <c>status='requested'</c> AND <c>expiresAt &gt; now</c>.
    /// The companion still validates <see cref="PendingCommand.IsExpired"/>
    /// locally before executing (defense in depth).
    /// </summary>
    Task<IReadOnlyList<PendingCommand>> FetchPendingCommandsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// v1 contract §7 — acknowledge receipt and/or report execution result.
    /// <c>POST /api/v1/companion/commands</c>. Idempotent on (commandId, status):
    /// the backend returns <c>applied:true</c> without re-processing.
    /// <para>Report <see cref="CommandStatus.Delivered"/> on receipt,
    /// <see cref="CommandStatus.Executing"/> when starting, then
    /// <see cref="CommandStatus.Succeeded"/> or <see cref="CommandStatus.Failed"/>
    /// on completion. <paramref name="resultJson"/> is a structured JSON string
    /// (e.g. <c>{"printers":["HP","Canon"]}</c>); omit for non-terminal statuses.</para>
    /// </summary>
    Task<CommandReportResult> ReportCommandStatusAsync(
        string commandId,
        string status,
        string? resultJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchange the stored device token for a short-lived JWT (15 min TTL).
    /// The JWT is used ONLY for Socket.IO auth (emit <c>auth {token: jwt}</c>
    /// after connecting). REST calls continue to use the device token
    /// directly. The JWT is HS256-signed by the backend and PrintEXE does
    /// NOT need to verify it — the Socket.IO server does that.
    /// </summary>
    Task<string> ExchangeDeviceTokenForJwtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Heartbeat: refreshes presence on the backend. Send every 15 seconds
    /// (the server marks the device offline after 30s of silence).
    /// <para><paramref name="printerName"/> carries the device's currently
    /// selected printer — the SaaS stores it on the agent row and shows it on
    /// the Devices page. Pass null to fall back to the configured default.</para>
    /// </summary>
    Task<HeartbeatResult> HeartbeatAsync(CancellationToken cancellationToken = default, string? printerName = null);

    /// <summary>
    /// Rotate the device credential: POST /api/v1/companion/token/rotate with
    /// the CURRENT token; the backend returns a NEW 64-hex device token ONCE
    /// and keeps accepting the old token for a short (10-minute) overlap so an
    /// in-flight request is not dropped mid-rotation.
    /// <para>The new token is DPAPI-persisted BEFORE the in-memory credential
    /// is switched (the spec's "persist before transition" rule); if the
    /// persist fails, the old token is kept and the rotation can be retried
    /// inside the overlap window.</para>
    /// </summary>
    Task<TokenRotationResult> RotateDeviceTokenAsync(CancellationToken cancellationToken = default);

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
    /// Durable receipt acknowledgement — confirms that the PDF has been
    /// downloaded, SHA-256 verified, and atomically persisted to the local
    /// encrypted SQLite queue. The backend uses this to:
    /// <list type="bullet">
    ///   <item>Set <c>deliveryStatus = "received"</c> on the PrintJob row.</item>
    ///   <item>Set <c>storageStatus = "local_verified"</c>.</item>
    ///   <item>Set <c>receivedAt = now</c> and <c>cloudDeleteAfter = now + 15 min</c>.</item>
    ///   <item>Schedule the cloud cleanup worker to delete the Supabase object.</item>
    /// </list>
    /// <para><b>Idempotent:</b> calling twice with the same checksum is a no-op
    /// (returns the existing <c>cloudDeleteAfter</c>). Calling with a different
    /// checksum returns 409 Conflict (the file has been tampered with).</para>
    /// <para><b>Atomic local persistence:</b> the companion must NOT call this
    /// until the local file has been atomically renamed from <c>.tmp</c> to
    /// <c>.pdf</c> — otherwise a crash between this call and the rename would
    /// cause the cloud copy to be deleted with no local copy.</para>
    /// </summary>
    Task<ReceiptAcknowledgement> MarkJobReceivedAsync(
        string jobId,
        string checksum,
        long fileSize,
        DateTimeOffset receivedAt,
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

/// <summary>
/// Result of POST /api/v1/companion/token/rotate. <see cref="DeviceToken"/>
/// is the NEW credential (returned once — the server stores only its HMAC
/// verifier). <see cref="OverlapExpiresAt"/> is when the OLD token stops
/// working (null if the server didn't report it).
/// </summary>
public sealed record TokenRotationResult(
    string DeviceToken,
    DateTimeOffset RotatedAt,
    DateTimeOffset? OverlapExpiresAt,
    string? Warning);

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

/// <summary>
/// Response from POST /agents/jobs/{id}/received. The companion uses
/// <see cref="CloudDeleteAfter"/> to display "Cloud copy will be deleted at
/// ..." in the UI, but the actual deletion is done by the server-side cron
/// worker — the companion never deletes cloud objects.
/// </summary>
public sealed record ReceiptAcknowledgement(
    string? JobId,
    string? DeliveryStatus,    // "received" on success
    string? StorageStatus,     // "local_verified" on success
    DateTimeOffset? CloudDeleteAfter,
    bool Skipped,              // true if the receipt was a no-op (already received)
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

// ────────────────────────── v1 contract exceptions ──────────────────────────

/// <summary>
/// Raised by <see cref="IPrintShopBackend.PairWithTicketAsync"/> when the
/// backend rejects the pairing ticket for one of the contract's
/// ticket-specific reasons. The <see cref="Code"/> maps to a distinct
/// user-facing state on the Pair Computer screen.
///
/// <para><b>Phase 3 correction</b> (see docs/PHASE3_CONTRACT_MATRIX.md Mismatch #4):
/// the actual SaaS returns these codes (NOT the published contract's
/// <c>TICKET_EXPIRED</c>/<c>TICKET_CONSUMED</c>):</para>
/// <list type="bullet">
///   <item><c>VALIDATION_ERROR</c> (400) — zod rejected the request body.</item>
///   <item><c>UNAUTHORIZED</c> (401) — ticket unknown, OR ticket expired
///   (the server deliberately uses the same code for both to prevent ticket
///   enumeration; the message distinguishes them).</item>
///   <item><c>PAIRING_TICKET_UNAVAILABLE</c> (409) — ticket exists but status
///   is not <c>active</c> (e.g. <c>revoked</c>).</item>
///   <item><c>PAIRING_TICKET_CONSUMED</c> (409) — another request won the
///   atomic consume race.</item>
///   <item><c>NOT_FOUND</c> (404) — the shop was deleted between <c>begin</c>
///   and <c>pair</c>.</item>
///   <item><c>RATE_LIMITED</c> (429) — more than 10 consumes/min from this IP.</item>
/// </list>
/// </summary>
public sealed class PairingTicketException : Exception
{
    public PairingTicketException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>The backend's error code. See the class remarks for the actual SaaS codes.</summary>
    public string Code { get; }

    /// <summary>True if this is a ticket-content error the user can fix by generating a new ticket.</summary>
    public bool IsTicketError => Code is
        "UNAUTHORIZED" or                              // unknown OR expired ticket
        "PAIRING_TICKET_UNAVAILABLE" or                // ticket status not active
        "PAIRING_TICKET_CONSUMED" or                   // lost the consume race
        "VALIDATION_ERROR";                            // malformed request

    /// <summary>True if the server message indicates the ticket expired (vs unknown). Phase 3 — the server uses UNAUTHORIZED for both, with the message distinguishing them.</summary>
    public bool IsExpired => Code == "UNAUTHORIZED" &&
        (Message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
         Message.Contains("has expired", StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the backend rate-limited the pair attempt (actual SaaS: 10 consumes/min from IP).</summary>
    public bool IsRateLimited => Code is "RATE_LIMITED";
}

/// <summary>
/// Raised by any authenticated companion-route call when the backend returns
/// 403 FORBIDDEN / <c>DEVICE_DISABLED</c> (v1 contract §3 + §14). Distinct from
/// the 401 UNAUTHORIZED case (revoked) — a disabled device still exists, its
/// token hash is valid, but the shop admin has suspended it. The companion
/// must NOT clear the credential (the admin may re-enable it); it should show
/// "This device is disabled. Contact your shop administrator." and stop
/// retrying. See docs/CONTRACT_V1_REVIEW.md §3.
/// </summary>
public sealed class DeviceDisabledException : Exception
{
    public DeviceDisabledException(string message) : base(message) { }
}
