namespace PrintShopCompanion.Infrastructure.Backend;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Shared.Constants;
using PrintShopCompanion.Shared.Dto;
using SocketIOClient;

/// <summary>
/// Socket.IO realtime client. Connects to the backend's job-delivery hub
/// (the same one the existing Bun/TypeScript Print Agent uses) and receives
/// <c>DeliverJob</c> events. The handler ACKs on the wire but the durable
/// queue is the actual truth — the companion will keep an acked job in
/// its SQLite queue even after the wire ack so it can survive a crash.
///
/// <para>Falls back to HTTP polling if the socket is unreachable; see
/// <see cref="HttpFallbackPoller"/>. The mock backend does not run a
/// Socket.IO server, so this client is paired with the polling fallback
/// for local testing.</para>
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

    public event Action<PrintJobRecord>? JobDelivered;
    public event Action<bool>? ConnectionChanged;

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
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            ReconnectionDelay = 2000,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
        });

        _socket.OnConnected += OnConnected;
        _socket.OnDisconnected += OnDisconnected;
        _socket.OnError += OnError;
        _socket.On(CompanionProtocol.HubMethodDeliverJob, OnJobDelivered);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try { await _socket.ConnectAsync(); }
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
        var baseUrl = _backend.CurrentShop?.BaseUrl ?? (string.IsNullOrWhiteSpace(o.BaseUrl) ? null : new Uri(o.BaseUrl));
        if (baseUrl is null) throw new InvalidOperationException("Backend URL not configured");
        return new Uri(baseUrl, o.HubPath.TrimStart('/'));
    }

    private async Task OnConnected(SocketIO _)
    {
        _logger.LogInformation("Socket.IO connected");
        ConnectionChanged?.Invoke(true);
        // Register the device on the hub so the backend can route events to us.
        await _socket.EmitAsync("registerDevice", new { deviceId = ThisApp.DeviceId.ToString("N") });
    }

    private void OnDisconnected(object? _, string reason)
    {
        _logger.LogWarning("Socket.IO disconnected: {Reason}; HTTP poll fallback active", reason);
        ConnectionChanged?.Invoke(false);
        _poller.Start(_cts?.Token ?? CancellationToken.None);
    }

    private void OnError(object? _, string err) => _logger.LogError("Socket.IO error: {Err}", err);

    private async Task OnJobDelivered(SocketIOEvent ev)
    {
        try
        {
            var dto = ev.GetValue<PrintJobDto>();
            var record = new PrintJobRecord
            {
                JobId = dto.JobId,
                ShopId = dto.ShopId,
                DeviceId = dto.DeviceId,
                Title = dto.Title,
                PrinterName = dto.PrinterName,
                Copies = dto.Copies,
                PageRange = dto.PageRange,
                PaperSize = dto.PaperSize,
                Color = dto.Color,
                Duplex = dto.Duplex,
                PdfUrl = dto.PdfUrl,
                PdfSha256 = dto.PdfSha256,
                Status = JobStatus.Pending,
                CreatedAt = dto.CreatedAt,
                QueuedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            // Persist before processing. EnqueueAsync is idempotent on JobId.
            record = await _queue.EnqueueAsync(record);
            JobDelivered?.Invoke(record);
            // ACK on the wire — the queue is still the source of truth.
            await _socket.EmitAsync(CompanionProtocol.HubMethodAckJob, new { jobId = dto.JobId });
            _logger.LogInformation("Job {JobId} delivered via Socket.IO and persisted", dto.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnJobDelivered failed");
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
        _poller.Dispose();
    }
}

/// <summary>
/// HTTP long-poll fallback. Mirrors the existing Bun/TypeScript agent's
/// 2-second polling loop. Used when Socket.IO cannot connect, and as the
/// sole transport when running against the local mock backend (which does
/// not run a Socket.IO server).
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
                    foreach (var dto in jobs)
                    {
                        var record = await _queue.EnqueueAsync(dto);
                        JobDelivered?.Invoke(record);
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
