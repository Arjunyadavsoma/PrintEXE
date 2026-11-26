namespace PrintShopCompanion.Infrastructure.Backend;

using PrintShopCompanion.Core.Models;

/// <summary>
/// Shop-ownership validation for received print jobs (decision D-12, brief
/// §10: "Only authorized devices belonging to the correct shop may process
/// that shop's print jobs").
///
/// <para>The backend scopes <c>job.dispatch</c> and <c>GET /agents/jobs/pending</c>
/// to the device's shop (the Socket.IO JWT carries the shop claim, and the REST
/// token is device-scoped). Client-side this validator is defense in depth:
/// when BOTH the locally-known shop id (from pairing/heartbeat) and the
/// record's shop id are present, they must match — a mismatch means a
/// misrouted or forged dispatch and the job is rejected, never enqueued.
/// Unknown values are tolerated so older backends that omit shop ids keep
/// working (no invented contract fields).</para>
/// </summary>
public static class JobOwnershipValidator
{
    /// <summary>
    /// True when the record may be enqueued for this device. Pure function —
    /// unit-testable on any OS.
    /// </summary>
    public static bool IsAcceptable(PrintJobRecord record, string? knownShopId)
    {
        // Unknown local shop (not yet paired/resolved) — nothing to compare; accept.
        if (string.IsNullOrWhiteSpace(knownShopId)) return true;
        // Job carries no shop id (current v1 contract omits it) — accept; the
        // socket auth + device-scoped token are the gate.
        if (string.IsNullOrWhiteSpace(record.ShopId)) return true;
        return string.Equals(record.ShopId, knownShopId, StringComparison.OrdinalIgnoreCase);
    }
}
