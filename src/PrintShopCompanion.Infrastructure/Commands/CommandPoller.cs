namespace PrintShopCompanion.Infrastructure.Commands;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// v1 contract §6 + §7 + Phase F (brief §A) — the command poller. Periodically
/// calls <c>GET /api/v1/companion/commands</c>, fetches pending commands,
/// validates them, executes them via <see cref="CommandExecutor"/>, and
/// reports results via <c>POST /api/v1/companion/commands</c>.
///
/// <para><b>Phase F crash-safe state machine</b> (brief Phase A acceptance
/// criterion #1: "Interrupted non-idempotent commands cannot be silently
/// re-executed"). The poller drives the durable store through these states:</para>
/// <list type="number">
///   <item><see cref="DurableCommandStore.RecordReceivedAsync"/> — first time
///   we see a command from the server (status='received'). Idempotent.</item>
///   <item><see cref="DurableCommandStore.TryClaimAsync"/> — atomic
///   received→claimed. First caller wins; a concurrent caller sees 'claimed'
///   (or a terminal state) and does NOT execute.</item>
///   <item>Report <c>delivered</c> + <c>executing</c> to the SaaS (best-effort;
///   a network failure here does not block execution).</item>
///   <item><see cref="DurableCommandStore.MarkExecutingAsync"/> — persist
///   claimed→executing BEFORE running the handler, so a crash between claim
///   and handler-start leaves 'claimed' (safe: recovered to 'received' on
///   restart) not 'executing' (unsafe: would become 'outcome_unknown').</item>
///   <item>Run the handler via <see cref="CommandExecutor.ExecuteAsync"/>.</item>
///   <item><see cref="DurableCommandStore.MarkTerminalAsync"/> — persist the
///   terminal result (succeeded/failed/outcome_unknown) AFTER the handler
///   returns. Idempotent on re-report.</item>
///   <item>Report the terminal status to the SaaS. A network failure here is
///   NOT fatal — the durable row is terminal; the next redelivery re-reports
///   it (the SaaS is idempotent on commandId+status).</item>
/// </list>
///
/// <para><b>Restart recovery</b>: on startup, call
/// <see cref="RecoverInterruptedAsync"/>. It transitions any row left
/// 'executing' (handler started, side effect uncertain) to 'outcome_unknown'
/// and returns those rows so the poller re-reports them to the SaaS. 'claimed'
/// rows (handler never started) are reset to 'received' so the poller
/// re-claims + re-executes them (safe — no side effect ran). Terminal rows are
/// never touched.</para>
///
/// <para><b>Dedup layers</b> (the brief's §7):</para>
/// <list type="number">
///   <item><b>Fast-path cache</b> (<see cref="CommandDedupStore"/>): in-process
///   LRU. Avoids the SQLite round-trip on hot duplicates within one process.
///   NOT durable — cleared on restart.</item>
///   <item><b>Durable store</b> (<see cref="DurableCommandStore"/>): the
///   authoritative layer. Survives restart. The fast-path cache is a perf
///   optimization on top, never a substitute.</item>
/// </list>
///
/// <para><b>Poll cadence</b>: <see cref="BackendOptions.PollFallbackInterval"/>
/// (default 2s, within the contract's 2-5s range). The
/// <see cref="SocketIoRealtimeClient.CommandIssued"/> event triggers an
/// immediate poll so commands issued between ticks are not delayed.</para>
/// </summary>
public sealed class CommandPoller
{
    private readonly IPrintShopBackend _backend;
    private readonly CommandExecutor _executor;
    private readonly CommandDedupStore _cache;          // fast-path LRU (in-memory)
    private readonly DurableCommandStore _durable;    // durable state machine (SQLite)
    private readonly IOptionsMonitor<BackendOptions> _opts;
    private readonly ILogger<CommandPoller> _logger;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public CommandPoller(
        IPrintShopBackend backend,
        CommandExecutor executor,
        CommandDedupStore cache,
        DurableCommandStore durable,
        IOptionsMonitor<BackendOptions> opts,
        ILogger<CommandPoller> logger)
    {
        _backend = backend;
        _executor = executor;
        _cache = cache;
        _durable = durable;
        _opts = opts;
        _logger = logger;
    }

    /// <summary>
    /// Phase F (brief §A) — call ONCE on application startup, BEFORE the first
    /// tick. Recovers any commands interrupted by the previous process crash:
    /// 'executing' rows → 'outcome_unknown' (re-reported to SaaS; never
    /// re-executed); 'claimed' rows → 'received' (re-claimed + re-executed by
    /// the next tick, safe because the handler never started). Returns the
    /// list of commands transitioned to 'outcome_unknown' so the caller can
    /// surface them in the UI.
    /// </summary>
    public async Task<IReadOnlyList<CommandRow>> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        try
        {
            var outcomeUnknown = await _durable.RecoverInterruptedAsync(ct);
            // Re-report each outcome_unknown to the SaaS (best-effort; the SaaS
            // is idempotent on commandId+status so a later redelivery will retry).
            foreach (var row in outcomeUnknown)
            {
                await TryReportAsync(row.CommandId, DurableCommandStore.CommandState.OutcomeUnknown,
                    WrapOutcomeUnknown(row));
            }
            return outcomeUnknown;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restart recovery failed — interrupted commands may re-execute unsafely. STOP the poller + investigate.");
            throw;
        }
    }

    /// <summary>
    /// Run one poll-execute-report cycle. Returns the number of commands
    /// processed (executed or skipped-as-duplicate). Designed to be called
    /// by a timer at <see cref="BackendOptions.PollFallbackInterval"/> cadence
    /// AND by the <see cref="SocketIoRealtimeClient.CommandIssued"/> event
    /// for immediate dispatch. Serialized by <see cref="_tickGate"/> so the
    /// two callers don't overlap.
    /// </summary>
    public async Task<int> TickOnceAsync(CancellationToken ct = default)
    {
        if (!await _tickGate.WaitAsync(0, ct))
        {
            _logger.LogDebug("TickOnceAsync skipped — a previous tick is still running");
            return 0;
        }
        try
        {
            IReadOnlyList<PendingCommand> commands;
            try
            {
                commands = await _backend.FetchPendingCommandsAsync(ct);
            }
            catch (DeviceDisabledException ex)
            {
                _logger.LogWarning("Device disabled — command polling paused. {Message}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FetchPendingCommands failed (transient — will retry next tick)");
                return 0;
            }

            if (commands.Count == 0) return 0;
            _logger.LogInformation("Fetched {Count} pending command(s)", commands.Count);

            int processed = 0;
            foreach (var cmd in commands)
            {
                ct.ThrowIfCancellationRequested();
                processed += await ProcessOneAsync(cmd, ct);
            }
            return processed;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>
    /// Process a single command through the crash-safe state machine.
    /// </summary>
    private async Task<int> ProcessOneAsync(PendingCommand command, CancellationToken ct)
    {
        // 1. Fast-path cache: if we've already processed this id in this
        // process lifetime, re-report the terminal status without touching
        // SQLite or re-executing. (LRU is a perf optimization, not a guarantee.)
        if (_cache.TryGet(command.Id, out var cached) && cached is not null)
        {
            _logger.LogInformation("Command {CommandId} already processed ({Status}) — re-reporting (fast-path)",
                command.Id, cached.Status);
            await TryReportAsync(command.Id, cached.Status, cached.ResultJson);
            return 1;
        }

        // 2. Record receipt (idempotent) + atomic claim (received→claimed).
        // The claim is the first-wins gate against concurrent pollers +
        // duplicate redeliveries.
        try
        {
            await _durable.RecordReceivedAsync(command, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecordReceived for {CommandId} failed — falling back to in-memory dedup only", command.Id);
        }

        ClaimResult claim;
        try
        {
            claim = await _durable.TryClaimAsync(command, ct);
        }
        catch (Exception ex)
        {
            // Durable store unavailable (DB locked, init failed). Fall back to
            // the in-memory cache as the only dedup layer — less safe but the
            // command still executes once this process. Logged at Warning so we
            // notice the degradation.
            _logger.LogWarning(ex, "Durable claim for {CommandId} failed — falling back to in-memory dedup only", command.Id);
            claim = new ClaimResult(Claimed: true, Status: null, ResultJson: null, Error: null);
        }

        if (!claim.Claimed)
        {
            // We lost the claim — another caller owns this command, OR it's a
            // redelivery of a command we (or a previous process) already handled.
            if (claim.Status is null)
            {
                // Should not happen — TryClaimAsync always returns a non-null
                // Status when Claimed is false. Treat as already-handled.
                _logger.LogWarning("Claim lost for {CommandId} but no status returned — skipping", command.Id);
                return 1;
            }

            // Terminal states (succeeded/failed/outcome_unknown/expired/cancelled):
            // re-report the stored result WITHOUT re-executing. This is the
            // brief's "Terminal states: never execute again" + the SaaS's
            // idempotency on commandId+status.
            if (DurableCommandStore.TerminalStates.Contains(claim.Status))
            {
                _logger.LogInformation("Command {CommandId} already terminal ({Status}) — re-reporting", command.Id, claim.Status);
                var resultToSend = claim.Status switch
                {
                    DurableCommandStore.CommandState.Succeeded => claim.ResultJson,
                    DurableCommandStore.CommandState.Failed => WrapError(claim.Error),
                    DurableCommandStore.CommandState.OutcomeUnknown => WrapOutcomeUnknownFromError(claim.Error),
                    _ => WrapError(claim.Error ?? $"Command is {claim.Status}"),
                };
                await TryReportAsync(command.Id, claim.Status, resultToSend);
                _cache.Record(command.Id, claim.Status, claim.ResultJson, claim.Error);
                return 1;
            }

            // Non-terminal (received/claimed/executing): another caller is
            // mid-flight in THIS process, OR a previous process left it
            // non-terminal. RecoverInterruptedAsync (called on startup) has
            // already transitioned 'executing' → 'outcome_unknown' + 'claimed'
            // → 'received', so a non-terminal row at this point means another
            // caller in THIS process is handling it. Skip — they'll report.
            _logger.LogInformation("Command {CommandId} is non-terminal ({Status}) in another caller — skipping", command.Id, claim.Status);
            return 1;
        }

        // 3. We won the claim. Check expiry BEFORE executing (the brief's
        // "Expired unstarted commands: mark expired" + the contract's local
        // expiry validation).
        if (command.IsExpired)
        {
            _logger.LogWarning("Command {CommandId} won claim but is expired — marking expired (not executing)", command.Id);
            try
            {
                await _durable.MarkExpiredAsync(command.Id, ct);
                _cache.Record(command.Id, DurableCommandStore.CommandState.Expired, null, "Command expired before execution started.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MarkExpired for {CommandId} failed (non-fatal — will re-report on redelivery)", command.Id);
            }
            await TryReportAsync(command.Id, DurableCommandStore.CommandState.Expired, WrapError("Command expired before execution started."));
            return 1;
        }

        // 4. Acknowledge receipt (delivered) so the backend knows we got it.
        // Best-effort — a network failure here does NOT block execution.
        await TryReportAsync(command.Id, CommandStatus.Delivered, null);

        // 5. Report executing to the SaaS, THEN persist executing locally.
        // The local MarkExecuting is the critical safety point: it transitions
        // claimed→executing. A crash AFTER this point leaves 'executing', which
        // RecoverInterruptedAsync transitions to 'outcome_unknown' on restart
        // (NEVER re-executed). A crash BEFORE this point leaves 'claimed',
        // which is recovered to 'received' (safe to re-execute — the handler
        // never started).
        await TryReportAsync(command.Id, CommandStatus.Executing, null);
        bool executingMarked;
        try
        {
            executingMarked = await _durable.MarkExecutingAsync(command.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarkExecuting for {CommandId} failed — aborting execution (safer to skip than to execute without the durable marker)", command.Id);
            await TryReportAsync(command.Id, DurableCommandStore.CommandState.Failed, WrapError("Local state machine could not mark executing; execution aborted for safety."));
            return 1;
        }
        if (!executingMarked)
        {
            // The row is no longer 'claimed' — another process raced us. Don't execute.
            _logger.LogInformation("Command {CommandId} lost the executing race — skipping", command.Id);
            return 1;
        }

        // 6. Run the handler.
        var result = await _executor.ExecuteAsync(command, ct);

        // 7. Map the executor's terminal result to a durable terminal state.
        // The executor returns CommandStatus.Succeeded/Failed; we never
        // auto-convert to outcome_unknown here (only RecoverInterruptedAsync
        // does that, on a crash). The executor may itself return
        // outcome_unknown for a handler that detected an uncertain spooler
        // outcome — that's the executor's call, not ours.
        var terminalStatus = result.Status switch
        {
            CommandStatus.Succeeded => DurableCommandStore.CommandState.Succeeded,
            CommandStatus.Failed => DurableCommandStore.CommandState.Failed,
            // The executor never returns other statuses for a terminal result,
            // but if it did, treat as failed (fail closed).
            _ => DurableCommandStore.CommandState.Failed,
        };
        var resultJson = terminalStatus == DurableCommandStore.CommandState.Succeeded
            ? result.ResultJson : null;
        var error = terminalStatus == DurableCommandStore.CommandState.Failed
            ? result.Error : null;

        // 8. Persist the terminal result durably + in the cache + report to backend.
        try
        {
            await _durable.MarkTerminalAsync(command.Id, terminalStatus, resultJson, error, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Durable terminal status for {CommandId} failed — stored in cache only (re-report will retry)", command.Id);
        }
        _cache.Record(command.Id, terminalStatus, result.ResultJson, result.Error);

        await TryReportAsync(command.Id, terminalStatus,
            terminalStatus == DurableCommandStore.CommandState.Succeeded ? result.ResultJson : WrapError(result.Error));
        _logger.LogInformation("Command {CommandId} executed → {Status}", command.Id, terminalStatus);
        return 1;
    }

    /// <summary>Report a status to the backend, swallowing transient errors (will retry next tick).</summary>
    private async Task TryReportAsync(string commandId, string status, string? resultJson)
    {
        try
        {
            await _backend.ReportCommandStatusAsync(commandId, status, resultJson);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Report {Status} for {CommandId} failed (will retry next tick)", status, commandId);
        }
    }

    /// <summary>Wrap a failure reason as a JSON object so the backend's `result` field is always valid JSON.</summary>
    private static string WrapError(string? error) =>
        System.Text.Json.JsonSerializer.Serialize(new { error });

    /// <summary>Wrap an outcome_unknown row's metadata for the SaaS report (no secrets, no PII).</summary>
    private static string WrapOutcomeUnknown(CommandRow row) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            reason = "Execution interrupted by application restart; physical outcome uncertain.",
            commandType = row.CommandType,
            executingAt = row.ExecutingAt?.ToString("O"),
        });

    /// <summary>Wrap an outcome_unknown error string for re-report.</summary>
    private static string WrapOutcomeUnknownFromError(string? error) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            reason = error ?? "Execution interrupted by application restart; physical outcome uncertain.",
        });
}
