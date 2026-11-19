namespace PrintShopCompanion.Core.Presentation;

using System.Globalization;
using PrintShopCompanion.Core.Models;

/// <summary>
/// Web-parity presentation layer. Mirrors printshop-saas's design system so
/// the Windows app and the shopkeeper webpage look and read the same:
///
///   - <see cref="StatusPresentation"/>  ≙ web's ORDER_STATUS_CONFIG
///     (src/components/shared/status-indicator.tsx): same labels, same
///     semantic colors (oklch tokens converted to sRGB hex).
///   - <see cref="Rupees"/>              ≙ web's formatINRFromPaise.
///   - <see cref="RelativeTime"/>        ≙ web's timeAgo.
///
/// Colors were converted from the web's oklch() tokens in src/app/globals.css:
///   brand   oklch(0.50 0.19 264) → #2858CD   (indigo — NOT amber)
///   info    oklch(0.50 0.13 230) → #006E9E
///   success oklch(0.52 0.14 152) → #007F3D
///   warning oklch(0.72 0.15 65)  → #E48E26
///   danger  oklch(0.52 0.19 25)  → #BE222A
/// Soft variants are the same hue at oklch L≈0.95; borders at L≈0.80.
/// </summary>
public static class StatusPresentation
{
    public sealed record StatusStyle(string Label, string Foreground, string Background, string Border);

    public static readonly StatusStyle Brand    = new("Printing",          "#2858CD", "#E1EFFF", "#AEBEDF");
    public static readonly StatusStyle Info     = new("Paid",              "#006E9E", "#DBF3FF", "#9ECAE4");
    public static readonly StatusStyle Success  = new("Ready for pickup",  "#007F3D", "#E0F5E5", "#A3D8B5");
    public static readonly StatusStyle SuccessDone = new("Completed",      "#007F3D", "#E0F5E5", "#A3D8B5");
    public static readonly StatusStyle Warning  = new("Awaiting payment",  "#7A4B0E", "#FFE8CC", "#E3B888");
    public static readonly StatusStyle Danger   = new("Cancelled",         "#BE222A", "#FFE7E4", "#E8A9A5");
    public static readonly StatusStyle Neutral  = new("Refunded",          "#4B5563", "#F3F4F6", "#D1D5DB");

    /// <summary>Map an order status to its web-identical presentation.</summary>
    public static StatusStyle For(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPayment => Warning,
        OrderStatus.Paid            => Info,
        OrderStatus.Printing        => Brand,
        OrderStatus.Ready           => Success,
        OrderStatus.Collected       => SuccessDone,
        OrderStatus.Cancelled       => Danger,
        _                           => Neutral,
    };

    /// <summary>Best-effort mapping for arbitrary/unknown status strings (API drift).
    /// Mirrors the web's fallback: the raw status string in a neutral pill.</summary>
    public static StatusStyle ForString(string? status)
    {
        if (Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var s)) return For(s);
        return new StatusStyle(status ?? "Unknown", Neutral.Foreground, Neutral.Background, Neutral.Border);
    }

    /// <summary>Convert "#RRGGBB" to a System.Drawing-style hex used by WPF (same format).</summary>
    public static string Hex(string hex) => hex;
}

/// <summary>₹ formatting identical to the web's formatINRFromPaise (INR, grouping, 2 decimals).</summary>
public static class Rupees
{
    public static string Format(long paise)
    {
        var rupees = paise / 100m;
        return "₹" + rupees.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    }

    public static string FormatNullable(long? paise) => paise is null ? "—" : Format(paise.Value);
}

/// <summary>Compact relative time identical to the web's timeAgo helper.</summary>
public static class RelativeTime
{
    public static string From(DateTimeOffset utc)
    {
        var ago = DateTimeOffset.UtcNow - utc;
        if (ago.TotalSeconds < 5) return "just now";
        if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s ago";
        if (ago.TotalMinutes < 60) return ago.TotalMinutes < 2 ? "1m ago" : $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return ago.TotalHours < 2 ? "1h ago" : $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 7) return ago.TotalDays < 2 ? "1d ago" : $"{(int)ago.TotalDays}d ago";
        return utc.ToLocalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }
}
