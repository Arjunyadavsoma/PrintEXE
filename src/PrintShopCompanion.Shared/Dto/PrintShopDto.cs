namespace PrintShopCompanion.Shared.Dto;

using System.Text.Json.Serialization;

/// <summary>Wire DTO for the data field of POST /api/v1/auth/login.</summary>
public sealed class LoginResponseDto
{
    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("expiresIn")]
    public required long ExpiresIn { get; init; }

    [JsonPropertyName("user")]
    public required UserDto User { get; init; }
}

public sealed class RefreshResponseDto
{
    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public required string RefreshToken { get; init; }
}

public sealed class UserDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("shopId")]
    public string? ShopId { get; init; }
}

/// <summary>Wire DTO for the data field of GET /api/v1/shops/me.</summary>
public sealed class ShopDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("upiId")]
    public string? UpiId { get; init; }

    [JsonPropertyName("paymentProvider")]
    public string? PaymentProvider { get; init; }

    [JsonPropertyName("pricingConfig")]
    public string? PricingConfig { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    [JsonPropertyName("subscriptionTier")]
    public string? SubscriptionTier { get; init; }

    [JsonPropertyName("subscriptionExpiresAt")]
    public DateTimeOffset? SubscriptionExpiresAt { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("businessHours")]
    public string? BusinessHours { get; init; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; init; }

    [JsonPropertyName("servicesJson")]
    public string? ServicesJson { get; init; }

    [JsonPropertyName("paymentPhone")]
    public string? PaymentPhone { get; init; }

    [JsonPropertyName("paymentInstructions")]
    public string? PaymentInstructions { get; init; }

    [JsonPropertyName("cashOnPickup")]
    public bool CashOnPickup { get; init; }
}

/// <summary>Wire DTO for an Order row in GET /shops/me/orders.</summary>
public sealed class OrderDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("shopId")]
    public required string ShopId { get; init; }

    [JsonPropertyName("customerToken")]
    public required string CustomerToken { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("totalAmountPaise")]
    public required long TotalAmountPaise { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "INR";

    [JsonPropertyName("paymentProvider")]
    public string? PaymentProvider { get; init; }

    [JsonPropertyName("paymentTxnId")]
    public string? PaymentTxnId { get; init; }

    [JsonPropertyName("paymentUtr")]
    public string? PaymentUtr { get; init; }

    [JsonPropertyName("paidAt")]
    public DateTimeOffset? PaidAt { get; init; }

    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("tokenNumber")]
    public string? TokenNumber { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("files")]
    public List<OrderFileDto>? Files { get; init; }
}

public sealed class OrderFileDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("orderId")]
    public required string OrderId { get; init; }

    [JsonPropertyName("originalFilename")]
    public required string OriginalFilename { get; init; }

    [JsonPropertyName("storageKey")]
    public string? StorageKey { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("pageCount")]
    public int? PageCount { get; init; }

    [JsonPropertyName("copies")]
    public int Copies { get; init; }

    [JsonPropertyName("colorMode")]
    public string ColorMode { get; init; } = "bw";

    [JsonPropertyName("pageRange")]
    public string? PageRange { get; init; }

    [JsonPropertyName("paperSize")]
    public string PaperSize { get; init; } = "a4";

    [JsonPropertyName("duplex")]
    public bool Duplex { get; init; }

    [JsonPropertyName("pricePaise")]
    public long PricePaise { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("printedAt")]
    public DateTimeOffset? PrintedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Wire DTO for GET /shops/me/analytics.</summary>
public sealed class AnalyticsDto
{
    [JsonPropertyName("revenue")]
    public required RevenueDto Revenue { get; init; }

    [JsonPropertyName("orderCount")]
    public required OrderCountDto OrderCount { get; init; }

    [JsonPropertyName("byStatus")]
    public required Dictionary<string, int> ByStatus { get; init; }

    [JsonPropertyName("popular")]
    public required PopularDto Popular { get; init; }

    [JsonPropertyName("peakHourUtc")]
    public int? PeakHourUtc { get; init; }

    [JsonPropertyName("hours")]
    public required List<int> Hours { get; init; }
}

public sealed class RevenueDto
{
    [JsonPropertyName("totalPaise")]
    public required long TotalPaise { get; init; }

    [JsonPropertyName("formatted")]
    public required string Formatted { get; init; }
}

public sealed class OrderCountDto
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("paid")]
    public int Paid { get; init; }
}

public sealed class PopularDto
{
    [JsonPropertyName("color")]
    public int Color { get; init; }

    [JsonPropertyName("bw")]
    public int Bw { get; init; }

    [JsonPropertyName("duplex")]
    public int Duplex { get; init; }

    [JsonPropertyName("simplex")]
    public int Simplex { get; init; }

    [JsonPropertyName("a4")]
    public int A4 { get; init; }

    [JsonPropertyName("a3")]
    public int A3 { get; init; }

    [JsonPropertyName("other")]
    public int Other { get; init; }
}

public sealed class DailyReportRowDto
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("orders")]
    public int Orders { get; init; }

    [JsonPropertyName("revenuePaise")]
    public long RevenuePaise { get; init; }

    [JsonPropertyName("files")]
    public int Files { get; init; }

    [JsonPropertyName("byStatus")]
    public required Dictionary<string, int> ByStatus { get; init; }
}

/// <summary>Wire DTO for the data field of POST /api/v1/agents/register.</summary>
public sealed class RegisterAgentResponseDto
{
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("deviceToken")]
    public required string DeviceToken { get; init; }

    [JsonPropertyName("shopId")]
    public required string ShopId { get; init; }

    [JsonPropertyName("shopName")]
    public required string ShopName { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}

public sealed class AgentDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; init; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset? LastSeenAt { get; init; }

    [JsonPropertyName("printerName")]
    public string? PrinterName { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("os")]
    public string? Os { get; init; }
}

/// <summary>The standard envelope every API response uses (from src/lib/api/envelope.ts).</summary>
public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("error")]
    public ApiError? Error { get; init; }

    [JsonPropertyName("pagination")]
    public PaginationDto? Pagination { get; init; }
}

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class PaginationDto
{
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }
}

/// <summary>Wire DTO for a print-agent job (from GET /agents/jobs/pending).</summary>
public sealed class PrintJobDto
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("fileId")]
    public required string FileId { get; init; }

    [JsonPropertyName("orderId")]
    public required string OrderId { get; init; }

    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("downloadUrl")]
    public required string DownloadUrl { get; init; }

    [JsonPropertyName("copies")]
    public int Copies { get; init; }

    [JsonPropertyName("colorMode")]
    public string ColorMode { get; init; } = "bw";

    [JsonPropertyName("duplex")]
    public bool Duplex { get; init; }

    [JsonPropertyName("paperSize")]
    public string PaperSize { get; init; } = "a4";

    [JsonPropertyName("pageRange")]
    public string? PageRange { get; init; }
}

/// <summary>Response envelope for GET /agents/jobs/pending (envelope-wrapped).</summary>
public sealed class PendingJobsEnvelopeDto
{
    [JsonPropertyName("jobs")]
    public required List<PrintJobDto> Jobs { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }
}

/// <summary>
/// Wire DTO for POST /api/v1/agents/token response body (inside the
/// envelope's <c>data</c> field). Used to exchange a device token for a
/// short-lived JWT for Socket.IO auth.
/// </summary>
public sealed class AgentTokenResponseDto
{
    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("shopId")]
    public string? ShopId { get; init; }
}

/// <summary>Wire DTO for POST /api/v1/agents/heartbeat response body.</summary>
public sealed class AgentHeartbeatResponseDto
{
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("online")]
    public bool Online { get; init; }

    [JsonPropertyName("ts")]
    public DateTimeOffset? Ts { get; init; }
}

/// <summary>
/// Wire DTO for POST /api/v1/agents/jobs/{id}/status request body.
/// <para><see cref="Status"/> is the string <c>"printing"</c>, <c>"completed"</c>, or <c>"failed"</c>.</para>
/// <para><see cref="PrinterName"/> is required when claiming (status = "printing").</para>
/// <para><see cref="LastError"/> is required when failing (status = "failed").</para>
/// </summary>
public sealed class AgentJobStatusRequestDto
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("printerName")]
    public string? PrinterName { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}

/// <summary>
/// Wire DTO for POST /api/v1/agents/jobs/{id}/status response body.
/// <para><see cref="Applied"/> is true if the status change was applied. False if skipped (e.g. another agent claimed it first).</para>
/// <para><see cref="Skipped"/> is true if the status change was a no-op (already in the target state, or the precondition didn't hold).</para>
/// </summary>
public sealed class AgentJobStatusResponseDto
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("applied")]
    public bool Applied { get; init; }

    [JsonPropertyName("skipped")]
    public bool Skipped { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Wire DTO for GET /api/v1/sync/pull response body.</summary>
public sealed class SyncPullResponseDto
{
    [JsonPropertyName("changes")]
    public required List<SyncChangeDto> Changes { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

public sealed class SyncChangeDto
{
    [JsonPropertyName("entity")]
    public required string Entity { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("data")]
    public System.Text.Json.JsonElement Data { get; init; }

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Wire DTO for POST /api/v1/sync/push request body.</summary>
public sealed class SyncPushRequestDto
{
    [JsonPropertyName("changes")]
    public required List<SyncPushChangeDto> Changes { get; init; }
}

public sealed class SyncPushChangeDto
{
    [JsonPropertyName("entity")]
    public required string Entity { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("data")]
    public System.Text.Json.JsonElement Data { get; init; }

    [JsonPropertyName("clientRevision")]
    public int ClientRevision { get; init; }

    [JsonPropertyName("baseRevision")]
    public int BaseRevision { get; init; }
}

public sealed class SyncPushResponseDto
{
    [JsonPropertyName("results")]
    public required List<SyncPushResultDto> Results { get; init; }
}

public sealed class SyncPushResultDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("serverRevision")]
    public int? ServerRevision { get; init; }

    [JsonPropertyName("conflict")]
    public bool Conflict { get; init; }
}

/// <summary>Wire DTO for GET /api/v1/sync/status response body.</summary>
public sealed class SyncStatusResponseDto
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("shopId")]
    public string? ShopId { get; init; }

    [JsonPropertyName("syncCursor")]
    public string? SyncCursor { get; init; }

    [JsonPropertyName("lastSyncAt")]
    public DateTimeOffset? LastSyncAt { get; init; }

    [JsonPropertyName("agentName")]
    public string? AgentName { get; init; }

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; init; }
}

/// <summary>
/// Wire DTO for POST /api/v1/agents/jobs/{jobId}/received request body.
/// Per the actual backend zod schema:
///   checksum: SHA-256 hex string (64 hex chars, NO 'sha256:' prefix) — optional
///   fileSize: positive int — optional
///   receivedAt: ISO datetime string — optional (defaults to now on server)
///   deliveryAttemptId: UUID — optional
///   transferId: UUID — optional (for future WebRTC)
/// </summary>
public sealed class JobReceivedRequestDto
{
    [JsonPropertyName("checksum")]
    public string? Checksum { get; init; }

    [JsonPropertyName("fileSize")]
    public long? FileSize { get; init; }

    [JsonPropertyName("receivedAt")]
    public string? ReceivedAt { get; init; }

    [JsonPropertyName("deliveryAttemptId")]
    public string? DeliveryAttemptId { get; init; }

    [JsonPropertyName("transferId")]
    public string? TransferId { get; init; }
}

/// <summary>
/// Wire DTO for POST /api/v1/agents/jobs/{jobId}/received response body.
/// Per the actual backend response:
///   { jobId, received, cleanupScheduled, cleanupScheduledAt?, message }
/// </summary>
public sealed class JobReceivedResponseDto
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("received")]
    public bool Received { get; init; }

    [JsonPropertyName("cleanupScheduled")]
    public bool CleanupScheduled { get; init; }

    [JsonPropertyName("cleanupScheduledAt")]
    public DateTimeOffset? CleanupScheduledAt { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// v1 contract: pairing-ticket exchange (POST /api/v1/companion/pair)
// See docs/integration/COMPANION_V1_CONTRACT.md §2. Auth: NONE (ticket IS the auth).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wire DTO for POST /api/v1/companion/pair request body. The pairing
/// ticket is the authorization (no Bearer header). The backend consumes
/// the ticket atomically (status active → consumed) and returns the
/// device token exactly once.
/// </summary>
public sealed class PairTicketRequestDto
{
    [JsonPropertyName("ticket")]
    public required string Ticket { get; init; }          // 64 hex chars

    /// <summary>Device display name. Actual SaaS zod cap is 120 chars (not 255 — Phase 3 fix).</summary>
    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }     // 1-120 chars

    /// <summary>Companion semver. Actual SaaS zod cap is 40 chars.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }                // optional, max 40 chars

    [JsonPropertyName("os")]
    public string? Os { get; init; }                     // optional, max 40 chars

    [JsonPropertyName("printerName")]
    public string? PrinterName { get; init; }            // optional, max 200 chars
}

/// <summary>
/// Wire DTO for POST /api/v1/companion/pair response body (inside the
/// envelope's data field). The deviceToken is 64 hex chars, returned exactly
/// once — the backend stores only HMAC-SHA256("print-agent-salt", token).
/// protocolVersion is 1 for this contract.
/// </summary>
public sealed class PairTicketResponseDto
{
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("deviceToken")]
    public required string DeviceToken { get; init; }   // 64 hex chars

    [JsonPropertyName("shopId")]
    public required string ShopId { get; init; }

    [JsonPropertyName("shopName")]
    public required string ShopName { get; init; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = 1;
}

// ─────────────────────────────────────────────────────────────────────────────
// v1 contract: remote command polling + ack/result (GET/POST /api/v1/companion/commands)
// See docs/integration/COMPANION_V1_CONTRACT.md §6 + §7. Auth: Bearer device token.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wire DTO for a pending command returned by GET /api/v1/companion/commands.
/// Phase 3 fix: the actual SaaS route returns commands with field
/// <c>commandId</c> (NOT <c>id</c>), <c>payload</c> as a parsed JSON OBJECT
/// (NOT a JSON string), and an <c>issuedAt</c> field. The published contract
/// drifted from the implementation here — see docs/PHASE3_CONTRACT_MATRIX.md
/// Mismatch #1. Only commands with status='requested' AND expiresAt > now
/// are returned; the companion still validates <c>ExpiresAt</c> locally
/// before executing (defense in depth).
/// </summary>
public sealed class PendingCommandDto
{
    /// <summary>Server field is <c>commandId</c> (NOT <c>id</c> per the published contract).</summary>
    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }      // uuid

    [JsonPropertyName("commandType")]
    public required string CommandType { get; init; } // refresh_printers | pause | resume | cancel_job | retry_job | test_print | diagnostics

    /// <summary>
    /// The command payload as a parsed JSON object (the actual SaaS does
    /// <c>JSON.parse(payload ?? '{}')</c> server-side, so this arrives as an
    /// object, not a string). Null/garbage → empty object. We keep the raw
    /// <see cref="JsonElement"/> so the executor can parse job-specific fields
    /// (e.g. <c>jobId</c> for cancel_job/retry_job) without a round-trip.
    /// </summary>
    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement Payload { get; set; }

    /// <summary>ISO8601 of the command's <c>createdAt</c>. Added Phase 3 — the published contract omitted this but the server sends it.</summary>
    [JsonPropertyName("issuedAt")]
    public DateTimeOffset? IssuedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Response envelope for GET /api/v1/companion/commands. Phase 3: also carries <c>agentId</c>.</summary>
public sealed class PendingCommandsEnvelopeDto
{
    /// <summary>The agent id the commands belong to (the authenticated device). Useful as a sanity check.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("commands")]
    public required List<PendingCommandDto> Commands { get; init; }
}

/// <summary>
/// Wire DTO for POST /api/v1/companion/commands request body — acknowledge
/// receipt and/or report execution result. Status values per the actual SaaS
/// zod schema: <c>delivered, accepted, executing, succeeded, failed</c>.
/// <para><b>Phase 3 fix:</b> the <c>result</c> field is an OBJECT
/// (<c>z.record(z.string(), z.unknown())</c>), NOT a JSON string. The
/// executor produces a JSON string; the backend client parses it back to a
/// <see cref="System.Text.Json.JsonElement"/> before sending. Sending a string
/// would fail zod with 400 VALIDATION_ERROR on every result report.</para>
/// </summary>
public sealed class ReportCommandRequestDto
{
    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }        // delivered|accepted|executing|succeeded|failed

    /// <summary>Structured result as a JSON OBJECT (not a string). Omit for non-terminal statuses.</summary>
    [JsonPropertyName("result")]
    public System.Text.Json.JsonElement? Result { get; init; }
}

/// <summary>
/// Response for POST /api/v1/companion/commands. Phase 3 fix: the actual SaaS
/// returns <c>{commandId, status, updatedAt}</c> — NOT <c>{applied}</c> as the
/// published contract stated. See docs/PHASE3_CONTRACT_MATRIX.md Mismatch #3.
/// </summary>
public sealed class ReportCommandResponseDto
{
    [JsonPropertyName("commandId")]
    public string? CommandId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Response of POST /api/v1/companion/token/rotate (device credential
/// rotation). The new <c>deviceToken</c> is returned ONCE — the backend
/// stores only its HMAC verifier. The previous token keeps working until
/// <c>overlapExpiresAt</c> (10 minutes), then is rejected.
/// </summary>
public sealed class TokenRotationResponseDto
{
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("deviceToken")]
    public string DeviceToken { get; init; } = "";

    [JsonPropertyName("rotatedAt")]
    public DateTimeOffset? RotatedAt { get; init; }

    [JsonPropertyName("overlapExpiresAt")]
    public DateTimeOffset? OverlapExpiresAt { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}
