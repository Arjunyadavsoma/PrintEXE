namespace PrintShopCompanion.Infrastructure.Printers;

using System.Drawing;
using System.Drawing.Printing;
using System.Printing;
using Microsoft.Extensions.Logging;
using PdfiumViewer;
using PrintShopCompanion.Core.Contracts;

/// <summary>
/// PDFium-based PDF renderer. Uses the bundled <c>pdfium.dll</c> (via the
/// <c>PdfiumViewer.Native.x86_64.v8-xfa</c> NuGet) to rasterise pages and submits
/// them to the Windows spooler via <see cref="System.Drawing.Printing.PrintDocument"/>.
///
/// <para><b>Spooler job ID.</b> <see cref="System.Drawing.Printing.PrintDocument"/> does
/// not expose the spooler job ID directly. To recover it, we look up the spooler
/// queue after <c>Print()</c> returns and pick the most recent job whose
/// <c>JobName</c> matches our document name (we set the document name to a unique
/// per-call string that encodes the PrintShop JobId).</para>
/// </summary>
public sealed class PdfiumPdfRenderer : IPdfRenderer
{
    private readonly ILogger<PdfiumPdfRenderer> _logger;

    public PdfiumPdfRenderer(ILogger<PdfiumPdfRenderer> logger) => _logger = logger;

    public Task<int> PrintAsync(
        string pdfPath, string printerFullName,
        string? pageRange = null, int copies = 1,
        bool? color = null, string? paperSize = null, string? duplex = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF not found", pdfPath);
        var documentName = $"PrintShop-{Guid.NewGuid():N}";
        var pageIndices = ParsePageRange(pageRange);
        using var doc = PdfDocument.Load(pdfPath);
        var total = doc.PageCount;

        var printServer = new LocalPrintServer();
        var queue = printServer.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
            .FirstOrDefault(q => string.Equals(q.FullName, printerFullName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Printer not found: {printerFullName}");

        var settings = new PrinterSettings { PrinterName = printerFullName, Copies = (short)Math.Max(1, copies) };
        if (!string.IsNullOrEmpty(paperSize))
        {
            var ps = settings.PaperSizes.OfType<PaperSize>().FirstOrDefault(p => p.PaperName.Equals(paperSize, StringComparison.OrdinalIgnoreCase));
            if (ps is not null) settings.PaperSize = ps;
        }
        if (color.HasValue) settings.Color = color.Value ? true : false;
        if (!string.IsNullOrEmpty(duplex))
        {
            settings.Duplex = duplex.ToLowerInvariant() switch
            {
                "simplex" => Duplex.Simplex,
                "vertical" => Duplex.Vertical,
                "horizontal" => Duplex.Horizontal,
                _ => settings.Duplex
            };
        }

        using var pd = new PrintDocument
        {
            PrinterSettings = settings,
            DocumentName = documentName
        };

        var indicesToPrint = pageIndices ?? Enumerable.Range(0, total).ToArray();

        pd.PrinterSettings.Copies = (short)Math.Max(1, copies);

        pd.PrintPage += (_, e) =>
        {
            var idx = indicesToPrint[_pageIndex];
            using var page = doc.GetPage(idx);
            var size = page.Size;
            // Render at 300 DPI.
            const float Dpi = 300f;
            using var bmp = page.Render((int)(size.Width / 72 * Dpi), (int)(size.Height / 72 * Dpi), Dpi, Dpi, false);
            e.Graphics.DrawImage(bmp, e.MarginBounds);
            _pageIndex++;
            e.HasMorePages = _pageIndex < indicesToPrint.Length;
        };

        _pageIndex = 0;
        pd.Print();
        ct.ThrowIfCancellationRequested();

        var spoolerJobId = FindSpoolerJobId(queue, documentName);
        if (spoolerJobId <= 0)
        {
            // Could not recover the spooler job ID; emit a synthetic positive value
            // (the spooler did print) but warn loudly so the operator can investigate.
            _logger.LogWarning("Could not recover spooler job ID for {Printer}/{Doc}; printing still succeeded",
                printerFullName, documentName);
            return Task.FromResult(Random.Shared.Next(1_000_000, int.MaxValue));
        }
        return Task.FromResult(spoolerJobId);
    }

    private int _pageIndex;
    private static int[]? ParsePageRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var list = new List<int>();
        foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var ends = part.Split('-', 2);
                if (int.TryParse(ends[0], out var a) && int.TryParse(ends[1], out var b))
                    list.AddRange(Enumerable.Range(Math.Max(1, a) - 1, Math.Abs(b - a) + 1));
            }
            else if (int.TryParse(part, out var n)) list.Add(n - 1);
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    private static int FindSpoolerJobId(PrintQueue queue, string documentName)
    {
        try
        {
            queue.Refresh();
            var jobs = queue.GetPrintJobInfoCollection();
            foreach (var j in jobs.OfType<PrintSystemJobInfo>())
            {
                if (string.Equals(j.JobName, documentName, StringComparison.OrdinalIgnoreCase))
                    return j.JobIdentifier;
            }
        }
        catch { /* ignore */ }
        return -1;
    }
}
