namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using PrintShopCompanion.Infrastructure.Printers;
using Xunit;

/// <summary>
/// Tests for the SumatraPDF -print-settings composition (pure string logic —
/// no Windows print APIs). Regression coverage for the page-range bug: the
/// range used to be emitted as "r1-3,5-6" — SumatraPDF has no "r" prefix,
/// so the invalid token was silently ignored and the WHOLE document printed
/// even when the customer selected only a subset of pages.
/// </summary>
public class SumatraPrintSettingsTests
{
    // ── Page range (the regression) ──

    [Fact]
    public void BuildPrintSettings_range_is_emitted_as_plain_token()
    {
        var s = SumatraPdfPrinter.BuildPrintSettings(1, "1-3,5-6", null, null, null);
        s.Should().Contain("1-3,5-6");
        s.Should().NotContain("r1-3"); // the old, silently-ignored token form
    }

    [Fact]
    public void BuildPrintSettings_copies_come_first_then_range()
    {
        var s = SumatraPdfPrinter.BuildPrintSettings(2, "4-6", null, null, null);
        s.Should().Be("2x,4-6");
    }

    [Fact]
    public void BuildPrintSettings_null_range_is_omitted()
    {
        var s = SumatraPdfPrinter.BuildPrintSettings(1, null, null, null, null);
        s.Should().Be("1x");
    }

    [Fact]
    public void BuildPrintSettings_empty_range_is_omitted()
    {
        var s = SumatraPdfPrinter.BuildPrintSettings(1, "  ", null, null, null);
        s.Should().Be("1x");
    }

    // ── Normalisation ──

    [Theory]
    [InlineData("1-3,5-6", "1-3,5-6")]     // already clean
    [InlineData(" 1 - 3 , 5 ", "1-3,5")]   // whitespace stripped
    [InlineData("1-3,abc,5", "1-3,5")]     // junk segment dropped
    [InlineData(",,1,,", "1")]             // empty segments dropped
    [InlineData("abc,def", null)]          // nothing valid → null (no token)
    [InlineData(null, null)]
    [InlineData("", null)]
    public void NormalizePageRange_handles_messy_input(string? input, string? expected)
    {
        SumatraPdfPrinter.NormalizePageRange(input).Should().Be(expected);
    }

    // ── Other settings tokens ──

    [Theory]
    [InlineData(1, "1x")]
    [InlineData(3, "3x")]
    [InlineData(0, "1x")]      // floored — Sumatra rejects 0x
    [InlineData(-5, "1x")]
    public void BuildPrintSettings_copies_is_floored_at_one(int copies, string expected)
    {
        var s = SumatraPdfPrinter.BuildPrintSettings(copies, null, null, null, null);
        s.Should().StartWith(expected);
    }

    [Fact]
    public void BuildPrintSettings_color_modes()
    {
        SumatraPdfPrinter.BuildPrintSettings(1, null, true, null, null).Should().Be("1x,color");
        SumatraPdfPrinter.BuildPrintSettings(1, null, false, null, null).Should().Be("1x,monochrome");
        SumatraPdfPrinter.BuildPrintSettings(1, null, null, null, null).Should().Be("1x");
    }

    [Fact]
    public void BuildPrintSettings_paper_size()
    {
        SumatraPdfPrinter.BuildPrintSettings(1, null, null, "a4", null).Should().Be("1x,paper=a4");
    }

    [Theory]
    [InlineData("simplex", "1x,simplex")]
    [InlineData("vertical", "1x,duplexshort")]
    [InlineData("horizontal", "1x,duplexlong")]
    [InlineData("duplex", "1x,duplex")]   // unknown values pass through
    public void BuildPrintSettings_duplex_mapping(string duplex, string expected)
    {
        SumatraPdfPrinter.BuildPrintSettings(1, null, null, null, duplex).Should().Be(expected);
    }

    [Fact]
    public void BuildPrintSettings_full_combo_order_is_stable()
    {
        var s = SumatraPdfPrinter.BuildPrintSettings(2, "1,4-6", false, "a4", "horizontal");
        s.Should().Be("2x,1,4-6,monochrome,paper=a4,duplexlong");
    }
}
