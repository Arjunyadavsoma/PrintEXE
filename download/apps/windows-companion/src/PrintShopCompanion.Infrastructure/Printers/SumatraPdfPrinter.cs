namespace PrintShopCompanion.Infrastructure.Printers;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

/// <summary>
/// SumatraPDF command-line fallback renderer. Invokes the bundled
/// <c>SumatraPDF.exe</c> with <c>-silent -print-to-default -print-to &lt;printer&gt;</c>
/// to print a PDF without UI. Useful when PDFium fails on a particular driver
/// (some Indian-market dot-matrix / ESC/POS label printers).
///
/// <para>SumatraPDF reports its spooler job ID via stdout in the form
/// <c>SPOOLER_JOB_ID=&lt;int&gt;</c> when invoked with <c>-print-to -print-stat</c>.
/// We capture this and return it; if absent we fall back to a synthetic ID.</para>
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
        if (color == false)
            args.AddRange(new[] { "-print-settings", "color" });

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
