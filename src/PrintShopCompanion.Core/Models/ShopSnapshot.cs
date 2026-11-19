namespace PrintShopCompanion.Core.Models;

/// <summary>
/// Read-only shop snapshot pulled with the DEVICE credential
/// (<c>GET /api/v1/companion/shop-snapshot</c>).
/// <para>
/// Used when the companion is PAIRED but has no shopkeeper session — e.g.
/// the shopkeeper paired via a pairing ticket and never signed in with
/// email+password. Session-scoped endpoints (<c>shops/me/*</c>) are closed to
/// device JWTs by design, so the device pulls its own shop's queue through
/// its device token instead (the Universal Print model: a registered device
/// sees the queue it serves, never a user session).
/// </para>
/// <para>Shapes mirror <c>shops/me</c> + <c>shops/me/orders</c> +
/// <c>shops/me/analytics</c> so the same store/mappers apply.</para>
/// </summary>
public sealed class ShopSnapshot
{
    public required string ShopName { get; init; }
    public ShopAnalytics? Analytics { get; init; }
    public required IReadOnlyList<OrderRecord> Orders { get; init; }
}
