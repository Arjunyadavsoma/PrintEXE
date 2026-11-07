namespace PrintShopCompanion.Core.Models;

/// <summary>
/// The shopkeeper's session, mirroring the real <c>POST /api/v1/auth/login</c>
/// response. The access token is short-lived (~15 min); the refresh token is
/// long-lived (~7 days). Both are stored DPAPI-encrypted at
/// <c>%AppData%\PrintShop\Companion\device.dat</c> alongside the device token.
/// </summary>
public sealed record ShopSession
{
    /// <summary>JWT access token — sent as <c>Authorization: Bearer ...</c> on every shopkeeper API call.</summary>
    public required string AccessToken { get; init; }

    /// <summary>JWT refresh token — used to mint a new access token when it expires.</summary>
    public required string RefreshToken { get; init; }

    /// <summary>Access token lifetime in seconds (from the JWT exp claim).</summary>
    public required long ExpiresIn { get; init; }

    /// <summary>UTC instant the access token expires (computed from ExpiresIn).</summary>
    public required DateTimeOffset AccessExpiresAt { get; init; }

    /// <summary>The shopkeeper's user record.</summary>
    public required ShopkeeperUser User { get; init; }

    /// <summary>UTC instant the session was issued.</summary>
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>The shopkeeper's user record, from <c>POST /auth/login</c> or <c>GET /me</c>.</summary>
public sealed record ShopkeeperUser
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }  // "owner" | "admin"
    public string? ShopId { get; init; }        // null for admin
}

/// <summary>
/// The shop profile, from <c>GET /api/v1/shops/me</c>. The shopkeeper's own
/// shop. Used by the Dashboard header and the Settings → Shop Management screen.
/// </summary>
public sealed record ShopInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? UpiId { get; init; }
    public string? PaymentProvider { get; init; }
    public string? PricingConfig { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Timezone { get; init; } = "Asia/Kolkata";
    public bool IsActive { get; init; } = true;
    public string? SubscriptionTier { get; init; } = "free";
    public DateTimeOffset? SubscriptionExpiresAt { get; init; }
    public string? Description { get; init; }
    public string? BusinessHours { get; init; }
    public string? LogoUrl { get; init; }
    public string? ServicesJson { get; init; }
    public string? PaymentPhone { get; init; }
    public string? PaymentInstructions { get; init; }
    public bool CashOnPickup { get; init; }
}

/// <summary>
/// Long-lived device token issued by <c>POST /api/v1/agents/register</c>.
/// Used by the background print engine to authenticate job-related calls
/// (heartbeat, pending jobs, status reports). Stored DPAPI-encrypted at
/// <c>%AppData%\PrintShop\Companion\device.dat</c> alongside the shop session.
/// </summary>
public sealed record DeviceToken
{
    public required string AgentId { get; init; }
    public required string ShopId { get; init; }
    public required string Token { get; init; }
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Combined persisted credential bundle. Stored DPAPI-encrypted on disk.</summary>
public sealed record StoredCredentials
{
    public ShopSession? Session { get; init; }
    public DeviceToken? DeviceToken { get; init; }
}
