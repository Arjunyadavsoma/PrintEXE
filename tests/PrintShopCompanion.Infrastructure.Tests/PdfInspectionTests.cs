using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Infrastructure.Pdf;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>
/// Decision D-04 — managed PDF inspection. Runs for real on any OS: the
/// PDFium native library ships for linux-x64 and win-x64 via PDFtoImage.
/// </summary>
public sealed class PdfInspectionTests
{
    private static string TempPath(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "psh-pdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static async Task<string> WriteAsync(string path, byte[] bytes)
    {
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static readonly PdfDocumentInspector Inspector =
        new(new Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfDocumentInspector>());

    [Fact]
    public async Task Valid_test_page_reports_one_A4_page()
    {
        var path = await WriteAsync(TempPath("valid.pdf"), TestPagePdf.BuildBytes("unit test", "structured"));
        var result = await Inspector.InspectAsync(path);
        Assert.True(result.IsValid, result.Message);
        Assert.Equal(1, result.PageCount);
        Assert.NotNull(result.PageWidthPt);
        // Our test page declares MediaBox [0 0 595 842] (A4 in points).
        Assert.InRange(result.PageWidthPt!.Value, 594, 596);
        Assert.InRange(result.PageHeightPt!.Value, 841, 843);
    }

    [Fact]
    public async Task Empty_file_is_classified_empty()
    {
        var path = await WriteAsync(TempPath("empty.pdf"), Array.Empty<byte>());
        var result = await Inspector.InspectAsync(path);
        Assert.Equal(PdfInspectionError.Empty, result.Error);
    }

    [Fact]
    public async Task Missing_file_is_classified_empty()
    {
        var result = await Inspector.InspectAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".pdf"));
        Assert.Equal(PdfInspectionError.Empty, result.Error);
    }

    [Fact]
    public async Task Non_pdf_bytes_are_classified_unsupported()
    {
        var path = await WriteAsync(TempPath("html.pdf"), "<html><body>error page</body></html>"u8.ToArray());
        var result = await Inspector.InspectAsync(path);
        Assert.Equal(PdfInspectionError.Unsupported, result.Error);
    }

    [Fact]
    public async Task Corrupt_pdf_with_valid_header_is_rejected()
    {
        // Header present (so not "Unsupported") but the body is garbage.
        var garbage = "%PDF-1.4\nthis is not a pdf body at all\u0000\u0000\u0001\u0002\u0003"u8.ToArray();
        var path = await WriteAsync(TempPath("corrupt.pdf"), garbage);
        var result = await Inspector.InspectAsync(path);
        Assert.False(result.IsValid);
        Assert.True(result.Error is PdfInspectionError.Corrupt or PdfInspectionError.NoPages,
            $"unexpected classification {result.Error}");
    }

    [Fact]
    public void Header_scanner_allows_leading_junk_like_the_pdf_spec()
    {
        var junk = new byte[600];
        junk.AsSpan().Fill(0x20);
        var header = "%PDF-1.7"u8;
        header.CopyTo(junk.AsSpan(500));
        Assert.True(PdfDocumentInspector.LooksLikePdf(junk));
        Assert.False(PdfDocumentInspector.LooksLikePdf(new byte[] { 0x25, 0x00, 0x10 }));
    }

    [Fact]
    public async Task Test_page_preview_renders_png_bytes()
    {
        var path = await WriteAsync(TempPath("preview.pdf"), TestPagePdf.BuildBytes("preview", "png"));
        var png = PdfDocumentInspector.TryRenderFirstPagePng(path);
        Assert.NotNull(png);
        // PNG magic bytes.
        Assert.Equal(0x89, png![0]);
        Assert.Equal((byte)'P', png[1]);
    }
}
