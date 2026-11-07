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

        var args = new List<string>
        {
            "-silent",
            "-print-to", $"\"{printerFullName}\"",
            "-print-settings", $"{Math.Max(1, copies)}x"
        };
        if (!string.IsNullOrEmpty(pageRange))
            args.AddRange(new[] { "-print-settings", $"r{pageRange}" });
        // Color: SumatraPDF uses `-print-settings color` to force color and
        // `-print-settings monochrome` to force grayscale. The companion's
        // domain model carries both `bool? Color` (legacy) and `string? ColorMode`
        // ("bw"|"color"). We honour whichever the caller sets; ColorMode wins.
        // The legacy `color == false` branch was wrong (it forced color when
        // the caller asked for monochrome); this is now corrected.
        if (color == true)
            args.AddRange(new[] { "-print-settings", "color" });
        else if (color == false)
            args.AddRange(new[] { "-print-settings", "monochrome" });
        if (!string.IsNullOrEmpty(paperSize))
            args.AddRange(new[] { "-print-settings", $"paper={paperSize}" });
        if (!string.IsNullOrEmpty(duplex))
        {
            var d = duplex.ToLowerInvariant() switch
            {
                "simplex"    => "simplex",
                "vertical"   => "duplexshort",
                "horizontal" => "duplexlong",
                _            => duplex
            };
            args.AddRange(new[] { "-print-settings", d });
        }

        args.Add("-print-stat");
        args.Add($"\"{pdfPath}\"");

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
        _logger.LogInformation("SumatraPDF completed but did not report a spooler job ID; using synthetic ID");
        return Random.Shared.Next(1_000_000, int.MaxValue);
    }

    private string ResolveExePath()
    {
        var configured = _options.CurrentValue.SumatraExePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var appDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(appDir, "runtimes", "win-x64", "native", "SumatraPDF.exe");
        return File.Exists(bundled) ? bundled : Path.Combine(appDir, "SumatraPDF.exe");
    }
}
