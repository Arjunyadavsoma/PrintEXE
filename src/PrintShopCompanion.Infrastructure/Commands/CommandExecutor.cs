namespace PrintShopCompanion.Infrastructure.Commands;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;  // ThisApp (internal, same assembly)

/// <summary>
/// v1 contract §4 + §8 — validates and executes a single remote command
/// against the companion's local application services. This class is the
/// <b>execution</b> half of the command lifecycle; the <see cref="CommandPoller"/>
/// handles fetch/ack/result-reporting.
///
/// <para><b>Security invariants (contract §8 Phase 8):</b></para>
/// <list type="bullet">
///   <item>Only the 7 allowlisted command types are accepted (see <see cref="CommandType"/>).
///   Unsupported types are rejected with a structured error — never executed.</item>
///   <item>Command payloads are parsed as JSON but NEVER shell-executed, NEVER
///   treated as a file path, NEVER treated as a URL. <c>cancel_job</c> and
///   <c>retry_job</c> extract a <c>jobId</c> (UUID string) and pass it to the
///   queue; they do not touch the filesystem or spooler directly.</item>
///   <item>Expired commands (<see cref="PendingCommand.IsExpired"/>) are rejected
///   without execution and reported as failed with reason "expired".</item>
///   <item>Printing commands (<c>test_print</c>) follow the SAME durable-queue +
///   idempotency rules as ordinary jobs — they are NOT printed directly here.</item>
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
    /// <c>succeeded</c> or <c>failed</c>.
    /// <para>The poller is responsible for the <c>delivered</c>/<c>executing</c>
    /// status reports; this method only does the final execution + produces the
    /// terminal result.</para>
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

        // 3. Dispatch to the per-type handler. Any exception → structured failure.
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

    /// <summary>refresh_printers — re-enumerate Windows printers + push via heartbeat.</summary>
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
    /// dequeues block until <c>resume</c>. The pause is NOT persisted across
    /// restart (the contract treats pause as a transient shopkeeper action).
    /// </summary>
    private async Task<CommandExecutionResult> PauseAsync(PendingCommand command, CancellationToken ct)
    {
        var wasAlreadyPaused = _queueController.IsPaused;
        await _queueController.PauseAsync(ct);
        var result = new { paused = true, wasAlreadyPaused };
        _logger.LogInformation("pause command {CommandId}: queue paused (wasAlreadyPaused={WasAlreadyPaused})", command.Id, wasAlreadyPaused);
        return Succeeded(command.Id, JsonSerializer.Serialize(result));
    }

    /// <summary>resume — Phase 3 (brief §7): resume the JobProcessor's dequeue loop after a pause.</summary>
    private async Task<CommandExecutionResult> ResumeAsync(PendingCommand command, CancellationToken ct)
    {
        var wasPaused = _queueController.IsPaused;
        await _queueController.ResumeAsync(ct);
        var result = new { resumed = true, wasPaused };
        _logger.LogInformation("resume command {CommandId}: queue resumed (wasPaused={WasPaused})", command.Id, wasPaused);
        return Succeeded(command.Id, JsonSerializer.Serialize(result));
    }

    /// <summary>cancel_job — mark a queued/printing job Cancelled in the local queue.</summary>
    private async Task<CommandExecutionResult> CancelJobAsync(PendingCommand command, CancellationToken ct)
    {
        var (jobId, parseError) = ParseJobId(command.PayloadJson);
        if (parseError is not null)
            return Failed(command.Id, parseError);
        try
        {
            await _queue.CancelAsync(jobId!, ct);
            _logger.LogInformation("cancel_job {CommandId}: cancelled job {JobId}", command.Id, jobId);
            return Succeeded(command.Id, JsonSerializer.Serialize(new { jobId, cancelled = true }));
        }
        catch (Exception ex)
        {
            return Failed(command.Id, $"cancel_job failed for {jobId}: {ex.Message}");
        }
    }

    /// <summary>retry_job — re-queue a failed job (Failed → Pending) by reading the row + updating its status via <see cref="IJobQueue.UpdateAsync"/>. The queue has no RetryAsync; re-queue = read + set status=Pending + clear next_attempt_at (the WriteStatusAsync path already sets next_attempt_at=NULL).</summary>
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
            if (job.Status != JobStatus.Failed)
                return Failed(command.Id, $"Job {jobId} is in state {job.Status} — only Failed jobs can be retried.");
            // Mutate the settable Status (init? no — Status is settable on PrintJobRecord).
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

    /// <summary>test_print — print a test page. Per contract §4, printing commands follow the SAME durable-queue + idempotency rules as ordinary jobs. We enqueue a synthetic test-print job (NOT a direct spooler call) so the JobProcessor's idempotency + safety invariants apply.</summary>
    private async Task<CommandExecutionResult> TestPrintAsync(PendingCommand command, CancellationToken ct)
    {
        var selected = _printers.GetSelectedPrinter();
        var record = new PrintJobRecord
        {
            JobId = "test-print-" + command.Id,           // stable id → idempotent on duplicate test_print commands
            ShopId = "",
            DeviceId = "",
            Title = "PrintShop Companion — Test Print",
            PrinterName = selected?.FullName,
            Copies = 1,
            Status = JobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            QueuedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _queue.EnqueueAsync(record, ct);
        _logger.LogInformation("test_print {CommandId}: enqueued test job {JobId} on printer {Printer}",
            command.Id, record.JobId, record.PrinterName);
        return Succeeded(command.Id, JsonSerializer.Serialize(new
        {
            jobId = record.JobId,
            printer = record.PrinterName,
            queued = true,
            note = "Test job queued — the JobProcessor will submit it to the Windows spooler.",
        }));
    }

    /// <summary>diagnostics — collect local diagnostic info (companion version, OS, selected printer, queue summary).</summary>
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
            queueSummary = jobs.GroupBy(j => j.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
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
