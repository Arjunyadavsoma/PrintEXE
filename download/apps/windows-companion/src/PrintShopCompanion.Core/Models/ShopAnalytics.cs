namespace PrintShopCompanion.Core.Models;

/// <summary>
/// The dashboard analytics payload from <c>GET /api/v1/shops/me/analytics</c>.
/// Drives the Overview screen's cards and the popular-settings breakdown.
/// </summary>
public sealed class ShopAnalytics
{
    public required RevenueSummary Revenue { get; init; }
    public required OrderCountSummary OrderCount { get; init; }
    public required IReadOnlyDictionary<OrderStatus, int> ByStatus { get; init; }
    public required PopularSettings Popular { get; init; }
    public int? PeakHourUtc { get; init; }
    public required IReadOnlyList<int> Hours { get; init; }   // 24 entries
}

public sealed record RevenueSummary(long TotalPaise, string Formatted);
public sealed record OrderCountSummary(int Total, int Paid);
public sealed record PopularSettings(
    int Color, int Bw, int Duplex, int Simplex, int A4, int A3, int Other);

/// <summary>One day's row from <c>GET /api/v1/shops/me/reports/daily</c>.</summary>
public sealed class DailyReportRow
{
    public required string Date { get; init; }    // yyyy-MM-dd
    public int Orders { get; init; }
    public long RevenuePaise { get; init; }
    public int Files { get; init; }
    public required IReadOnlyDictionary<OrderStatus, int> ByStatus { get; init; }
}
