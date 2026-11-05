namespace PrintShopCompanion.Infrastructure.Queue;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Background job processor. Pulls jobs from the durable queue, downloads the
/// PDF from the backend, submits it to the printer, waits for the spooler to
/// confirm physical printing, reports the result back to the backend, and
/// updates the queue. All transitions honour the project's hard rules:
/// <list type="bullet">
///  <item>Persist before processing — the queue is the source of truth; this processor never holds an unpersisted job.</item>
///  <item>Never print twice — the queue is idempotent on JobId; a redelivered job is acked but not reprinted if already in terminal state.</item>
///  <item>Never report success just because the API call succeeded — <c>ReportedPrinted</c> requires the spooler to have confirmed physical printing first.</item>
/// </list>
/// </summary>
public sealed class JobProcessor
{
    private readonly IJobQueue _queue;
    private readonly IPrintShopBackend _backend;
    private readonly IPrinterService _printers;
    private readonly IPdfRenderer _renderer;
    private readonly IOptions<PrinterOptions> _options;
    private readonly ILogger<JobProcessor> _logger;
    private readonly SemaphoreSlim _concurrency = new(1, 1);
    private CancellationTokenSource? _loopCts;

    public JobProcessor(
        IJobQueue queue,
        IPrintShopBackend backend,
        IPrinterService printers,
        IPdfRenderer renderer,
        IOptions<PrinterOptions> options,
        ILogger<JobProcessor> logger)
    {
        _queue = queue;
        _backend = backend;
        _printers = printers;
        _renderer = renderer;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RunAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try { _loopCts?.Cancel(); } catch { /* ignore */ }
        _loopCts?.Dispose();
        _loopCts = null;
        await Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("JobProcessor loop started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _concurrency.WaitAsync(ct);
                try
                {
                    await ProcessOneAsync(ct);
                }
                finally { _concurrency.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JobProcessor iteration failed; backing off 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        _logger.LogInformation("JobProcessor loop stopped");
    }

    private async Task ProcessOneAsync(CancellationToken ct)
    {
        var job = await _queue.DequeueAsync(ct);
        if (job is null)
        {
            await Task.Delay(_options.Value.PollFallbackInterval, ct);
            return;
        }

        using var scope = _logger.BeginScope("Job {JobId} attempt {Attempt}", job.JobId, job.AttemptCount);

        // Re-delivered terminal jobs: ack-and-skip. This is the "never print twice" guard.
        if (job.Status is JobStatus.Printed or JobStatus.ReportedPrinted or JobStatus.Cancelled)
        {
            _logger.LogInformation("Skipping already-terminal job");
            await _backend.AcknowledgeJobAsync(job.JobId, ct);
            return;
        }

        // 1. Download the PDF if we don't already have it cached.
        if (job.PdfUrl is null)
        {
            await FailAsync(job, "Job has no PDF URL", permanent: true, ct);
            return;
        }

        if (string.IsNullOrEmpty(job.LocalPdfPath) || !File.Exists(job.LocalPdfPath))
        {
            try
            {
                var dl = await _backend.DownloadPdfAsync(job.JobId, job.PdfUrl, job.PdfSha256, ct);
                job.LocalPdfPath = dl.LocalPath;
                job.PdfSha256 = dl.Sha256;
                await _queue.UpdateAsync(job, ct);
                _logger.LogInformation("Downloaded PDF ({Bytes} bytes, sha256={Sha})", dl.SizeBytes, dl.Sha256);
            }
            catch (Exception ex)
            {
                await FailAsync(job, "PDF download failed: " + ex.Message, permanent: false, ct);
                return;
            }
        }

        // 2. Acknowledge the job BEFORE submitting to the spooler. The backend will not
        //    redeliver an acked job; if we crash between here and Submit, the queue's
        //    in-flight replay will resubmit on next start.
        try
        {
            await _backend.AcknowledgeJobAsync(job.JobId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ack failed; will retry on next delivery");
        }

        // 3. Submit to the spooler.
        SpoolSubmission sub;
        try
        {
            sub = await _printers.SubmitAsync(job, ct);
            job.SpoolerJobId = sub.SpoolerJobId;
            job.Status = JobStatus.Spooled;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _queue.UpdateAsync(job, ct);
            _logger.LogInformation("Submitted to spooler job #{SpoolerId} on {Printer}", sub.SpoolerJobId, sub.PrinterFullName);
        }
        catch (Exception ex)
        {
            await FailAsync(job, "Spooler submission failed: " + ex.Message, permanent: false, ct);
            return;
        }

        // 4. Wait for spooler to confirm physical printing.
        var deadline = DateTimeOffset.UtcNow + _options.Value.PrintConfirmTimeout;
        bool printed = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { printed = await _printers.HasPrintedAsync(sub.SpoolerJobId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Spooler status poll failed; will retry"); }
            if (printed) break;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (!printed)
        {
            await FailAsync(job, "Spooler did not confirm physical print within timeout", permanent: false, ct);
            return;
        }

        // 5. Mark Printed in the queue BEFORE reporting to the backend. ReportedPrinted is
        //    a separate state that is only set after the backend confirms receipt.
        var printedAt = DateTimeOffset.UtcNow;
        await _queue.MarkPrintedAsync(job.JobId, printedAt, sub.SpoolerJobId, ct);
        _logger.LogInformation("Spooler confirmed physical print at {Time}", printedAt);

        // 6. Report to the backend.
        try
        {
            await _backend.ReportJobStatusAsync(job.JobId, printedAt, failedAt: null, errorMessage: null, ct);
            await _queue.MarkReportedPrintedAsync(job.JobId, ct);
            _logger.LogInformation("Reported Printed to backend");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Report to backend failed; will retry on next heartbeat");
            // The job stays in Printed state. The heartbeat task will eventually retry the report.
        }
    }

    private async Task FailAsync(PrintJobRecord job, string error, bool permanent, CancellationToken ct)
    {
        if (permanent || job.AttemptCount >= _options.Value.MaxAttempts)
            await _queue.MarkFailedAsync(job.JobId, error, permanent: true, ct);
        else
            await _queue.MarkFailedAsync(job.JobId, error, permanent: false, ct);

        try
        {
            await _backend.ReportJobStatusAsync(job.JobId, printedAt: null, failedAt: DateTimeOffset.UtcNow, errorMessage: error, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backend report of failure did not succeed; will retry on heartbeat");
        }
    }
}
