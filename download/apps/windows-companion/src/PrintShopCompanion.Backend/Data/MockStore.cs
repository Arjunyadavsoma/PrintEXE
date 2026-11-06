namespace PrintShopCompanion.Backend.Data;

using System.Collections.Concurrent;
using PrintShopCompanion.Shared.Dto;

/// <summary>
/// In-memory store for the mock backend. Not durable — restarts lose state.
/// That's fine for local pairing / test-print flows.
/// </summary>
public sealed class MockStore
{
    public ConcurrentDictionary<string, DeviceRecord> Devices { get; } = new();
    public ConcurrentDictionary<string, JobRecord> Jobs { get; } = new();
    public ConcurrentDictionary<string, string> PendingPairings { get; } = new(); // token -> shopId
    public string ShopId { get; set; } = "shop_demo_001";
    public string ShopName { get; set; } = "Demo Print Shop";
    public byte[] TestPdf { get; set; } = Array.Empty<byte>();
}

public sealed record DeviceRecord(
    string DeviceId, string ShopId, string Hostname, string OperatingSystem,
    string AppVersion, string DeviceToken, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);

public sealed record JobRecord(
    PrintJobDto Dto, bool Acked, DateTimeOffset? PrintedAt, DateTimeOffset? FailedAt,
    string? Error, int? SpoolerJobId);
