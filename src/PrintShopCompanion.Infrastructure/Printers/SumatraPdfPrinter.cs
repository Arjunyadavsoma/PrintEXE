namespace PrintShopCompanion.Infrastructure.Printers;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

/// <summary>
/// SumatraPDF command-line renderer — the **primary** PDF printing path as of v0.1.1.
/// Invokes the bundled <c>SumatraPDF.exe</c> with
/// <c>-silent -print-to &lt;printer&gt; -print-settings &lt;...&gt; -print-stat &lt;pdf&gt;</c>
/// to print a PDF without UI. SumatraPDF renders the PDF internally and submits to the
/// Windows print spooler; we don't need to load any native renderer into our process.
///
/// <para>SumatraPDF reports its spooler job ID via stdout in the form
/// <c>SPOOLER_JOB_ID=&lt;int&gt;</c> when invoked with <c>-print-stat</c>. We capture this
/// and return it so <c>WindowsPrinterService.HasPrintedAsync</c> can poll the spooler
/// for physical-print confirmation. If the line is absent (older SumatraPDF versions,
/// or stdout swallowed by a shell), we fall back to a synthetic spooler ID and the
/// companion will surface the job as <c>UncertainSpooled</c> after the print-confirm
/// timeout (see <c>JobProcessor.ProcessOneAsync</c>).</para>
///
/// <para>Replaces the previous <c>PdfiumPdfRenderer</c> in-process renderer, which was
/// removed because PdfiumViewer targets .NET Framework (NU1701) and pulled vulnerable
/// transitive deps (NU1903).</para>
/// </summary>
public sealed class SumatraPdfPrinter : IPdfRenderer
{
    private readonly IOptionsMonitor<PrinterOptions> _options;
    private readonly ILogger<SumatraPdfPrinter> _logger;

    public SumatraPdfPrinter(IOptionsMonitor<PrinterOptions> options, ILogger<SumatraPdfPrinter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> PrintAsync(
        string pdfPath, string printerFullName,
        string? pageRange = null, int copies = 1,
        bool? color = null, string? paperSize = null, string? duplex = null,
        CancellationToken ct = default)
    {
        var exePath = ResolveExePath();
        if (!File.Exists(exePath))
            throw new FileNotFoundException("SumatraPDF.exe not found at: " + exePath);

        // Phase 5 fix: ALL settings go into ONE comma-separated
        // -print-settings argument. Emitting several separate
        // -print-settings flags made SumatraPDF honor only the last one
        // (e.g. copies silently discarded when color/paper followed) —
        // silent wrong physical output.
        var printSettings = BuildPrintSettings(copies, pageRange, color, paperSize, duplex);

        var args = new List<string>
        {
            "-silent",
            "-print-to", $"\"{printerFullName}\"",
            "-print-settings", printSettings,
            "-print-stat",
            $"\"{pdfPath}\""
        };

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start SumatraPDF");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"SumatraPDF exited with {p.ExitCode}: {stderr}");

        // Try to parse a SPOOLER_JOB_ID line.
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string Tag = "SPOOLER_JOB_ID=";
            if (line.StartsWith(Tag, StringComparison.OrdinalIgnoreCase) && int.TryParse(line[Tag.Length..], out var id) && id > 0)
            {
                _logger.LogInformation("SumatraPDF spooled job #{Id}", id);
                return id;
            }
        }
        // Phase 5 fix: NO synthetic spooler id. A random id would make the
        // confirmation poll find no such spooler job and conclude "completed
        // and purged" — reporting success with zero physical confirmation.
        // Return 0 (= no id, per IPdfRenderer contract); WindowsPrinterService
        // maps this to a null SpoolSubmission.SpoolerJobId and the job
        // processor parks the job as outcome-uncertain instead of printed.
        _logger.LogInformation("SumatraPDF completed but did not report a spooler job ID — physical outcome cannot be confirmed");
        return 0;
    }

    private string ResolveExePath()
    {
        var configured = _options.CurrentValue.SumatraExePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var appDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(appDir, "runtimes", "win-x64", "native", "SumatraPDF.exe");
        return File.Exists(bundled) ? bundled : Path.Combine(appDir, "SumatraPDF.exe");
    }

    /// <summary>
    /// Builds the single comma-separated <c>-print-settings</c> argument:
    /// copies ("2x"), page range ("1-3,5-6"), color/monochrome, paper, duplex.
    /// All tokens go into ONE argument — SumatraPDF honours only the last of
    /// several separate -print-settings flags.
    /// </summary>
    public static string BuildPrintSettings(
        int copies, string? pageRange,
        bool? color, string? paperSize, string? duplex)
    {
        var settings = new List<string> { $"{Math.Max(1, copies)}x" };

        var range = NormalizePageRange(pageRange);
        if (!string.IsNullOrEmpty(range))
            settings.Add(range);

        // Color: SumatraPDF uses `-print-settings color` to force color and
        // `-print-settings monochrome` to force grayscale. The caller may set
        // either legacy `bool? Color` or `string? ColorMode`; bool wins when set.
        if (color == true)
            settings.Add("color");
        else if (color == false)
            settings.Add("monochrome");

        if (!string.IsNullOrEmpty(paperSize))
            settings.Add($"paper={paperSize}");

        if (!string.IsNullOrEmpty(duplex))
        {
            settings.Add(duplex.ToLowerInvariant() switch
            {
                "simplex"    => "simplex",
                "vertical"   => "duplexshort",
                "horizontal" => "duplexlong",
                _            => duplex
            });
        }

        return string.Join(",", settings);
    }

    /// <summary>
    /// Normalises a page-range string to SumatraPDF's plain token form
    /// ("1-3,5-6"). SumatraPDF has NO "r" prefix for ranges — the previous
    /// "r1-3,5-6" token was invalid and silently IGNORED, so the whole
    /// document printed even when the customer selected a subset of pages.
    /// ALL whitespace is stripped (segment edges AND inside ranges — CI caught
    /// " 1 - 3 , 5 " being rejected because inner spaces survived the trim) and
    /// empty/non-numeric segments are dropped.
    /// </summary>
    public static string? NormalizePageRange(string? pageRange)
    {
        if (string.IsNullOrWhiteSpace(pageRange)) return null;
        var parts = pageRange.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var valid = parts
            .Select(p => new string(p.Where(c => !char.IsWhiteSpace(c)).ToArray()))
            .Where(p => p.Length > 0 && p.All(c => char.IsAsciiDigit(c) || c == '-'))
            .ToArray();
        return valid.Length > 0 ? string.Join(",", valid) : null;
    }
}
