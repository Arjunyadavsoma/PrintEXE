namespace PrintShopCompanion.Infrastructure.Printers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Composite renderer. Picks the primary based on <see cref="PrinterOptions.Renderer"/>
/// and walks <see cref="PrinterOptions.FallbackRenderers"/> on failure.
///
/// <para>As of v0.1 the default chain is:</para>
/// <list type="number">
///   <item><b>SumatraPDF</b> — primary. Bundled <c>SumatraPDF.exe</c> invoked with
///     <c>-silent -print-to &lt;printer&gt; -print-stat &lt;pdf&gt;</c>. SumatraPDF
///     renders the PDF internally and submits to the Windows spooler. It reports
///     the spooler job ID via stdout as <c>SPOOLER_JOB_ID=&lt;int&gt;</c>. This is
///     the same binary that ships in <c>installer\redist\SumatraPDF.exe</c> and is
///     bundled into the MSI by <c>PrintShopCompanion.Installer.wxs</c>.</item>
///   <item><b>Raw GDI</b> — fallback for ESC/POS / label / dot-matrix printers
///     that don't accept PDFs directly. Uses <see cref="System.Drawing.Printing.PrintDocument"/>
///     and the winspool.drv P/Invoke for raw byte passthrough.</item>
/// </list>
///
/// <para>The previous PDFium-based in-process renderer was removed because:</para>
/// <list type="bullet">
///   <item>The PdfiumViewer NuGet package targets .NET Framework (NU1701) and
///     pulls vulnerable transitive dependencies including Microsoft.NETCore.App
///     1.1.0 and Newtonsoft.Json 9.0.1 (NU1903).</item>
///   <item>The native binary package PdfiumViewer.Native.x86_64.v8-xfa 2018.4.12.1858
///     is not available on NuGet (NU1102); only 2018.4.8.256 exists, and that
///     build is also unmaintained.</item>
///   <item>SumatraPDF already provides the same PDF printing capability more
///     robustly: it renders the PDF internally and submits to the spooler, so
///     we don't need to load a native renderer into the companion process.</item>
/// </list>
/// </summary>
public sealed class CompositePdfRenderer : IPdfRenderer
{
    private readonly SumatraPdfPrinter _sumatra;
    private readonly RawGdiPrinter _gdi;
    private readonly IOptionsMonitor<PrinterOptions> _options;
    private readonly ILogger<CompositePdfRenderer> _logger;

    public CompositePdfRenderer(
        SumatraPdfPrinter sumatra,
        RawGdiPrinter gdi,
        IOptionsMonitor<PrinterOptions> options,
        ILogger<CompositePdfRenderer> logger)
    {
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
            ["sumatra"] = _sumatra,
            ["gdi"]     = _gdi,
        };
        yield return (o.Renderer, byName.TryGetValue(o.Renderer, out var primary) ? primary : _sumatra);
        foreach (var fb in o.FallbackRenderers)
        {
            if (string.Equals(fb, o.Renderer, StringComparison.OrdinalIgnoreCase)) continue;
            if (byName.TryGetValue(fb, out var r)) yield return (fb, r);
        }
    }
}
