namespace PrintShopCompanion.Core.Contracts;

using PrintShopCompanion.Core.Models;

/// <summary>
/// The shopkeeper-facing backend. Authenticated via the shopkeeper's JWT
/// access token (issued by <c>POST /api/v1/auth/login</c>). This is the
/// primary interface for the Dashboard, Orders, Order Details, Payments,
/// Reports, and Settings screens.
/// </summary>
public interface IShopBackend
{
    /// <summary>True if we have a non-expired access token (or a refreshable one).</summary>
    bool IsAuthenticated { get; }

    /// <summary>The shopkeeper's user record (id, email, role, shopId). Null if not signed in.</summary>
    ShopkeeperUser? CurrentUser { get; }

    /// <summary>The shop profile (from GET /shops/me). Null if not yet fetched.</summary>
    ShopInfo? CurrentShop { get; }

    // ── Authentication ──────────────────────────────────────────────

    /// <summary>Sign in with email + password. Throws on 401 / rate-limit.</summary>
    Task<ShopSession> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Refresh the access token using the refresh token.</summary>
    Task<ShopSession> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Re-fetch the current user from GET /me — validates the access token.</summary>
    Task<ShopkeeperUser> GetCurrentUserAsync(CancellationToken ct = default);

    // ── Shop profile ───────────────────────────────────────────────

    /// <summary>GET /api/v1/shops/me</summary>
    Task<ShopInfo> GetShopAsync(CancellationToken ct = default);

    /// <summary>PATCH /api/v1/shops/me</summary>
    Task<ShopInfo> UpdateShopAsync(IDictionary<string, object?> patch, CancellationToken ct = default);

    // ── Orders ──────────────────────────────────────────────────────

    /// <summary>GET /api/v1/shops/me/orders?status=&amp;limit=&amp;offset=</summary>
    Task<(IReadOnlyList<OrderRecord> Orders, int Total)> ListOrdersAsync(
        OrderStatus? status = null, int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>GET /api/v1/shops/me/orders/{id}</summary>
    Task<OrderRecord> GetOrderAsync(string orderId, CancellationToken ct = default);

    /// <summary>PATCH /api/v1/shops/me/orders/{id} (currently only notes).</summary>
    Task<OrderRecord> UpdateOrderNotesAsync(string orderId, string? notes, CancellationToken ct = default);

    /// <summary>POST /api/v1/shops/me/orders/{id}/start — paid → printing.</summary>
    Task<OrderRecord> StartOrderAsync(string orderId, CancellationToken ct = default);

    /// <summary>POST /api/v1/shops/me/orders/{id}/complete — ready → collected (deletes files).</summary>
    Task<OrderRecord> CompleteOrderAsync(string orderId, CancellationToken ct = default);

    /// <summary>POST /api/v1/shops/me/orders/{id}/cancel — * → cancelled.</summary>
    Task<OrderRecord> CancelOrderAsync(string orderId, CancellationToken ct = default);

    /// <summary>GET /api/v1/shops/me/orders/{id}/files/{fileId}/download — returns the raw PDF bytes.</summary>
    Task<Stream> DownloadFileAsync(string orderId, string fileId, CancellationToken ct = default);

    // ── Analytics + reports ────────────────────────────────────────

    /// <summary>GET /api/v1/shops/me/analytics</summary>
    Task<ShopAnalytics> GetAnalyticsAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/shops/me/reports/daily?from=&amp;to=</summary>
    Task<IReadOnlyList<DailyReportRow>> GetDailyReportsAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    // ── Devices (print agents) ────────────────────────────────────

    /// <summary>POST /api/v1/agents/register — mints a new device token for this shop.</summary>
    Task<DeviceToken> RegisterDeviceAsync(string name, string? os = null, string? version = null, string? printerName = null, CancellationToken ct = default);

    /// <summary>GET /api/v1/shops/me/agents — lists all devices for this shop.</summary>
    Task<IReadOnlyList<DeviceRegistration>> ListDevicesAsync(CancellationToken ct = default);

    /// <summary>Sign out — clears the in-memory session. The persisted credentials file is NOT deleted (the shopkeeper may re-login).</summary>
    Task SignOutAsync(CancellationToken ct = default);
}
