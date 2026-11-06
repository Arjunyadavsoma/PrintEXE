namespace PrintShopCompanion.Backend.Endpoints;

using Microsoft.AspNetCore.Http;
using PrintShopCompanion.Backend.Data;
using PrintShopCompanion.Shared.Dto;

public static class PairingEndpoints
{
    public static void MapPairingEndpoints(this WebApplication app)
    {
        // Generate a one-time pairing token. In production this is signed by the
        // backend with its private key; here we mint a random opaque string.
        app.MapPost("/api/companion/v1/pairing/begin", (MockStore store) =>
        {
            var token = Guid.NewGuid().ToString("N");
            store.PendingPairings[token] = store.ShopId;
            return Results.Ok(new
            {
                pairingToken = token,
                magicLink = $"printshopcompanion://pair?token={token}&shop={store.ShopId}&expires={DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}",
                shopId = store.ShopId,
                shopName = store.ShopName
            });
        });

        app.MapPost("/api/companion/v1/pair", (PairingRequestDto req, MockStore store) =>
        {
            if (!store.PendingPairings.TryRemove(req.PairingToken, out var shopId))
                return Results.BadRequest(new { error = "invalid_or_expired_token" });

            var deviceToken = Guid.NewGuid().ToString("N");
            var deviceId = req.DeviceId;
            var now = DateTimeOffset.UtcNow;
            store.Devices[deviceId] = new DeviceRecord(
                deviceId, shopId, req.Hostname, req.OperatingSystem, req.AppVersion,
                deviceToken, now, now);

            return Results.Ok(new PairingResponseDto
            {
                ShopId = shopId,
                ShopName = store.ShopName,
                BaseUrl = new Uri("https://localhost:5241"),
                DeviceId = deviceId,
                DeviceToken = deviceToken,
                RefreshToken = Guid.NewGuid().ToString("N"),
                IssuedAt = now,
                ExpiresAt = now.AddYears(1),
                RegisteredAt = now,
                LastSeenAt = now
            });
        });

        app.MapPost("/api/companion/v1/refresh", (HttpRequest req, MockStore store) =>
        {
            var refresh = req.Headers["X-PrintShop-Refresh-Token"].ToString();
            if (string.IsNullOrEmpty(refresh)) return Results.BadRequest();
            // Issue a fresh token bound to the same device (best-effort mock).
            return Results.Ok(new PairingResponseDto
            {
                ShopId = store.ShopId,
                ShopName = store.ShopName,
                BaseUrl = new Uri("https://localhost:5241"),
                DeviceId = "any",
                DeviceToken = Guid.NewGuid().ToString("N"),
                RefreshToken = refresh,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
                RegisteredAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        });
    }
}
