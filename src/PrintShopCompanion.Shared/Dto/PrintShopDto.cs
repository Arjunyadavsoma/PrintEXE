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
