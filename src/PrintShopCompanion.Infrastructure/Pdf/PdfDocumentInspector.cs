namespace PrintShopCompanion.Infrastructure.Pdf;

using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;
using SkiaSharp;

/// <summary>
/// Managed PDF inspection backed by PDFium (via PDFtoImage, Apache-2.0/MIT —
/// see docs/research/PRINTEXE_ENGINEERING_DECISIONS.md D-04 and the
/// THIRD-PARTY-NOTICES register).
///
/// <para>Runs BEFORE a job is claimed and spooled. The companion never prints
/// a document it cannot open and classify: encrypted, corrupt, empty and
/// oversized files are failed permanently with a precise, user-safe reason.
/// Rendering happens in-process; PDFium is not thread-safe, and PDFtoImage
/// serializes calls within the process, so a single shared instance is safe.</para>
///
/// <para>Privacy: this class never logs document content — only structural
/// facts (page count, page size, classification) and never persists extracts
/// of the document. Preview PNGs (when requested) live in the job's own
/// cache directory and follow the same retention rules as the PDF.</para>
/// </summary>
public sealed class PdfDocumentInspector : IPdfInspector
{
    /// <summary>Maximum bytes the inspector will accept. Matches the backend's download cap.</summary>
    public const long MaxBytes = 100 * 1024 * 1024;

    private readonly ILogger<PdfDocumentInspector> _logger;

    public PdfDocumentInspector(ILogger<PdfDocumentInspector> logger) => _logger = logger;

    public Task<PdfInspectionResult> InspectAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Inspect(pdfPath), cancellationToken);
    }

    private PdfInspectionResult Inspect(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return new PdfInspectionResult { Error = PdfInspectionError.Empty, Message = "File not found: " + pdfPath };

        var fi = new FileInfo(pdfPath);
        if (fi.Length == 0)
            return new PdfInspectionResult { Error = PdfInspectionError.Empty, Message = "File is empty (0 bytes)" };
        if (fi.Length > MaxBytes)
            return new PdfInspectionResult { Error = PdfInspectionError.Oversized, Message = $"File is {fi.Length:N0} bytes (limit {MaxBytes:N0})" };

        try
        {
            // Read fully (bounded by MaxBytes) so PDFium works on a stable in-memory copy.
            var bytes = File.ReadAllBytes(pdfPath);

            // Cheap structural pre-check: PDFium reports odd errors for HTML
            // error pages / truncated uploads; a missing header classifies them
            // precisely instead of "corrupt".
            if (!LooksLikePdf(bytes))
                return new PdfInspectionResult { Error = PdfInspectionError.Unsupported, Message = "Missing %PDF- header — not a PDF document" };

            int pageCount;
            double wPt = 0, hPt = 0;
            // PDFtoImage: GetPageCount(byte[]) / GetPageSizes(byte[]) / ToImage(Stream, page).
            pageCount = PDFtoImage.Conversion.GetPageCount(bytes);
            if (pageCount <= 0)
                return new PdfInspectionResult { Error = PdfInspectionError.NoPages, Message = "Document parsed but contains 0 pages" };

            var sizes = PDFtoImage.Conversion.GetPageSizes(bytes);
            if (sizes is { Count: > 0 })
            {
                var first = sizes[0];
                wPt = first.Width;
                hPt = first.Height;
            }

            _logger.LogInformation("PDF inspection OK: {PageCount} page(s), {W:F0}x{H:F0} pt, {Bytes:N0} bytes",
                pageCount, wPt, hPt, fi.Length);
            return new PdfInspectionResult
            {
                Error = PdfInspectionError.None,
                PageCount = pageCount,
                PageWidthPt = wPt > 0 ? wPt : null,
                PageHeightPt = hPt > 0 ? hPt : null,
            };
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PDF inspection: document is password-protected ({Path})", pdfPath);
            return new PdfInspectionResult { Error = PdfInspectionError.Encrypted, Message = "Document is password-protected" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF inspection failed for {Path}", pdfPath);
            return new PdfInspectionResult { Error = PdfInspectionError.Corrupt, Message = "PDF could not be parsed: " + ex.GetType().Name };
        }
    }

    /// <summary>True when the bytes start with a %PDF- header (allowing a small BOM/whitespace prefix).</summary>
    internal static bool LooksLikePdf(byte[] bytes)
    {
        const string header = "%PDF-";
        // PDF spec allows arbitrary junk before %PDF- (readers scan the first 1024 bytes).
        var scan = Math.Min(bytes.Length, 1024);
        for (var i = 0; i + header.Length <= scan; i++)
        {
            var match = true;
            for (var j = 0; j < header.Length; j++)
            {
                if (bytes[i + j] != (byte)header[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    /// <summary>
    /// Renders the first page to a PNG (bounded to <paramref name="maxWidthPx"/>)
    /// for UI previews. Returns null on any failure — previews are best-effort
    /// and never block printing.
    /// </summary>
    public static byte[]? TryRenderFirstPagePng(string pdfPath, int maxWidthPx = 512)
    {
        try
        {
            using var stream = File.OpenRead(pdfPath);
            using var bitmap = PDFtoImage.Conversion.ToImage(stream, page: 0);
            if (bitmap is null) return null;
            var scale = Math.Min(1f, maxWidthPx / (float)bitmap.Width);
            var target = new SKImageInfo(
                Math.Max(1, (int)(bitmap.Width * scale)),
                Math.Max(1, (int)(bitmap.Height * scale)));
            using var resized = bitmap.Resize(target, SKSamplingOptions.Default); // SkiaSharp 3.x sampling API
            using var image = SKImage.FromBitmap(resized ?? bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 80).ToArray();
        }
        catch
        {
            return null; // previews never block the pipeline
        }
    }
}
