namespace PrintShopCompanion.Infrastructure.Commands;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// v1 contract §6 + §7 — the command poller. Periodically calls
/// <c>GET /api/v1/companion/commands</c>, fetches pending commands, validates
/// them, executes them via <see cref="CommandExecutor"/>, and reports results
/// via <c>POST /api/v1/companion/commands</c>.
///
/// <para><b>Dedup / idempotency (Phase 3, brief §7):</b> the backend is
/// idempotent on (commandId, status), but the companion must NOT re-execute a
/// command it has already processed. Two layers of protection:</para>
/// <list type="number">
///   <item><b>Fast-path cache</b> (<see cref="CommandDedupStore"/>): in-process
///   LRU of recently-processed ids. Avoids the SQLite round-trip on hot
///   duplicates within one process lifetime. NOT durable — cleared on restart.</item>
///   <item><b>Durable claim</b> (<see cref="DurableCommandStore"/>): SQLite
///   <c>companion_commands</c> table (shares the queue DB) with an atomic
///   INSERT-or-return-existing. The first caller wins; subsequent callers get
///   the stored terminal status and re-report it without re-executing.
///   Survives restart. This is the brief's §7 "durable command execution
///   tracking" requirement.</item>
/// </list>
/// <para>On restart, a command left in <c>claimed</c>/<c>executing</c> (a
/// crash before the terminal report) is re-executed ONLY if its
/// <see cref="PendingCommand.ExpiresAt"/> is still in the future; otherwise
/// it's reported <c>failed</c> ("interrupted by restart, expired"). Terminal
/// commands are re-reported (idempotent) but NEVER re-executed.</para>
///
/// <para><b>Poll cadence:</b> <see cref="BackendOptions.PollFallbackInterval"/>
/// (default 2s, within the contract's 2-5s range). The
/// <see cref="SocketIoRealtimeClient.CommandIssued"/> event triggers an
/// immediate poll so commands issued between ticks are not delayed.</para>
/// </summary>
public sealed class CommandPoller
{
    private readonly IPrintShopBackend _backend;
    private readonly CommandExecutor _executor;
    private readonly CommandDedupStore _cache;          // fast-path LRU (in-memory)
    private readonly DurableCommandStore _durable;    // durable claim (SQLite)
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

    /// <summary>Process a single command: fast-path cache → durable claim → ack → execute → terminal report.</summary>
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

        // 2. Durable claim — the atomic first-wins gate. If we win, execute.
        // If we lose (another caller — a previous process, or a concurrent tick —
        // already claimed it), re-report the stored terminal status WITHOUT
        // re-executing, unless the stored state is non-terminal AND the command
        // is still not expired (a crash interrupted the previous attempt).
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
            // We lost the claim — another caller owns this command.
            if (claim.Status is "succeeded" or "failed")
            {
                // Terminal — re-report the stored result (idempotent), don't re-execute.
                _logger.LogInformation("Command {CommandId} already terminal ({Status}) — re-reporting", command.Id, claim.Status);
                var resultToSend = claim.Status == "succeeded" ? claim.ResultJson : WrapError(claim.Error);
                await TryReportAsync(command.Id, claim.Status, resultToSend);
                // Cache it so the next duplicate fetch hits the fast-path.
                _cache.Record(command.Id, claim.Status, claim.ResultJson, claim.Error);
                return 1;
            }
            // Non-terminal (claimed/executing) — a previous attempt crashed.
            // Re-execute ONLY if the command is still valid; otherwise report failed.
            if (command.IsExpired)
            {
                _logger.LogWarning("Command {CommandId} was interrupted by restart and has expired — reporting failed", command.Id);
                var errMsg = "Execution interrupted by application restart; command expired before retry.";
                await _durable.UpdateStatusAsync(command.Id, CommandStatus.Failed, null, errMsg, ct);
                await TryReportAsync(command.Id, CommandStatus.Failed, WrapError(errMsg));
                _cache.Record(command.Id, CommandStatus.Failed, null, errMsg);
                return 1;
            }
            _logger.LogInformation("Command {CommandId} was interrupted by restart (state={State}) — re-executing", command.Id, claim.Status);
            // Fall through to execute.
        }

        // 3. Acknowledge receipt (delivered) so the backend knows we got it.
        await TryReportAsync(command.Id, CommandStatus.Delivered, null);

        // 4. Report executing + run the executor.
        await TryReportAsync(command.Id, CommandStatus.Executing, null);
        try
        {
            await _durable.UpdateStatusAsync(command.Id, CommandStatus.Executing, null, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Durable status=executing for {CommandId} failed (non-fatal)", command.Id);
        }

        var result = await _executor.ExecuteAsync(command, ct);

        // 5. Store the terminal result durably + in the cache + report to backend.
        try
        {
            await _durable.UpdateStatusAsync(command.Id, result.Status,
                result.Status == CommandStatus.Succeeded ? result.ResultJson : null,
                result.Status == CommandStatus.Failed ? result.Error : null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Durable terminal status for {CommandId} failed — stored in cache only (re-report will retry)", command.Id);
        }
        _cache.Record(command.Id, result.Status, result.ResultJson, result.Error);

        await TryReportAsync(command.Id, result.Status,
            result.Status == CommandStatus.Succeeded ? result.ResultJson : WrapError(result.Error));
        _logger.LogInformation("Command {CommandId} executed → {Status}", command.Id, result.Status);
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
}
