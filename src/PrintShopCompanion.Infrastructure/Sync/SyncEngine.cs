namespace PrintShopCompanion.Infrastructure.Sync;

using System.Net.Http;
using System.Text.Json;
using PrintShopCompanion.Infrastructure.Backend;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;

/// <summary>
/// The sync engine. Runs in the background, pushes pending operations to the
/// backend when connectivity is available, and pulls updates on a periodic schedule.
/// </summary>
public sealed class SyncEngine
{
    private readonly IShopBackend _backend;
    private readonly IPendingOpsQueue _pendingOps;
    private readonly ILogger<SyncEngine> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isOnline = true;
    private DateTimeOffset _lastSyncTime = DateTimeOffset.MinValue;

    public event Action<bool>? OnlineStatusChanged;
    public event Action? SyncCompleted;

    public bool IsOnline => _isOnline;
    public DateTimeOffset LastSyncTime => _lastSyncTime;
    public int PendingOpCount => _pendingOps.GetPendingCount();

    public SyncEngine(IShopBackend backend, IPendingOpsQueue pendingOps, ILogger<SyncEngine> logger)
    {
        _backend = backend;
        _pendingOps = pendingOps;
        _logger = logger;
    }

    public void Start(CancellationToken ct = default)
    {
        // Create a linked CTS so the caller can cancel the loop externally
        // (e.g. via App.OnExit's shared token). We keep our own CTS so
        // StopAsync can also cancel from here.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        // Cancel and WAIT for the loop task to actually exit before returning.
        // The previous implementation just cancelled + disposed the CTS without
        // waiting, which meant a background SQLite write could be cut off
        // mid-flight when the App proceeded to dispose the host.
        try { _cts?.Cancel(); } catch { }

        if (_loopTask is not null)
        {
            try
            {
                // Bounded wait — if the loop is stuck in an uncancelable
                // SQLite write, give up after 3 seconds.
                await Task.WhenAny(_loopTask, Task.Delay(TimeSpan.FromSeconds(3)));
            }
            catch { /* swallow — we're shutting down anyway */ }
        }

        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await SyncNowAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await SyncNowAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        bool wasOnline = _isOnline;
        try
        {
            if (_backend.IsAuthenticated)
                _ = await _backend.GetCurrentUserAsync(ct);
            _isOnline = true;
        }
        catch
        {
            _isOnline = false;
        }

        if (wasOnline != _isOnline)
        {
            OnlineStatusChanged?.Invoke(_isOnline);
            _logger.LogInformation("Connectivity changed: {Online}", _isOnline ? "ONLINE" : "OFFLINE");
        }

        if (!_isOnline || !_backend.IsAuthenticated)
        {
            _logger.LogDebug("Offline or not authenticated — skipping sync");
            return;
        }

        try { await PushPendingOpsAsync(ct); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to push pending ops"); }
        try { await PullUpdatesAsync(ct); _lastSyncTime = DateTimeOffset.UtcNow; SyncCompleted?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to pull updates"); }
    }

    private async Task PushPendingOpsAsync(CancellationToken ct)
    {
        var ops = _pendingOps.GetPendingOps(limit: 10);
        if (ops.Count == 0) return;

        _logger.LogInformation("Pushing {Count} pending operations", ops.Count);

        foreach (var op in ops)
        {
            if (op.Attempts >= 5)
            {
                _pendingOps.MarkFailed(op.OpId, "Max attempts reached");
                _logger.LogWarning("Pending op {OpId} exceeded max attempts", op.OpId);
                continue;
            }
            if (op.NextAttemptAt is not null && op.NextAttemptAt > DateTimeOffset.UtcNow)
                continue;

            _pendingOps.MarkInFlight(op.OpId);
            try
            {
                switch (op.OperationType)
                {
                    case "start_order": await _backend.StartOrderAsync(op.TargetId, ct); break;
                    case "complete_order": await _backend.CompleteOrderAsync(op.TargetId, ct); break;
                    case "cancel_order": await _backend.CancelOrderAsync(op.TargetId, ct); break;
                    case "update_notes":
                        var notes = op.GetPayloadField("notes");
                        await _backend.UpdateOrderNotesAsync(op.TargetId, notes, ct);
                        break;
                }
                _pendingOps.MarkCompleted(op.OpId);
                _logger.LogInformation("Pending op {OpId} ({Type}) completed", op.OpId, op.OperationType);
            }
            catch (PrintShopApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _pendingOps.MarkFailed(op.OpId, $"Conflict: {ex.Message}");
                _logger.LogWarning("Pending op {OpId} conflicted: {Msg}", op.OpId, ex.Message);
            }
            catch (Exception ex)
            {
                _pendingOps.MarkRetry(op.OpId, ex.Message);
                _logger.LogWarning(ex, "Pending op {OpId} failed — will retry", op.OpId);
            }
        }
    }

    private async Task PullUpdatesAsync(CancellationToken ct)
    {
        if (!_backend.IsAuthenticated) return;
        try { await _backend.GetShopAsync(ct); } catch { }
        try { var (orders, total) = await _backend.ListOrdersAsync(limit: 50, ct: ct); } catch { }
        try { await _backend.GetAnalyticsAsync(ct); } catch { }
        try { await _backend.ListDevicesAsync(ct); } catch { }
    }
}

/// <summary>Persistent queue of operations that need to be pushed to the backend.</summary>
public interface IPendingOpsQueue
{
    int GetPendingCount();
    IReadOnlyList<PendingOp> GetPendingOps(int limit = 10);
    void Enqueue(string operationType, string targetId, object? payload = null);
    void MarkInFlight(string opId);
    void MarkCompleted(string opId);
    void MarkFailed(string opId, string error);
    void MarkRetry(string opId, string error);
}

public sealed class PendingOp
{
    public required string OpId { get; init; }
    public required string OperationType { get; init; }
    public required string TargetId { get; init; }
    public required string PayloadJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? LastError { get; init; }
    public string Status { get; init; }

    public string? GetPayloadField(string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(PayloadJson);
            return doc.RootElement.TryGetProperty(fieldName, out var val) ? val.ToString() : null;
        }
        catch { return null; }
    }
}
