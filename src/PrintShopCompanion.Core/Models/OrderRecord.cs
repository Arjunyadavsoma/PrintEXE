namespace PrintShopCompanion.Core.Models;

/// <summary>
/// The set of valid order statuses from <c>packages/core/src/domain/order.ts</c>.
/// Used by the Order Details screen to enable / disable the action buttons.
/// </summary>
public enum OrderStatus
{
    AwaitingPayment,
    Paid,
    Printing,
    Ready,
    Collected,
    Cancelled
}

/// <summary>Extension methods for the <see cref="OrderStatus"/> state machine.</summary>
public static class OrderStatusExtensions
{
    /// <summary>Mirrors <c>canTransitionOrder(from, to)</c> in the real backend.</summary>
    public static bool CanTransitionTo(this OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.AwaitingPayment, OrderStatus.Paid)       => true,
        (OrderStatus.AwaitingPayment, OrderStatus.Cancelled)  => true,
        (OrderStatus.Paid,           OrderStatus.Printing)    => true,
        (OrderStatus.Paid,           OrderStatus.Cancelled)    => true,
        (OrderStatus.Printing,       OrderStatus.Ready)        => true,
        (OrderStatus.Printing,       OrderStatus.Cancelled)    => true,
        (OrderStatus.Ready,          OrderStatus.Collected)    => true,
        _ => false
    };

    /// <summary>The wire string the backend expects in URL params and JSON bodies.</summary>
    public static string ToWire(this OrderStatus s) => s switch
    {
        OrderStatus.AwaitingPayment => "awaiting_payment",
        OrderStatus.Paid             => "paid",
        OrderStatus.Printing          => "printing",
        OrderStatus.Ready             => "ready",
        OrderStatus.Collected         => "collected",
        OrderStatus.Cancelled          => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    /// <summary>Parse the wire string into the enum.</summary>
    public static OrderStatus ParseWire(string wire) => wire switch
    {
        "awaiting_payment" => OrderStatus.AwaitingPayment,
        "paid"             => OrderStatus.Paid,
        "printing"          => OrderStatus.Printing,
        "ready"             => OrderStatus.Ready,
        "collected"         => OrderStatus.Collected,
        "cancelled"          => OrderStatus.Cancelled,
        _ => throw new ArgumentException($"Unknown order status: {wire}")
    };
}

/// <summary>
/// An order, as returned by <c>GET /api/v1/shops/me/orders</c> (list) or
/// <c>GET /api/v1/shops/me/orders/{id}</c> (detail). The detail endpoint
/// also returns <see cref="Files"/>.
/// </summary>
public sealed class OrderRecord
{
    public required string Id { get; init; }
    public required string ShopId { get; init; }
    public required string CustomerToken { get; init; }
    public OrderStatus Status { get; set; }
    public required long TotalAmountPaise { get; init; }
    public string Currency { get; init; } = "INR";
    public string? PaymentProvider { get; init; }
    public string? PaymentTxnId { get; init; }
    public string? PaymentUtr { get; init; }
    public DateTimeOffset? PaidAt { get; init; }
    public string? CustomerPhone { get; init; }
    public string? Notes { get; set; }
    public string? TokenNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Only populated by the order-detail endpoint.</summary>
    public IReadOnlyList<OrderFile>? Files { get; init; }
}

/// <summary>
/// A file in an order, as returned by <c>GET /api/v1/shops/me/orders/{id}</c>.
/// Mirrors the real <c>PrintFile</c> Prisma model.
/// </summary>
public sealed class OrderFile
{
    public required string Id { get; init; }
    public required string OrderId { get; init; }
    public required string OriginalFilename { get; init; }
    public string? StorageKey { get; init; }
    public string? MimeType { get; init; }
    public long SizeBytes { get; init; }
    public int? PageCount { get; init; }
    public int Copies { get; init; } = 1;
    public string ColorMode { get; init; } = "bw";   // "bw" | "color"
    public string? PageRange { get; init; }
    public string PaperSize { get; init; } = "a4";
    public bool Duplex { get; init; }
    public long PricePaise { get; init; }
    public string Status { get; init; } = "pending";   // pending | queued | printing | completed | failed
    public DateTimeOffset? PrintedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
