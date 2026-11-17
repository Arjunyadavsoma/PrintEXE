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
/// <para><b>Dedup / idempotency (contract §7 + Phase 8 §11):</b> the backend is
/// idempotent on (commandId, status), but the companion must NOT re-execute a
/// command it has already processed. The poller keeps an in-process
/// <see cref="CommandDedupStore"/> of recently-processed command ids; a
/// duplicate fetch (e.g. the server didn't get our ack before the next poll)
/// is reported as already-succeeded without re-executing.</para>
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
    private readonly CommandDedupStore _dedup;
    private readonly IOptionsMonitor<BackendOptions> _opts;
    private readonly ILogger<CommandPoller> _logger;
    private readonly SemaphoreSlim _tickGate = new(1, 1);  // serialize ticks — don't overlap fetch/execute cycles

    public CommandPoller(
        IPrintShopBackend backend,
        CommandExecutor executor,
        CommandDedupStore dedup,
        IOptionsMonitor<BackendOptions> opts,
        ILogger<CommandPoller> logger)
    {
        _backend = backend;
        _executor = executor;
        _dedup = dedup;
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
            // Another tick is already in flight — skip this one (don't queue up).
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
                throw;   // let the host decide whether to stop the poller
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

    /// <summary>Process a single command: dedup check → delivered ack → execute → terminal report.</summary>
    private async Task<int> ProcessOneAsync(PendingCommand command, CancellationToken ct)
    {
        // 1. Dedup: if we've already processed this command id, skip execution
        // and re-report the stored terminal status (idempotent on the backend).
        if (_dedup.TryGet(command.Id, out var stored))
        {
            _logger.LogInformation("Command {CommandId} already processed ({Status}) — re-reporting", command.Id, stored!.Status);
            try
            {
                await _backend.ReportCommandStatusAsync(command.Id, stored.Status, stored.ResultJson, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Re-report of {CommandId} failed (will retry next tick)", command.Id);
            }
            return 1;
        }

        // 2. Acknowledge receipt (delivered) so the backend knows we got it.
        try
        {
            await _backend.ReportCommandStatusAsync(command.Id, CommandStatus.Delivered, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Delivered ack for {CommandId} failed (non-fatal — will still execute)", command.Id);
        }

        // 3. Report executing + run the executor.
        try
        {
            await _backend.ReportCommandStatusAsync(command.Id, CommandStatus.Executing, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Executing ack for {CommandId} failed (non-fatal — will still execute)", command.Id);
        }

        var result = await _executor.ExecuteAsync(command, ct);

        // 4. Store the terminal result in the dedup store + report it to the backend.
        _dedup.Record(command.Id, result.Status, result.ResultJson, result.Error);
        try
        {
            await _backend.ReportCommandStatusAsync(
                command.Id,
                result.Status,
                result.Status == CommandStatus.Succeeded ? result.ResultJson : JsonSerializerForError(result.Error),
                ct);
            _logger.LogInformation("Command {CommandId} reported {Status}", command.Id, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal report for {CommandId} ({Status}) failed — stored locally, will retry next tick",
                command.Id, result.Status);
        }
        return 1;
    }

    /// <summary>Wrap a failure reason as a JSON object so the backend's `result` field is always valid JSON.</summary>
    private static string JsonSerializerForError(string? error) =>
        System.Text.Json.JsonSerializer.Serialize(new { error });
}
