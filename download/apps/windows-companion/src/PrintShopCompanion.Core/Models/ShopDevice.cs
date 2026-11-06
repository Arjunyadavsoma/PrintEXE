namespace PrintShopCompanion.Core.Models;

/// <summary>
/// A print shop the companion is paired with. One companion at a time is paired to one shop.
/// </summary>
public sealed record ShopInfo
{
    public required string ShopId { get; init; }
    public required string ShopName { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string ApiBasePath { get; init; } = "/api/companion/v1";
}

/// <summary>
/// Long-lived device token. Stored encrypted with DPAPI (current-user scope).
/// </summary>
public sealed record DeviceToken
{
    public required string ShopId { get; init; }
    public required string DeviceId { get; init; }
    public required string Token { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? RefreshToken { get; init; }
}

/// <summary>
/// Device registration record kept in sync with the PrintShop backend's
/// <c>PrintAgent</c> table. The companion never invents a device ID; it
/// always mints a fresh GUID on first launch and registers it via pairing.
/// </summary>
public sealed record DeviceRegistration
{
    public required string DeviceId { get; init; }
    public required string ShopId { get; init; }
    public required string Hostname { get; init; }
    public required string OperatingSystem { get; init; }
    public required string AppVersion { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public bool Revoked { get; init; }
}
