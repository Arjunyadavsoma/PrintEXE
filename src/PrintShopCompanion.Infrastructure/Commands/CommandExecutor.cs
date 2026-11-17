namespace PrintShopCompanion.Infrastructure.Commands;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;  // ThisApp (internal, same assembly)

/// <summary>
/// v1 contract §4 + §8 + Phase F (brief §D) — validates and executes a single
/// remote command against the companion's local application services. This
/// class is the <b>execution</b> half of the command lifecycle; the
/// <see cref="CommandPoller"/> handles fetch/ack/result-reporting.
///
/// <para><b>Security invariants (contract §8 Phase 8 + Phase F §D):</b></para>
/// <list type="bullet">
///   <item>Only the 7 allowlisted command types are accepted (see <see cref="CommandType"/>).
///   Unsupported types are rejected with a structured error — never executed.</item>
///   <item>Command payloads are parsed as JSON but NEVER shell-executed, NEVER
///   treated as a file path, NEVER treated as a URL. <c>cancel_job</c> and
///   <c>retry_job</c> extract a <c>jobId</c> (UUID string) and pass it to the
///   queue; they do not touch the filesystem or spooler directly.</item>
///   <item>Payload size is capped (default 64 KiB) — oversized payloads are
///   rejected without execution.</item>
///   <item>Expired commands (<see cref="PendingCommand.IsExpired"/>) are rejected
///   without execution and reported as failed with reason "expired".</item>
///   <item>Printing commands (<c>test_print</c>) require EXPLICIT OPERATOR
///   AUTHORIZATION — they never silently print paper. The payload must carry
///   an <c>authorizedBy</c> field naming the operator who confirmed; the
///   shopkeeper confirms on the web dashboard before issuing the command.</item>
/// </list>
///
/// <para><b>Per-command retry-safety classification</b> (Phase F §A):</para>
/// <list type="bullet">
///   <item><c>refresh_printers</c> — retryable (no side effect).</item>
///   <item><c>diagnostics</c> — retryable (read-only, redacted).</item>
///   <item><c>pause</c> / <c>resume</c> — idempotent desired-state operations.</item>
///   <item><c>cancel_job</c> — verify current job state before retrying (a job
///   already printing is NOT cancelled; a job already terminal is a no-op).</item>
///   <item><c>retry_job</c> — NEVER auto-retry a job whose previous print
///   attempt has an uncertain physical outcome (<c>UncertainSpooled</c> /
///   <c>OutcomeUnknown</c>). The shopkeeper must resolve those manually.</item>
///   <item><c>test_print</c> — NEVER auto-repeat after uncertain execution
///   (the synthetic test job is durable; if it ends in an uncertain state,
///   a duplicate <c>test_print</c> command is a no-op, not a reprint).</item>
/// </list>
/// </summary>
public sealed class CommandExecutor
{
    private readonly IPrinterService _printers;
    private readonly IJobQueue _queue;
    private readonly IPrintShopBackend _backend;
    private readonly IQueueController _queueController;   // Phase 3: pause/resume
    private readonly IOptionsMonitor<PrinterOptions> _printerOpts;
    private readonly ILogger<CommandExecutor> _logger;

    /// <summary>
    /// Phase F (brief §D) — maximum payload size accepted for any command.
    /// 64 KiB is generous for the largest legitimate payload (a list of
    /// printer names, a diagnostics snapshot) and blocks abuse. The SaaS
    /// does not impose a payload-size cap at the API edge, so this is the
    /// companion's defense.
    /// </summary>
    private const int MaxPayloadBytes = 64 * 1024;

    public CommandExecutor(
        IPrinterService printers,
        IJobQueue queue,
        IPrintShopBackend backend,
        IQueueController queueController,
        IOptionsMonitor<PrinterOptions> printerOpts,
        ILogger<CommandExecutor> logger)
    {
        _printers = printers;
        _queue = queue;
        _backend = backend;
        _queueController = queueController;
        _printerOpts = printerOpts;
        _logger = logger;
    }

    /// <summary>
    /// Validate + execute a pending command. Returns a structured result that
    /// the poller forwards to the backend via
    /// <see cref="IPrintShopBackend.ReportCommandStatusAsync"/> with status
    /// <c>succeeded</c>, <c>failed</c>, or <c>outcome_unknown</c>.
    /// </summary>
    public async Task<CommandExecutionResult> ExecuteAsync(PendingCommand command, CancellationToken ct = default)
    {
        // 1. Validate type against the allowlist (contract §8 Phase 8).
        if (!CommandType.IsAllowed(command.CommandType))
        {
            _logger.LogWarning("Rejecting unsupported command {CommandId} type={Type}", command.Id, command.CommandType);
            return Failed(command.Id, $"Unsupported command type '{command.CommandType}'. Allowed: {string.Join(", ", CommandType.All)}");
        }

        // 2. Validate expiry (defense in depth — the backend already filters).
        if (command.IsExpired)
        {
            _logger.LogInformation("Rejecting expired command {CommandId} type={Type} expiredAt={ExpiresAt}",
                command.Id, command.CommandType, command.ExpiresAt);
            return Failed(command.Id, $"Command expired at {command.ExpiresAt:O} (now is {DateTimeOffset.UtcNow:O}).");
        }

        // 3. Phase F (brief §D) — payload size cap. Applies to all commands.
        if (command.PayloadJson is { Length: > MaxPayloadBytes })
        {
            _logger.LogWarning("Rejecting oversized command {CommandId} type={Type} payloadBytes={Bytes} (max {Max})",
                command.Id, command.CommandType, command.PayloadJson.Length, MaxPayloadBytes);
            return Failed(command.Id, $"Payload exceeds the {MaxPayloadBytes}-byte limit.");
        }

        // 4. Dispatch to the per-type handler. Any exception → structured failure.
        try
        {
            return command.CommandType.ToLowerInvariant() switch
            {
                var t when t == CommandType.RefreshPrinters => await RefreshPrintersAsync(command, ct),
                var t when t == CommandType.Pause => await PauseAsync(command, ct),
                var t when t == CommandType.Resume => await ResumeAsync(command, ct),
                var t when t == CommandType.CancelJob => await CancelJobAsync(command, ct),
                var t when t == CommandType.RetryJob => await RetryJobAsync(command, ct),
                var t when t == CommandType.TestPrint => await TestPrintAsync(command, ct),
                var t when t == CommandType.Diagnostics => await DiagnosticsAsync(command, ct),
                _ => Failed(command.Id, $"Unmapped command type '{command.CommandType}' (allowlist passed but no handler)."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandId} ({Type}) threw during execution", command.Id, command.CommandType);
            return Failed(command.Id, $"Execution threw: {ex.Message}");
        }
    }

    // ────────────────────────── per-type handlers ──────────────────────────

    /// <summary>refresh_printers — re-enumerate Windows printers + push via heartbeat. Retryable (no side effect).</summary>
    private async Task<CommandExecutionResult> RefreshPrintersAsync(PendingCommand command, CancellationToken ct)
    {
        var printers = _printers.DiscoverPrinters();
        var selected = _printers.GetSelectedPrinter();
        var names = printers.Select(p => p.FullName).ToList();
        var result = new
        {
            printers = names,
            defaultPrinter = selected?.FullName,
            count = names.Count,
        };
        // Push the current selected printer up to the backend via heartbeat
        // (the contract §5 says printer inventory is reported via heartbeat's
        // printerName field — a dedicated inventory endpoint is future work).
        try
        {
            await _backend.HeartbeatAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat after refresh_printers failed (non-fatal — printers still returned)");
        }
        _logger.LogInformation("refresh_printers: discovered {Count} printers, default={Default}", names.Count, selected?.FullName);
        return Succeeded(command.Id, JsonSerializer.Serialize(result));
    }

    /// <summary>
    /// pause — Phase 3 (brief §7): cooperatively pause the JobProcessor's
    /// dequeue loop. An in-flight job (if any) finishes cleanly; subsequent
    /// dequeues block until <c>resume</c>. Idempotent desired-state operation.
    /// The pause is NOT persisted across restart (the contract treats pause
    /// as a transient shopkeeper action).
    /// </summary>
    private async Task<CommandExecutionResult> PauseAsync(PendingCommand command, CancellationToken ct)
    {
        var wasAlreadyPaused = _queueController.IsPaused;
        await _queueController.PauseAsync(ct);
        var result = new { paused = true, wasAlreadyPaused };
        _logger.LogInformation("pause command {CommandId}: queue paused (wasAlreadyPaused={WasAlreadyPaused})", command.Id, wasAlreadyPaused);
        return Succeeded(command.Id, JsonSerializer.Serialize(result));
    }

    /// <summary>resume — Phase 3 (brief §7): resume the JobProcessor's dequeue loop after a pause. Idempotent desired-state operation.</summary>
    private async Task<CommandExecutionResult> ResumeAsync(PendingCommand command, CancellationToken ct)
    {
        var wasPaused = _queueController.IsPaused;
        await _queueController.ResumeAsync(ct);
        var result = new { resumed = true, wasPaused };
        _logger.LogInformation("resume command {CommandId}: queue resumed (wasPaused={WasPaused})", command.Id, wasPaused);
        return Succeeded(command.Id, JsonSerializer.Serialize(result));
    }

    /// <summary>
    /// cancel_job — Phase F (brief §D): verify job state before cancelling.
    /// Only <c>Pending</c> + <c>Downloading</c> + <c>Rendering</c> jobs are
    /// safe to cancel (they haven't reached the spooler). A <c>Spooled</c> /
    /// <c>Printing</c> job is already in the Windows spooler — cancelling the
    /// local row would not stop the physical print; reject with the reason.
    /// Terminal jobs (<c>Printed</c> / <c>ReportedPrinted</c> / <c>Failed</c>
    /// / <c>Cancelled</c>) are a no-op (idempotent). Uncertain jobs
    /// (<c>UncertainSpooled</c> / <c>OutcomeUnknown</c>) require operator
    /// resolution, NOT a remote cancel.
    /// </summary>
    private async Task<CommandExecutionResult> CancelJobAsync(PendingCommand command, CancellationToken ct)
    {
        var (jobId, parseError) = ParseJobId(command.PayloadJson);
        if (parseError is not null)
            return Failed(command.Id, parseError);
        try
        {
            var jobs = await _queue.ListAsync(cancellationToken: ct);
            var job = jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
                return Failed(command.Id, $"Job {jobId} not found in the local queue (it may have been purged or never assigned to this device).");

            // Verify the job is in a safe-to-cancel state.
            if (job.Status is JobStatus.Spooled or JobStatus.Printing)
            {
                _logger.LogWarning("cancel_job {CommandId}: rejected — job {JobId} is already in the Windows spooler ({Status}); cancelling the local row would not stop the physical print.",
                    command.Id, jobId, job.Status);
                return Failed(command.Id,
                    $"Job {jobId} is already in the Windows spooler ({job.Status}). Cancelling the local queue row would NOT stop the physical print. Resolve the spooler job manually on the printer.");
            }
            if (job.Status is JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown)
            {
                _logger.LogWarning("cancel_job {CommandId}: rejected — job {JobId} has an uncertain physical outcome ({Status}); requires operator resolution, not a remote cancel.",
                    command.Id, jobId, job.Status);
                return Failed(command.Id,
                    $"Job {jobId} has an uncertain physical outcome ({job.Status}). An operator must resolve it manually (verify the paper, then mark Resolved or Reprint) — a remote cancel cannot safely resolve an uncertain outcome.");
            }
            if (job.Status is JobStatus.Printed or JobStatus.ReportedPrinted or JobStatus.Failed or JobStatus.Cancelled)
            {
                // Terminal — idempotent no-op.
                _logger.LogInformation("cancel_job {CommandId}: job {JobId} already terminal ({Status}) — no-op", command.Id, jobId, job.Status);
                return Succeeded(command.Id, JsonSerializer.Serialize(new { jobId, cancelled = false, reason = $"Job is already {job.Status} (terminal).", status = job.Status.ToString() }));
            }

            // Safe to cancel (Pending / Downloading / Rendering).
            await _queue.CancelAsync(jobId!, ct);
            _logger.LogInformation("cancel_job {CommandId}: cancelled job {JobId} (was {Status})", command.Id, jobId, job.Status);
            return Succeeded(command.Id, JsonSerializer.Serialize(new { jobId, cancelled = true, previousStatus = job.Status.ToString() }));
        }
        catch (Exception ex)
        {
            return Failed(command.Id, $"cancel_job failed for {jobId}: {ex.Message}");
        }
    }

    /// <summary>
    /// retry_job — Phase F (brief §A + §D): NEVER auto-retry a job whose
    /// previous print attempt has an uncertain physical outcome. Only
    /// <c>Failed</c> jobs (a confirmed failure BEFORE the spooler accepted
    /// the job) may be retried. <c>UncertainSpooled</c> / <c>OutcomeUnknown</c>
    /// jobs require explicit operator resolution — a remote retry could
    /// duplicate the physical print.
    /// </summary>
    private async Task<CommandExecutionResult> RetryJobAsync(PendingCommand command, CancellationToken ct)
    {
        var (jobId, parseError) = ParseJobId(command.PayloadJson);
        if (parseError is not null)
            return Failed(command.Id, parseError);
        try
        {
            var jobs = await _queue.ListAsync(cancellationToken: ct);
            var job = jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
                return Failed(command.Id, $"Job {jobId} not found in the local queue (it may have been purged or never assigned to this device).");

            // Phase F §A: reject uncertain outcomes — NEVER auto-retry.
            if (job.Status is JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown)
            {
                _logger.LogWarning("retry_job {CommandId}: rejected — job {JobId} has an uncertain physical outcome ({Status}); an operator must resolve it (verify the paper, then mark Resolved or Reprint).",
                    command.Id, jobId, job.Status);
                return Failed(command.Id,
                    $"Job {jobId} has an uncertain physical outcome ({job.Status}). A remote retry could DUPLICATE the physical print. An operator must resolve it manually (verify the paper on the printer, then mark Resolved if it printed or Reprint if it did not).");
            }
            // Only Failed jobs (confirmed failure, pre-spooler) can be retried.
            if (job.Status != JobStatus.Failed)
            {
                return Failed(command.Id, $"Job {jobId} is in state {job.Status} — only Failed jobs can be retried (UncertainSpooled/OutcomeUnknown require operator resolution).");
            }
            // Mutate the settable Status.
            job.Status = JobStatus.Pending;
            job.LastError = null;
            job.AttemptCount = 0;
            await _queue.UpdateAsync(job, ct);
            _logger.LogInformation("retry_job {CommandId}: re-queued job {JobId}", command.Id, jobId);
            return Succeeded(command.Id, JsonSerializer.Serialize(new { jobId, retried = true, newStatus = "Pending" }));
        }
        catch (Exception ex)
        {
            return Failed(command.Id, $"retry_job failed for {jobId}: {ex.Message}");
        }
    }

    /// <summary>
    /// test_print — Phase F (brief §D): print a test page. Requires EXPLICIT
    /// OPERATOR AUTHORIZATION — never silently prints paper.
    ///
    /// <para><b>Authorization flow</b>: the web dashboard tells the shopkeeper
    /// "this will print a single test page on the selected printer; confirm
    /// you are at the printer" before issuing the command. The command
    /// payload MUST include an <c>authorizedBy</c> field naming the operator
    /// who confirmed (the SaaS sets this from the shopkeeper's user id). A
    /// <c>test_print</c> without <c>authorizedBy</c> is rejected — the
    /// companion never assumes authorization.</para>
    ///
    /// <para><b>No duplicate execution</b>: the synthetic test job uses a
    /// stable id <c>test-print-{commandId}</c>, so a duplicate
    /// <c>test_print</c> command (idempotency-key replay on the SaaS, or a
    /// redelivery to the companion) is a no-op on the queue
    /// (<c>EnqueueAsync</c> is idempotent on JobId). If a previous
    /// <c>test_print</c> for the same command ended in an uncertain state,
    /// this command does NOT re-enqueue (the operator must resolve the
    /// uncertain job manually — see retry_job's rejection).</para>
    /// </summary>
    private async Task<CommandExecutionResult> TestPrintAsync(PendingCommand command, CancellationToken ct)
    {
        // 1. Parse + require explicit operator authorization.
        var (authorizedBy, authError) = ParseAuthorizedBy(command.PayloadJson);
        if (authError is not null)
        {
            _logger.LogWarning("test_print {CommandId}: rejected — no operator authorization ({Error})", command.Id, authError);
            return Failed(command.Id, authError);
        }

        var selected = _printers.GetSelectedPrinter();
        if (selected is null)
        {
            _logger.LogWarning("test_print {CommandId}: rejected — no selected printer", command.Id);
            return Failed(command.Id, "No printer is selected on this device. Select a printer first (Settings → Printers).");
        }

        // 2. Idempotency: if a test job for this command already exists in a
        // terminal OR uncertain state, do NOT re-enqueue (no duplicate print).
        var testJobId = "test-print-" + command.Id;
        try
        {
            var jobs = await _queue.ListAsync(cancellationToken: ct);
            var existing = jobs.FirstOrDefault(j => j.JobId == testJobId);
            if (existing is not null)
            {
                if (existing.Status is JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown)
                {
                    _logger.LogWarning("test_print {CommandId}: a previous test print for this command has an uncertain outcome ({Status}) — NOT re-enqueuing (operator must resolve).",
                        command.Id, existing.Status);
                    return Failed(command.Id,
                        $"A previous test print for this command (job {testJobId}) has an uncertain physical outcome ({existing.Status}). An operator must resolve it manually before another test print can be issued.");
                }
                if (existing.Status is not (JobStatus.Pending or JobStatus.Downloading or JobStatus.Rendering))
                {
                    // Already in flight or terminal — idempotent no-op.
                    _logger.LogInformation("test_print {CommandId}: job {JobId} already exists in state {Status} — no-op", command.Id, testJobId, existing.Status);
                    return Succeeded(command.Id, JsonSerializer.Serialize(new
                    {
                        jobId = testJobId,
                        printer = selected.FullName,
                        queued = false,
                        reason = $"Test job already in state {existing.Status} (idempotent — no duplicate print).",
                        authorizedBy,
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "test_print {CommandId}: could not check existing test job — proceeding with enqueue (queue idempotency will dedup)", command.Id);
        }

        // 3. Enqueue the synthetic test job (durable; the JobProcessor submits
        // it to the spooler with the same safety invariants as ordinary jobs).
        var record = new PrintJobRecord
        {
            JobId = testJobId,                       // stable id → idempotent on duplicate test_print commands
            ShopId = "",
            DeviceId = "",
            Title = "PrintShop Companion — Test Print",
            PrinterName = selected.FullName,
            Copies = 1,
            Status = JobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            QueuedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _queue.EnqueueAsync(record, ct);
        _logger.LogInformation("test_print {CommandId}: enqueued test job {JobId} on printer {Printer} (authorizedBy={Operator})",
            command.Id, record.JobId, record.PrinterName, authorizedBy);
        return Succeeded(command.Id, JsonSerializer.Serialize(new
        {
            jobId = record.JobId,
            printer = record.PrinterName,
            queued = true,
            authorizedBy,
            note = "Test job queued — the JobProcessor will submit it to the Windows spooler. Verify the paper at the printer.",
        }));
    }

    /// <summary>
    /// diagnostics — Phase F (brief §D): return SANITIZED operational metadata
    /// only. Includes app version, device id, OS, machine name, selected
    /// printer, queue summary. Excludes: file contents, customer documents,
    /// PDF paths, PII, credentials, tokens, spooler job IDs of real jobs.
    /// </summary>
    private async Task<CommandExecutionResult> DiagnosticsAsync(PendingCommand command, CancellationToken ct)
    {
        var selected = _printers.GetSelectedPrinter();
        var jobs = await _queue.ListAsync(cancellationToken: ct);
        var result = new
        {
            companionVersion = ThisApp.Version,
            deviceId = ThisApp.DeviceId.ToString("N"),
            os = Environment.OSVersion.VersionString,
            machineName = Environment.MachineName,
            selectedPrinter = selected?.FullName,
            // Queue summary: status → count only (NO job ids, NO filenames,
            // NO customer data — just aggregate health).
            queueSummary = jobs.GroupBy(j => j.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            queueDepth = jobs.Count,
            timestamp = DateTimeOffset.UtcNow,
        };
        _logger.LogInformation("diagnostics {CommandId}: collected {JobCount} jobs, printer={Printer}",
            command.Id, jobs.Count, selected?.FullName);
        return Succeeded(command.Id, JsonSerializer.Serialize(result));
    }

    // ────────────────────────── helpers ──────────────────────────

    /// <summary>
    /// Parse the <c>{"jobId":"uuid"}</c> payload for cancel_job/retry_job.
    /// Returns (jobId, null) on success or (null, errorMessage) on parse failure.
    /// Never throws — a malformed payload is a structured failure, not an exception.
    /// </summary>
    private static (string? JobId, string? Error) ParseJobId(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) payloadJson = "{}";
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("jobId", out var jobEl) || jobEl.ValueKind != JsonValueKind.String)
                return (null, "Payload missing 'jobId' string field.");
            var jobId = jobEl.GetString();
            if (string.IsNullOrWhiteSpace(jobId))
                return (null, "Payload 'jobId' is empty.");
            return (jobId, null);
        }
        catch (Exception ex)
        {
            return (null, $"Payload is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase F (brief §D) — parse the <c>authorizedBy</c> field from a
    /// test_print payload. The SaaS sets this from the shopkeeper's user id
    /// when the operator confirms the test print on the web dashboard. A
    /// test_print without it is REJECTED — the companion never assumes
    /// authorization. Also rejects if the payload contains unknown fields
    /// (the test_print schema is <c>{authorizedBy: string}</c> only).
    /// </summary>
    private static (string? AuthorizedBy, string? Error) ParseAuthorizedBy(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) payloadJson = "{}";
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            // Reject unknown fields — the test_print schema is exactly {authorizedBy}.
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name != "authorizedBy")
                    return (null, $"Payload has unknown field '{prop.Name}'. The test_print schema accepts only {{\"authorizedBy\":\"<operator name>\"}}.");
            }
            if (!doc.RootElement.TryGetProperty("authorizedBy", out var authEl) || authEl.ValueKind != JsonValueKind.String)
                return (null, "Payload missing 'authorizedBy' string field. The web dashboard must confirm the test print before issuing the command; this field records the operator who confirmed.");
            var authorizedBy = authEl.GetString();
            if (string.IsNullOrWhiteSpace(authorizedBy))
                return (null, "Payload 'authorizedBy' is empty. The web dashboard must name the operator who confirmed the test print.");
            return (authorizedBy, null);
        }
        catch (Exception ex)
        {
            return (null, $"Payload is not valid JSON: {ex.Message}");
        }
    }

    private static CommandExecutionResult Succeeded(string commandId, string resultJson) =>
        new(commandId, CommandStatus.Succeeded, resultJson, null);

    private static CommandExecutionResult Failed(string commandId, string error) =>
        new(commandId, CommandStatus.Failed, null, error);
}

/// <summary>
/// Structured result of executing one command. The poller forwards this to
/// the backend via <see cref="IPrintShopBackend.ReportCommandStatusAsync"/>:
/// <see cref="Status"/> is <c>succeeded</c> or <c>failed</c>;
/// <see cref="ResultJson"/> is the structured payload on success;
/// <see cref="Error"/> is the failure reason on failure.
/// </summary>
public sealed record CommandExecutionResult(
    string CommandId,
    string Status,
    string? ResultJson,
    string? Error);
