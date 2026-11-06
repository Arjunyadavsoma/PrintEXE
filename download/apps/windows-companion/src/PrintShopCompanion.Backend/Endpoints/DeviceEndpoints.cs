namespace PrintShopCompanion.Backend.Endpoints;

using Microsoft.AspNetCore.Http;
using PrintShopCompanion.Backend.Data;
using PrintShopCompanion.Shared.Constants;
using PrintShopCompanion.Shared.Dto;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/companion/v1/heartbeat", async (HttpRequest req, MockStore store) =>
        {
            if (!IsAuthorized(req, store, out var device)) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            store.Devices[device.DeviceId] = device with { LastSeenAt = now };
            return Results.Ok(new HeartbeatResponseDto
            {
                ServerTime = now,
                TokenStillValid = true,
                RecommendedBackendVersion = "0.1.0"
            });
        });

        app.MapPost("/api/companion/v1/device/revoke", (HttpRequest req, MockStore store) =>
        {
            if (!IsAuthorized(req, store, out var device)) return Results.Unauthorized();
            store.Devices.TryRemove(device.DeviceId, out _);
            return Results.Ok();
        });
    }

    public static bool IsAuthorized(HttpRequest req, MockStore store, out DeviceRecord device)
    {
        var token = req.Headers[CompanionProtocol.DeviceTokenHeader].ToString();
        var devId = req.Headers[CompanionProtocol.DeviceIdHeader].ToString();
        device = null!;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(devId)) return false;
        if (!store.Devices.TryGetValue(devId, out var rec)) return false;
        if (!string.Equals(rec.DeviceToken, token, StringComparison.Ordinal)) return false;
        device = rec;
        return true;
    }
}
