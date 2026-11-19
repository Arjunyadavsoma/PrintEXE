namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Presentation;
using Xunit;

/// <summary>
/// Web-parity presentation catalog. These tests pin the Windows app's status
/// labels, colors and ₹ formatting to the EXACT values the shopkeeper webpage
/// renders (printshop-saas ORDER_STATUS_CONFIG + formatINRFromPaise), so a
/// future drift in either direction fails CI instead of silently mismatching.
/// </summary>
public class WebUiCatalogTests
{
    [Theory]
    [InlineData(OrderStatus.AwaitingPayment, "Awaiting payment")]
    [InlineData(OrderStatus.Paid, "Paid")]
    [InlineData(OrderStatus.Printing, "Printing")]
    [InlineData(OrderStatus.Ready, "Ready for pickup")]
    [InlineData(OrderStatus.Collected, "Completed")]
    [InlineData(OrderStatus.Cancelled, "Cancelled")]
    public void Status_labels_match_the_web(OrderStatus status, string expectedLabel)
    {
        StatusPresentation.For(status).Label.Should().Be(expectedLabel);
    }

    [Fact]
    public void Printing_uses_the_indigo_brand_not_amber()
    {
        var s = StatusPresentation.For(OrderStatus.Printing);
        s.Foreground.Should().Be("#2858CD");   // web brand oklch(0.50 0.19 264)
        s.Background.Should().Be("#E1EFFF");   // web brand-soft
    }

    [Theory]
    [InlineData(OrderStatus.AwaitingPayment, "#E48E26")]   // warning
    [InlineData(OrderStatus.Paid, "#006E9E")]              // info
    [InlineData(OrderStatus.Ready, "#007F3D")]             // success
    [InlineData(OrderStatus.Collected, "#007F3D")]         // success (Completed)
    [InlineData(OrderStatus.Cancelled, "#BE222A")]         // danger
    public void Status_foregrounds_match_web_semantic_tokens(OrderStatus status, string expectedHex)
    {
        StatusPresentation.For(status).Foreground.Should().Be(expectedHex);
    }

    [Fact]
    public void Unknown_status_strings_fall_back_to_neutral_with_raw_label()
    {
        var s = StatusPresentation.ForString("mystery_status");
        s.Label.Should().Be("mystery_status");   // web fallback: raw status in a neutral pill
        s.Foreground.Should().Be("#4B5563");
    }

    [Theory]
    [InlineData(0, "₹0.00")]
    [InlineData(12500, "₹125.00")]
    [InlineData(123456789, "₹12,34,567.89")]   // en-IN grouping (lakh/crore), same as web
    [InlineData(-500, "₹-5.00")]
    public void Rupee_formatting_matches_formatINRFromPaise(long paise, string expected)
    {
        Rupees.Format(paise).Should().Be(expected);
    }

    [Fact]
    public void RelativeTime_buckets()
    {
        var now = DateTimeOffset.UtcNow;
        RelativeTime.From(now.AddSeconds(-5)).Should().Be("5s ago");
        RelativeTime.From(now.AddMinutes(-3)).Should().Be("3m ago");
        RelativeTime.From(now.AddHours(-2)).Should().Be("2h ago");
    }
}
