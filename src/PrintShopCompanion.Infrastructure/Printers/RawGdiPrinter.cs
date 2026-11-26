namespace PrintShopCompanion.Infrastructure.Printers;

using System.Drawing;
using System.Drawing.Printing;
using System.Printing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;
using SkiaSharp;

/// <summary>
/// Raw GDI printer — the **fallback** renderer for label / receipt / dot-matrix
/// printers that take raw bytes (ESC/POS, PCL) or that don't behave well with the
/// SumatraPDF command-line path. Supports both a "raw bytes" mode (for ESC/POS
/// printers, via winspool.drv P/Invoke) and a "GDI rasterise" mode (used as the
/// final fallback when SumatraPDF is unavailable or the printer doesn't accept
/// PDF documents at all).
/// </summary>
public sealed class RawGdiPrinter : IPdfRenderer
{
    private readonly ILogger<RawGdiPrinter> _logger;
    public RawGdiPrinter(ILogger<RawGdiPrinter> logger) => _logger = logger;

    public Task<int> PrintAsync(
        string pdfPath, string printerFullName,
        string? pageRange = null, int copies = 1,
        bool? color = null, string? paperSize = null, string? duplex = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF not found", pdfPath);
        var documentName = $"PrintShop-{Guid.NewGuid():N}";

        // Decision D-05: rasterise the ACTUAL PDF pages with PDFium and print
        // them through System.Drawing. The previous implementation drew a
        // placeholder string ("raw GDI fallback") — a fake fallback that
        // produced wrong paper. Pages are rendered up-front for the requested
        // range (bounded memory: only selected pages are held).
        var pageIndexes = SelectPageIndexes(pdfPath, pageRange);
        if (pageIndexes.Count == 0)
            throw new InvalidOperationException("Page range selected no pages (document may be empty)");

        using var pd = new PrintDocument
        {
            DocumentName = documentName,
            PrinterSettings = { PrinterName = printerFullName, Copies = (short)Math.Max(1, copies) }
        };
        if (!string.IsNullOrEmpty(paperSize))
        {
            var match = pd.PrinterSettings.PaperSizes
                .Cast<PaperSize>()
                .FirstOrDefault(p => string.Equals(p.PaperName, paperSize, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                pd.DefaultPageSettings.PaperSize = match;
            // Unknown paper names fall back to the printer default — the spec
            // the customer asked for is preserved in the job record and shown
            // in the UI; we never silently claim a different paper succeeded.
        }
        if (duplex is not null)
        {
            pd.PrinterSettings.Duplex = duplex.ToLowerInvariant() switch
            {
                "simplex" or "onesided" => Duplex.Simplex,
                "vertical" or "duplexshort" or "shortedge" => Duplex.Vertical,
                "horizontal" or "duplexlong" or "longedge" => Duplex.Horizontal,
                _ => pd.PrinterSettings.Duplex
            };
        }

        var pages = RenderPages(pdfPath, pageIndexes, grayscale: color == false);
        try
        {
            var pageQueue = new Queue<Image>(pages);
            pd.PrintPage += (_, e) =>
            {
                ct.ThrowIfCancellationRequested();
                if (pageQueue.Count == 0) { e.HasMorePages = false; return; }
                var image = pageQueue.Dequeue();
                DrawPageCoveringPrintableArea(e.Graphics, e.MarginBounds, image);
                e.HasMorePages = pageQueue.Count > 0;
            };
            pd.Print();
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }

        var spoolerId = FindSpoolerJobId(printerFullName, documentName);
        if (spoolerId <= 0)
        {
            // Decision D-05 (parity with SumatraPdfPrinter): NO synthetic id.
            // 0 = "no id" per the IPdfRenderer contract; the caller parks the
            // job as outcome-uncertain instead of assuming printed.
            _logger.LogWarning("RawGdi: could not recover spooler job ID for {Printer} — physical outcome cannot be confirmed", printerFullName);
            return Task.FromResult(0);
        }
        return Task.FromResult(spoolerId);
    }

    /// <summary>Parses a Sumatra-style page range ("1-3,5") into 0-based indexes; empty/null = all pages.</summary>
    private List<int> SelectPageIndexes(string pdfPath, string? pageRange)
    {
        var total = PDFtoImage.Conversion.GetPageCount(File.ReadAllBytes(pdfPath));
        var indexes = new List<int>();
        if (string.IsNullOrWhiteSpace(pageRange))
        {
            for (var i = 0; i < total; i++) indexes.Add(i);
            return indexes;
        }
        foreach (var seg in SumatraPdfPrinter.NormalizePageRange(pageRange)?.Split(',') ?? Array.Empty<string>())
        {
            if (seg.Length == 0) continue;
            var dash = seg.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(seg, out var one) && one >= 1 && one <= total) indexes.Add(one - 1);
            }
            else if (int.TryParse(seg[..dash], out var from) && int.TryParse(seg[(dash + 1)..], out var to))
            {
                from = Math.Max(1, from);
                to = Math.Min(total, to);
                for (var i = from; i <= to; i++) indexes.Add(i - 1);
            }
        }
        return indexes.Distinct().OrderBy(i => i).ToList();
    }

    /// <summary>Renders the given 0-based pages to GDI images (optionally grayscaled for B&W jobs).</summary>
    private List<Image> RenderPages(string pdfPath, IReadOnlyList<int> pageIndexes, bool grayscale)
    {
        var images = new List<Image>(pageIndexes.Count);
        foreach (var idx in pageIndexes)
        {
            RenderOnePage(pdfPath, idx, grayscale, images);
        }
        return images;
    }

    private void RenderOnePage(string pdfPath, int pageIndex, bool grayscale, List<Image> images)
    {
        using var stream = File.OpenRead(pdfPath);
        using var sk = PDFtoImage.Conversion.ToImage(stream, page: pageIndex);
        using var data = SKImage.FromBitmap(sk).Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        var img = Image.FromStream(ms);
        if (grayscale) img = ToGrayscale(img);
        images.Add(img);
    }

    private static Image ToGrayscale(Image source)
    {
        var result = new Bitmap(source.Width, source.Height);
        using var g = Graphics.FromImage(result);
        // ITU-R 601 luminance matrix — matches what "monochrome" drivers usually do.
        var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
        {
            new[] { 0.299f, 0.299f, 0.299f, 0, 0 },
            new[] { 0.587f, 0.587f, 0.587f, 0, 0 },
            new[] { 0.114f, 0.114f, 0.114f, 0, 0 },
            new[] { 0f, 0f, 0f, 1, 0 },
            new[] { 0f, 0f, 0f, 0, 1 },
        });
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        return result;
    }

    /// <summary>Draws the page bitmap to fill the printable area while preserving aspect ratio, centred.</summary>
    private static void DrawPageCoveringPrintableArea(Graphics g, Rectangle bounds, Image page)
    {
        var scale = Math.Min((float)bounds.Width / page.Width, (float)bounds.Height / page.Height);
        var w = (int)(page.Width * scale);
        var h = (int)(page.Height * scale);
        var x = bounds.X + (bounds.Width - w) / 2;
        var y = bounds.Y + (bounds.Height - h) / 2;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(page, x, y, w, h);
    }

    private static int FindSpoolerJobId(string printerFullName, string documentName)
    {
        try
        {
            var server = new LocalPrintServer();
            var queue = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                .FirstOrDefault(q => string.Equals(q.FullName, printerFullName, StringComparison.OrdinalIgnoreCase));
            if (queue is null) return -1;
            queue.Refresh();
            foreach (var j in queue.GetPrintJobInfoCollection().OfType<PrintSystemJobInfo>())
                if (string.Equals(j.JobName, documentName, StringComparison.OrdinalIgnoreCase))
                    return j.JobIdentifier;
        }
        catch { /* ignore */ }
        return -1;
    }

    // ─────────────── raw-byte passthrough (for ESC/POS printers) ───────────────

    /// <summary>Send raw bytes directly to a printer's port. Used for ESC/POS label printers.</summary>
    public static bool SendRawBytes(string printerName, byte[] bytes)
    {
        // P/Invoke to winspool.drv AddMonitor etc. is omitted for brevity; the production
        // build uses the RawPrinterHelper from the System.Drawing.Printing namespace.
        // See docs/PHASES.md (Phase 4) for the precise P/Invoke contract.
        return RawPrinterHelper.SendBytes(printerName, bytes);
    }
}

internal static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DocInfo
    {
        public string pDocName;
        public string pOutputFile;
        public string pDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr hPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DocInfo di);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] buf, int count, out int written);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    public static bool SendBytes(string printerName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out var h, IntPtr.Zero)) return false;
        try
        {
            var di = new DocInfo { pDocName = "PrintShop raw", pOutputFile = "", pDataType = "RAW" };
            if (!StartDocPrinter(h, 1, ref di)) return false;
            try
            {
                StartPagePrinter(h);
                WritePrinter(h, bytes, bytes.Length, out _);
                EndPagePrinter(h);
                return true;
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }
}
