namespace PrintShopCompanion.Shared.Dto;

using System.Text.Json.Serialization;

/// <summary>Wire DTO for a print job delivered by the backend.</summary>
public sealed class PrintJobDto
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("shopId")]
    public required string ShopId { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = "PrintShop Job";

    [JsonPropertyName("printerName")]
    public string? PrinterName { get; init; }

    [JsonPropertyName("copies")]
    public int Copies { get; init; } = 1;

    [JsonPropertyName("pageRange")]
    public string? PageRange { get; init; }

    [JsonPropertyName("paperSize")]
    public string? PaperSize { get; init; }

    [JsonPropertyName("color")]
    public bool? Color { get; init; }

    [JsonPropertyName("duplex")]
    public bool? Duplex { get; init; }

    [JsonPropertyName("pdfUrl")]
    public Uri? PdfUrl { get; init; }

    [JsonPropertyName("pdfSha256")]
    public string? PdfSha256 { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Wire DTO for pairing-token exchange.</summary>
public sealed class PairingRequestDto
{
    [JsonPropertyName("pairingToken")]
    public required string PairingToken { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("hostname")]
    public required string Hostname { get; init; }

    [JsonPropertyName("operatingSystem")]
    public required string OperatingSystem { get; init; }

    [JsonPropertyName("appVersion")]
    public required string AppVersion { get; init; }
}

public sealed class PairingResponseDto
{
    [JsonPropertyName("shopId")]
    public required string ShopId { get; init; }

    [JsonPropertyName("shopName")]
    public required string ShopName { get; init; }

    [JsonPropertyName("baseUrl")]
    public required Uri BaseUrl { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("deviceToken")]
    public required string DeviceToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("registeredAt")]
    public DateTimeOffset RegisteredAt { get; init; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; init; }
}

public sealed class HeartbeatResponseDto
{
    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; init; }

    [JsonPropertyName("tokenStillValid")]
    public bool TokenStillValid { get; init; }

    [JsonPropertyName("recommendedBackendVersion")]
    public string? RecommendedBackendVersion { get; init; }
}

public sealed class JobStatusReportDto
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("printedAt")]
    public DateTimeOffset? PrintedAt { get; init; }

    [JsonPropertyName("failedAt")]
    public DateTimeOffset? FailedAt { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("spoolerJobId")]
    public int? SpoolerJobId { get; init; }

    [JsonPropertyName("pdfSha256")]
    public string? PdfSha256 { get; init; }
}

public sealed class AckJobRequestDto
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("acceptedAt")]
    public DateTimeOffset AcceptedAt { get; init; }
}
