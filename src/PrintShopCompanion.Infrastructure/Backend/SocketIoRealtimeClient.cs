namespace PrintShopCompanion.Infrastructure.Backend;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Shared.Dto;
using SocketIOClient;

/// <summary>
/// Socket.IO realtime client. Connects to the backend's realtime service
/// (via the Caddy gateway) and receives <c>job.dispatch</c> events when new
/// print jobs are available for this agent.
///
/// <para><b>Connection URL (v0.4.7+):</b> the URL MUST include the
/// <c>?XTransformPort=3003</c> query parameter — without it, Caddy routes
/// to the Next.js app (port 3000) which doesn't serve Socket.IO. The URL is
/// configured via <c>Backend:SocketIoUrl</c> in appsettings.json.</para>
///
/// <para><b>Auth handshake (v0.4.7+):</b> after connecting, PrintEXE emits an
/// <c>auth</c> event with the JWT (exchanged from the device token via
/// <c>POST /api/v1/agents/token</c>). The server responds with
/// <c>auth-ok</c> (joined to room <c>shop:&lt;shopId&gt;</c>) or
/// <c>auth-error</c> (invalid/expired JWT — must refresh and reconnect).</para>
///
/// <para><b>Events to listen for (per integration guide section 4.4):</b>
/// <c>job.dispatch</c>, <c>order.paid</c>, <c>order.ready</c>,
/// <c>job.printing</c>, <c>job.completed</c>, <c>agent.revoked</c>,
/// <c>sync.pushed</c>. PrintEXE should NOT emit any custom events (other
/// than the initial <c>auth</c>) — all communication is via REST.</para>
/// </summary>
public sealed class SocketIoRealtimeClient : IDisposable
{
    private readonly SocketIO _socket;
    private readonly IPrintShopBackend _backend;
    private readonly IJobQueue _queue;
    private readonly IOptionsMonitor<BackendOptions> _opts;
    private readonly ILogger<SocketIoRealtimeClient> _logger;
    private readonly HttpFallbackPoller _poller;
    private CancellationTokenSource? _cts;
    private string? _currentJwt;

    public event Action<PrintJobRecord>? JobDelivered;
    public event Action<bool>? ConnectionChanged;
    public event Action? Revoked;

    /// <summary>
    /// v1 contract §12 — raised when the server emits <c>device.command.issued</c>
    /// <c>{commandId, agentId, commandType}</c>. The companion responds by
    /// polling <c>GET /companion/commands</c> immediately (rather than waiting
    /// for the next poll tick) to fetch the command + execute it.
    /// </summary>
    public event Action<string?>? CommandIssued;

    public bool IsConnected => _socket.Connected;

    public SocketIoRealtimeClient(
        IPrintShopBackend backend,
        IJobQueue queue,
        IOptionsMonitor<BackendOptions> opts,
        ILogger<SocketIoRealtimeClient> logger,
        HttpFallbackPoller poller)
    {
        _backend = backend;
        _queue = queue;
        _opts = opts;
        _logger = logger;
        _poller = poller;
        _socket = new SocketIO(BuildUri(), new SocketIOOptions
        {
            // Path = "/" because the Caddy gateway routes based on the
            // ?XTransformPort=3003 query (which is in BuildUri()).
            Path = "/",
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            ReconnectionDelay = 2000,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
        });

        _socket.OnConnected += OnConnected;
        _socket.OnDisconnected += OnDisconnected;
        _socket.OnError += OnError;

        // Auth handshake responses
        _socket.On("auth-ok", response =>
        {
            _logger.LogInformation("Socket.IO auth-ok — joined shop room");
            ConnectionChanged?.Invoke(true);
        });
        _socket.On("auth-error", async response =>
        {
            _logger.LogWarning("Socket.IO auth-error — JWT may be expired, refreshing");
            // The JWT is short-lived (15 min). On auth-error we re-exchange
            // the device token for a fresh JWT and reconnect.
            try
            {
                _currentJwt = await _backend.ExchangeDeviceTokenForJwtAsync();
                _logger.LogInformation("Refreshed JWT — reconnecting");
                await _socket.DisconnectAsync();
                await _socket.ConnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JWT refresh failed — falling back to polling");
            }
        });

        // job.dispatch — the main event: a new job is available for this agent.
        _socket.On("job.dispatch", async ev =>
        {
            try
            {
                var dto = ev.GetValue<PrintJobDto>();
                var record = MapDtoToRecord(dto, _backend.CurrentShop?.Id);
                // Decision D-12 — shop-ownership defense in depth. The backend
                // scopes dispatches to our shop; if a payload ever names a
                // different shop, refuse to enqueue it.
                if (!JobOwnershipValidator.IsAcceptable(record, _backend.CurrentShop?.Id))
                {
                    _logger.LogError(
                        "Rejected job {JobId}: shop mismatch (job shop '{JobShop}' != device shop '{DeviceShop}')",
                        record.JobId, record.ShopId, _backend.CurrentShop?.Id);
                    return;
                }
                record = await _queue.EnqueueAsync(record);
                JobDelivered?.Invoke(record);
                _logger.LogInformation("Job {JobId} delivered via Socket.IO and persisted", dto.JobId);
                // Per the integration guide: PrintEXE does NOT emit any custom
                // events back. The job is claimed later via
                // POST /agents/jobs/{id}/status {status:"printing"} when the
                // JobProcessor dequeues it.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnJobDispatched failed");
            }
        });

        // agent.revoked — if the revoked agentId is ours, the device token is
        // no longer valid. Show "Device revoked" + stop.
        _socket.On("agent.revoked", ev =>
        {
            try
            {
                var payload = ev.GetValue<AgentRevokedPayload>();
                if (IsUs(payload?.AgentId))
                {
                    _logger.LogWarning("This device was revoked by the shopkeeper");
                    Revoked?.Invoke();
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "agent.revoked handler failed"); }
        });

        // v1 contract §12 — device.command.issued: a command was created for this
        // device. Trigger an immediate command poll so the executor fetches +
        // executes it without waiting for the next poll tick. The payload has
        // {commandId, agentId, commandType}; we pass commandType (or null) as a
        // hint — the executor still fetches via GET /companion/commands to get
        // the authoritative payload + expiry.
        _socket.On("device.command.issued", ev =>
        {
            try
            {
                var payload = ev.GetValue<DeviceCommandIssuedPayload>();
                if (IsUs(payload?.AgentId))
                {
                    _logger.LogInformation("Socket.IO device.command.issued — type={CommandType} id={CommandId}",
                        payload?.CommandType, payload?.CommandId);
                    CommandIssued?.Invoke(payload?.CommandType);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "device.command.issued handler failed"); }
        });

        // v1 contract §12 — device.revoked: same effect as agent.revoked but the
        // newer event name. Clear credential + stop. Some deployments may emit
        // either; handle both.
        _socket.On("device.revoked", ev =>
        {
            try
            {
                var payload = ev.GetValue<DeviceRevokedPayload>();
                if (IsUs(payload?.AgentId))
                {
                    _logger.LogWarning("This device was revoked (device.revoked event)");
                    Revoked?.Invoke();
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "device.revoked handler failed"); }
        });

        // sync.pushed — new data available, trigger a sync pull
        _socket.On("sync.pushed", ev =>
        {
            try
            {
                _logger.LogInformation("Socket.IO sync.pushed — new data available on backend");
                // The SyncEngine's 15s poll will pick this up; we don't trigger
                // an immediate sync to avoid hammering the backend.
            }
            catch { }
        });
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Pre-exchange the device token for a JWT so we can emit `auth`
            // immediately on connect.
            _currentJwt = await _backend.ExchangeDeviceTokenForJwtAsync();
            await _socket.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial Socket.IO connect failed; falling back to polling");
            _poller.Start(_cts.Token);
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await _socket.DisconnectAsync();
        _poller.Stop();
    }

    private Uri BuildUri()
    {
        var o = _opts.CurrentValue;
        // Prefer the explicit SocketIoUrl (with the XTransformPort=3003 query).
        // Fall back to BaseUrl + HubPath for backward compatibility (older
        // appsettings.json files might not have SocketIoUrl set).
        if (!string.IsNullOrWhiteSpace(o.SocketIoUrl))
            return new Uri(o.SocketIoUrl);
        if (!string.IsNullOrWhiteSpace(o.BaseUrl))
        {
            var baseUri = new Uri(o.BaseUrl);
            return new Uri(baseUri, o.HubPath.TrimStart('/'));
        }
        throw new InvalidOperationException("Socket.IO URL not configured — set Backend:SocketIoUrl in appsettings.json");
    }

    private async void OnConnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Socket.IO connected");
        // Per the integration guide section 4.3: after connecting, PrintEXE
        // must emit an `auth` event with the JWT. The server responds with
        // `auth-ok` (joined to room `shop:<shopId>`) or `auth-error`.
        try
        {
            if (string.IsNullOrEmpty(_currentJwt))
                _currentJwt = await _backend.ExchangeDeviceTokenForJwtAsync();
            await _socket.EmitAsync("auth", new { token = _currentJwt });
            _logger.LogInformation("Emitted auth event with JWT");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit auth event after Socket.IO connect");
        }
    }

    private void OnDisconnected(object? sender, string reason)
    {
        _logger.LogWarning("Socket.IO disconnected: {Reason}; HTTP poll fallback active", reason);
        ConnectionChanged?.Invoke(false);
        _poller.Start(_cts?.Token ?? CancellationToken.None);
    }

    private void OnError(object? sender, string err) => _logger.LogError("Socket.IO error: {Err}", err);

    /// <summary>
    /// v0.7.2 sync: revocation/command events carry the SERVER-assigned agentId
    /// (PrintAgent uuid from pair/heartbeat/pending responses). Match on that
    /// (the local-GUID comparison stays for backward compatibility) —
    /// previously the local GUID never equaled the server id, so
    /// agent.revoked / device.revoked were silently ignored and a revoked
    /// device kept printing.
    /// </summary>
    private bool IsUs(string? payloadAgentId) =>
        string.IsNullOrEmpty(payloadAgentId) ||
        payloadAgentId == _backend.ServerAgentId ||
        payloadAgentId == ThisApp.DeviceId.ToString("N");

    private PrintJobRecord MapDtoToRecord(PrintJobDto dto, string? knownShopId) => new()
    {
        JobId = dto.JobId,
        // Decision D-12: stamp the device's shop id so every record carries its
        // owner (the Phase 0 audit found ShopId was always empty here).
        ShopId = knownShopId ?? "",
        DeviceId = _backend.CurrentDevice?.DeviceId ?? "",
        Title = dto.Filename,
        PrinterName = null,
        Copies = dto.Copies,
        PageRange = dto.PageRange,
        PaperSize = dto.PaperSize,
        Color = dto.ColorMode == "color",
        Duplex = dto.Duplex,
        PdfUrl = Uri.TryCreate(dto.DownloadUrl, UriKind.Absolute, out var u) ? u : null,
        PdfSha256 = null,
        Status = JobStatus.Pending,
        AttemptCount = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public void Dispose()
    {
        _socket.Dispose();
        _poller.Dispose();
    }
}

/// <summary>
/// Wire payload for the `agent.revoked` Socket.IO event.
/// </summary>
internal sealed class AgentRevokedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("agentId")]
    public string? AgentId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("agentName")]
    public string? AgentName { get; set; }
}

/// <summary>
/// v1 contract §12 — payload for the <c>device.command.issued</c> Socket.IO event.
/// <c>{commandId, agentId, commandType}</c>. The companion polls
/// <c>GET /companion/commands</c> on receipt to fetch the authoritative payload
/// + expiry (defense in depth: the event is a notification, not a record).
/// </summary>
internal sealed class DeviceCommandIssuedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("commandId")]
    public string? CommandId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("commandType")]
    public string? CommandType { get; set; }
}

/// <summary>
/// v1 contract §12 — payload for the <c>device.revoked</c> Socket.IO event.
/// <c>{agentId}</c>. Treated the same as <c>agent.revoked</c> (clear credential + stop).
/// </summary>
internal sealed class DeviceRevokedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("agentId")]
    public string? AgentId { get; set; }
}

/// <summary>
/// HTTP long-poll fallback. Per the integration guide section 5.1, if
/// Socket.IO is disconnected, PrintEXE polls GET /agents/jobs/pending every
/// 2 seconds and claims the first job it can.
/// </summary>
public sealed class HttpFallbackPoller : IDisposable
{
    private readonly IPrintShopBackend _backend;
    private readonly IJobQueue _queue;
    private readonly ILogger<HttpFallbackPoller> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<PrintJobRecord>? JobDelivered;
    public bool IsRunning => _loop is { IsCompleted: false };

    public HttpFallbackPoller(IPrintShopBackend backend, IJobQueue queue, ILogger<HttpFallbackPoller> logger)
    {
        _backend = backend;
        _queue = queue;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        if (IsRunning) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("HTTP poll fallback started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_backend.IsAuthenticated)
                {
                    var jobs = await _backend.FetchPendingJobsAsync(ct);
                    foreach (var record in jobs)
                    {
                        // Decision D-12 — same shop-ownership defense as the
                        // socket path (poll fallback must not bypass it).
                        if (!JobOwnershipValidator.IsAcceptable(record, _backend.CurrentShop?.Id))
                        {
                            _logger.LogError(
                                "Rejected polled job {JobId}: shop mismatch (job shop '{JobShop}' != device shop '{DeviceShop}')",
                                record.JobId, record.ShopId, _backend.CurrentShop?.Id);
                            continue;
                        }
                        var queued = await _queue.EnqueueAsync(record);
                        JobDelivered?.Invoke(queued);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HTTP poll iteration failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        _logger.LogInformation("HTTP poll fallback stopped");
    }

    public void Dispose() => Stop();
}
