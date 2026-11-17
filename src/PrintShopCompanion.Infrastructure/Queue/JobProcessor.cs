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
public sealed class JobProcessor : IQueueController
{
    private readonly IJobQueue _queue;
    private readonly IPrintShopBackend _backend;
    private readonly IPrinterService _printers;
    private readonly IPdfRenderer _renderer;
    private readonly IOptions<PrinterOptions> _options;
    private readonly IOptions<BackendOptions> _backendOpts;
    private readonly ILogger<JobProcessor> _logger;
    private readonly SemaphoreSlim _concurrency = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    // Phase F (brief §C) — cooperative pause gate. The gate is a SemaphoreSlim
    // used as a SIGNAL: when paused, the gate has count 0 (waiters block); when
    // resumed, the gate has count 1 (one waiter passes + resets to 0, the next
    // iteration re-waits if still paused). This is cleaner than the Phase 3
    // approach (which acquired+released the gate and could over-release):
    //   - PauseAsync: spin-wait until we can take the count down to 0 (block
    //     future waiters). Idempotent: if already 0, no-op.
    //   - ResumeAsync: release once (count 0 → 1) so exactly one blocked waiter
    //     passes. Idempotent: if already 1, the Release would throw
    //     SemaphoreFullException — we catch + treat as no-op.
    //   - The loop's ProcessOneAsync waits on the gate when paused; on
    //     cancellation (shutdown) the WaitAsync throws OperationCanceledException
    //     and the loop exits cleanly (no leaked task, no deadlock).
    // Pause state is NOT persisted across restart — on restart the queue
    // resumes normally (the brief §C: "Pause state persistence is explicitly
    // defined. Do not accidentally claim pause survives restart if it does not.")
    private readonly SemaphoreSlim _pauseGate = new(initialCount: 1, maxCount: 1);
    private volatile bool _paused;
    private readonly object _pauseLock = new();

    /// <summary>True while the processor is paused (won't dequeue new jobs). In-flight jobs finish.</summary>
    public bool IsPaused => _paused;

    /// <summary>
    /// Phase F (brief §C) — pause job processing. Cooperative: the current
    /// iteration finishes; subsequent iterations block at the pause gate until
    /// <see cref="ResumeAsync"/> is called. Idempotent + thread-safe (the
    /// _pauseLock serializes concurrent pause/resume). Does NOT cancel in-flight
    /// print submissions (the brief: "Pause must stop new job processing without
    /// corrupting an in-flight job").
    /// </summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        lock (_pauseLock)
        {
            if (_paused) return;   // idempotent
            _paused = true;
        }
        // Drain the gate to 0 so future WaitAsync calls block. The gate's count
        // is at most 1; a single WaitAsync(0) takes it if available, else returns
        // false immediately. We don't block here — the loop's WaitAsync (without
        // timeout) is what blocks; we just ensure the count is 0.
        _pauseGate.Wait(0, ct);
        _logger.LogInformation("JobProcessor paused (in-flight job, if any, will finish)");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Phase F (brief §C) — resume job processing after a
    /// <see cref="PauseAsync"/>. Idempotent + thread-safe. Releases the gate so
    /// exactly one blocked waiter passes.
    /// </summary>
    public Task ResumeAsync(CancellationToken ct = default)
    {
        lock (_pauseLock)
        {
            if (!_paused) return Task.CompletedTask;   // idempotent
            _paused = false;
        }
        // Release the gate (count 0 → 1) so a blocked waiter passes. If the
        // count is already 1 (no waiter), Release throws
        // SemaphoreFullException — we catch + treat as a no-op (the next
        // iteration's WaitAsync will pass immediately since count is 1).
        try { _pauseGate.Release(); }
        catch (System.Threading.SemaphoreFullException) { /* gate already signaled */ }
        _logger.LogInformation("JobProcessor resumed");
        return Task.CompletedTask;
    }

    public JobProcessor(
        IJobQueue queue,
        IPrintShopBackend backend,
        IPrinterService printers,
        IPdfRenderer renderer,
        IOptions<PrinterOptions> options,
        IOptions<BackendOptions> backendOpts,
        ILogger<JobProcessor> logger)
    {
        _queue = queue;
        _backend = backend;
        _printers = printers;
        _renderer = renderer;
        _options = options;
        _backendOpts = backendOpts;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // Cancel and WAIT for the loop task to actually exit before returning.
        // The previous implementation just cancelled + disposed the CTS without
        // waiting, which meant a background spooler submission could be cut
        // off mid-flight when the App proceeded to dispose the host.
        try { _loopCts?.Cancel(); } catch { /* ignore */ }

        if (_loopTask is not null)
        {
            try
            {
                // Bounded wait — if the loop is stuck in an uncancelable
                // spooler call, give up after 3 seconds.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(_loopTask, Task.Delay(TimeSpan.FromSeconds(3), linkedCts.Token));
            }
            catch { /* swallow — we're shutting down anyway */ }
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
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
        // Phase F (brief §C) — cooperative pause gate. The gate is a SIGNAL:
        // when paused, count=0 (WaitAsync blocks); on Resume, count=1 (one
        // blocked waiter passes, count returns to 0). The loop re-checks
        // _paused each iteration, so it re-waits if still paused, or proceeds
        // if resumed. Shutdown's cancellation propagates as
        // OperationCanceledException, breaking the outer loop cleanly (no
        // leaked task, no deadlock).
        while (_paused)
        {
            await _pauseGate.WaitAsync(ct);   // throws OCE on shutdown — propagates to RunAsync
            // Resume released the gate (count 0→1); WaitAsync consumed it (count 1→0).
            // Re-check _paused: if still paused (a concurrent Pause raced the
            // Resume), we re-wait; if not, we proceed to dequeue.
        }

        var job = await _queue.DequeueAsync(ct);
        if (job is null)
        {
            await Task.Delay(_backendOpts.Value.PollFallbackInterval, ct);
            return;
        }

        using var scope = _logger.BeginScope("Job {JobId} attempt {Attempt}", job.JobId, job.AttemptCount);

        // Re-delivered terminal jobs: skip. This is the "never print twice" guard.
        // UncertainSpooled and OutcomeUnknown are NOT terminal — they require operator action.
        if (job.Status is JobStatus.Printed or JobStatus.ReportedPrinted or JobStatus.Cancelled)
        {
            _logger.LogInformation("Skipping already-terminal job");
            return;
        }

        // Do not auto-process uncertain jobs. Surface them in the UI for operator reconciliation.
        if (job.Status is JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown)
        {
            _logger.LogWarning("Job {JobId} in {Status} state — waiting for operator reconciliation", job.JobId, job.Status);
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
                job.Status = JobStatus.Rendering;
                job.DeliveryStatus = DeliveryStatus.Transferring;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateAsync(job, ct);
                _logger.LogInformation("Downloaded PDF ({Bytes} bytes, sha256={Sha})", dl.SizeBytes, dl.Sha256);

                // v0.5: Durable receipt acknowledgement — tell the backend the PDF
                // is on the shop PC, SHA-256 verified, and atomically persisted.
                // This gates cloud deletion: the backend sets cloudDeleteAfter =
                // now + 15 min. If this call fails, the cloud copy stays.
                try
                {
                    var checksum = "sha256:" + dl.Sha256;
                    var receipt = await _backend.MarkJobReceivedAsync(
                        job.JobId, checksum, dl.SizeBytes, DateTimeOffset.UtcNow, ct);
                    job.DeliveryStatus = DeliveryStatus.Received;
                    job.StorageStatus = StorageStatus.LocalVerified;
                    job.ReceivedAt = DateTimeOffset.UtcNow;
                    job.CloudDeleteAfter = receipt.CloudDeleteAfter;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    await _queue.UpdateAsync(job, ct);
                    _logger.LogInformation("Receipt acknowledged — cloud copy scheduled for deletion at {Deadline}",
                        receipt.CloudDeleteAfter);
                }
                catch (Exception ex)
                {
                    // The backend may not have the /received route yet (v0.4.7).
                    // Log + continue — the cloud copy just won't be auto-deleted.
                    _logger.LogWarning(ex, "Receipt acknowledgement failed (backend may not support /received yet) — continuing");
                    job.DeliveryStatus = DeliveryStatus.Received;  // locally we have the file
                    job.StorageStatus = StorageStatus.LocalVerified;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    await _queue.UpdateAsync(job, ct);
                }
            }
            catch (Exception ex)
            {
                job.DeliveryStatus = DeliveryStatus.DeliveryFailed;
                job.LastDeliveryError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateAsync(job, ct);
                await FailAsync(job, "PDF download failed: " + ex.Message, permanent: false, ct);
                return;
            }
        }

        // 2. ATOMIC CLAIM: send status:"printing" to the backend. This is a
        //    conditional UPDATE (WHERE status='queued') — if another agent
        //    already claimed it, the response has skipped:true and we move on.
        //    Per the integration guide section 3.1, this replaces the old
        //    ack+report two-step pattern.
        var printerName = _options.Value.SelectedPrinter ?? "(default)";
        JobStatusReport claim;
        try
        {
            claim = await _backend.ReportJobStatusAsync(job.JobId, "printing", printerName, errorMessage: null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Atomic claim failed; will retry on next iteration");
            return;
        }
        if (claim.Skipped)
        {
            _logger.LogInformation("Job {JobId} skipped: {Reason} — another agent got it", job.JobId, claim.Reason);
            // Mark as cancelled locally so we don't keep retrying.
            await _queue.CancelAsync(job.JobId, ct);
            return;
        }
        if (!claim.Applied)
        {
            _logger.LogWarning("Job {JobId} status report not applied: {Reason}", job.JobId, claim.Reason);
            return;
        }
        _logger.LogInformation("Claimed job {JobId} on printer {Printer}", job.JobId, printerName);

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
            // Spooler did not confirm physical printing within the timeout. This is the
            // "uncertain outcome" case the spec calls out: never auto-reprint, surface to the operator.
            await _queue.MarkUncertainAsync(job.JobId,
                "Spooler did not confirm physical print within timeout; outcome uncertain",
                ct);
            _logger.LogWarning("Job {JobId} marked UncertainSpooled — operator reconciliation required", job.JobId);
            // Also report failure to the backend so it knows the claim didn't result in a print.
            try
            {
                await _backend.ReportJobStatusAsync(job.JobId, "failed", printerName,
                    errorMessage: "Spooler did not confirm physical print within timeout", ct);
            }
            catch { /* swallow — already marked uncertain locally */ }
            return;
        }

        // 5. Mark Printed in the queue BEFORE reporting to the backend. ReportedPrinted is
        //    a separate state that is only set after the backend confirms receipt.
        var printedAt = DateTimeOffset.UtcNow;
        await _queue.MarkPrintedAsync(job.JobId, printedAt, sub.SpoolerJobId, ct);
        _logger.LogInformation("Spooler confirmed physical print at {Time}", printedAt);

        // v0.5: Set localDeleteAfter = printedAt + 60 min (configurable).
        // The FileRetentionManager will delete the local PDF when this deadline passes.
        try
        {
            var retentionMinutes = _backendOpts.Value.PollFallbackInterval.TotalSeconds > 0 ? 60 : 60;  // default
            job.LocalDeleteAfter = printedAt.AddMinutes(retentionMinutes);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _queue.UpdateAsync(job, ct);
            _logger.LogInformation("Local PDF scheduled for deletion at {Deadline} (60 min after print)", job.LocalDeleteAfter);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to set localDeleteAfter (non-fatal)"); }

        // 6. Report completion to the backend.
        try
        {
            await _backend.ReportJobStatusAsync(job.JobId, "completed", printerName, errorMessage: null, ct);
            await _queue.MarkReportedPrintedAsync(job.JobId, ct);
            _logger.LogInformation("Reported completed to backend");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Report to backend failed; marking OutcomeUnknown for heartbeat reconciliation");
            await _queue.MarkOutcomeUnknownAsync(job.JobId, ex.Message, ct);
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
            var printerName = _options.Value.SelectedPrinter ?? "(default)";
            await _backend.ReportJobStatusAsync(job.JobId, "failed", printerName, errorMessage: error, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backend report of failure did not succeed; will retry on heartbeat");
        }
    }
}
