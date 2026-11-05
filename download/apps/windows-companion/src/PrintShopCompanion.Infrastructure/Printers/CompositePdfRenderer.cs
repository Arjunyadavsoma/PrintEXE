namespace PrintShopCompanion.Infrastructure.Printers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Composite renderer. Picks the primary based on <see cref="PrinterOptions.Renderer"/>
/// and walks <see cref="PrinterOptions.FallbackRenderers"/> on failure. This honours
/// the user's "Phase 4 fallback" stack selection: PDFium → SumatraPDF → raw GDI.
/// </summary>
public sealed class CompositePdfRenderer : IPdfRenderer
{
    private readonly IPdfRenderer _pdfium;
    private readonly IPdfRenderer _sumatra;
    private readonly IPdfRenderer _gdi;
    private readonly IOptionsMonitor<PrinterOptions> _options;
    private readonly ILogger<CompositePdfRenderer> _logger;

    public CompositePdfRenderer(
        PdfiumPdfRenderer pdfium,
        SumatraPdfPrinter sumatra,
        RawGdiPrinter gdi,
        IOptionsMonitor<PrinterOptions> options,
        ILogger<CompositePdfRenderer> logger)
    {
        _pdfium = pdfium;
        _sumatra = sumatra;
        _gdi = gdi;
        _options = options;
        _logger = logger;
    }

    public async Task<int> PrintAsync(
        string pdfPath, string printerFullName,
        string? pageRange = null, int copies = 1,
        bool? color = null, string? paperSize = null, string? duplex = null,
        CancellationToken ct = default)
    {
        var chain = BuildChain();
        Exception? lastError = null;
        foreach (var (name, renderer) in chain)
        {
            try
            {
                _logger.LogInformation("Attempting print via {Renderer}", name);
                return await renderer.PrintAsync(pdfPath, printerFullName, pageRange, copies, color, paperSize, duplex, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Renderer {Renderer} failed; trying next", name);
                lastError = ex;
            }
        }
        throw new InvalidOperationException("All renderers in the fallback chain failed", lastError);
    }

    private IEnumerable<(string Name, IPdfRenderer Renderer)> BuildChain()
    {
        var o = _options.CurrentValue;
        var byName = new Dictionary<string, IPdfRenderer>(StringComparer.OrdinalIgnoreCase)
        {
            ["pdfium"] = _pdfium,
            ["sumatra"] = _sumatra,
            ["gdi"] = _gdi
        };
        yield return (o.Renderer, byName.TryGetValue(o.Renderer, out var primary) ? primary : _pdfium);
        foreach (var fb in o.FallbackRenderers)
        {
            if (string.Equals(fb, o.Renderer, StringComparison.OrdinalIgnoreCase)) continue;
            if (byName.TryGetValue(fb, out var r)) yield return (fb, r);
        }
    }
}
