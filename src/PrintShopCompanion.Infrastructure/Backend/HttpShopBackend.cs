namespace PrintShopCompanion.Infrastructure.Backend;

using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Shared.Dto;

/// <summary>
/// HTTP client for the shopkeeper-facing API (auth, orders, analytics, shop profile).
/// Authenticated via the shopkeeper's JWT access token. Auto-refreshes when the
/// access token expires (one retry per call).
///
/// <para>This is the <em>shopkeeper</em> backend. The print-agent backend
/// (<see cref="HttpPrintShopBackend"/>) is a separate client authenticated via
/// the device token. The two clients coexist in the same process — the
/// shopkeeper signs in once, then registers a device, then the background
/// engine uses the device token for job-related calls.</para>
/// </summary>
public sealed class HttpShopBackend : IShopBackend, IDisposable
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<BackendOptions> _opts;
    private readonly ILogger<HttpShopBackend> _logger;
    private ShopSession? _session;
    private ShopInfo? _shop;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpShopBackend(HttpClient http, IOptionsMonitor<BackendOptions> opts, ILogger<HttpShopBackend> logger)
    {
        _http = http;
        _opts = opts;
        _logger = logger;
    }

    public bool IsAuthenticated => _session is not null && _session.AccessExpiresAt > DateTimeOffset.UtcNow;
    public ShopkeeperUser? CurrentUser => _session?.User;
    public ShopInfo? CurrentShop => _shop;

    /// <summary>Restore a saved session. Called by the App at startup.</summary>
    public void RestoreSession(ShopSession session) => _session = session;

    /// <summary>Restore the cached shop profile (after RestoreSession succeeds).</summary>
    public void RestoreShop(ShopInfo shop) => _shop = shop;

    // ── Authentication ──────────────────────────────────────────────

    public async Task<ShopSession> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var url = ApiUrl("auth/login");
        var resp = await _http.PostAsJsonAsync(url, new { email, password }, ct);
        var session = await ReadEnvelopeAsync<LoginResponseDto>(resp, ct);
        var now = DateTimeOffset.UtcNow;
        _session = new ShopSession
        {
            AccessToken   = session.AccessToken,
            RefreshToken  = session.RefreshToken,
            ExpiresIn     = session.ExpiresIn,
            AccessExpiresAt = now.AddSeconds(session.ExpiresIn),
            User = new ShopkeeperUser
            {
                Id     = session.User.Id,
                Email  = session.User.Email,
                Role   = session.User.Role,
                ShopId = session.User.ShopId
            },
            IssuedAt = now
        };
        _logger.LogInformation("Shopkeeper {Email} signed in (shop={ShopId})", email, session.User.ShopId);
        return _session;
    }

    public async Task<ShopSession> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var url = ApiUrl("auth/refresh");
        var resp = await _http.PostAsJsonAsync(url, new { refreshToken }, ct);
        var dto = await ReadEnvelopeAsync<RefreshResponseDto>(resp, ct);
        var now = DateTimeOffset.UtcNow;
        _session = _session is null
            ? throw new InvalidOperationException("Cannot refresh — no existing session")
            : _session with
            {
                AccessToken   = dto.AccessToken,
                RefreshToken  = dto.RefreshToken,
                AccessExpiresAt = now.AddSeconds(900),  // backend doesn't return expiresIn on refresh; assume 15 min
                IssuedAt = now
            };
        return _session;
    }

    public async Task<ShopkeeperUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Get, "me", ct);
        var user = await ReadEnvelopeAsync<UserDto>(resp, ct);
        return new ShopkeeperUser
        {
            Id = user.Id, Email = user.Email, Role = user.Role, ShopId = user.ShopId
        };
    }

    // ── Shop profile ────────────────────────────────────────────────

    public async Task<ShopInfo> GetShopAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Get, "shops/me", ct);
        var dto = await ReadEnvelopeAsync<ShopDto>(resp, ct);
        _shop = MapShop(dto);
        return _shop;
    }

    public async Task<ShopInfo> UpdateShopAsync(IDictionary<string, object?> patch, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Patch, "shops/me", ct, patch);
        var dto = await ReadEnvelopeAsync<ShopDto>(resp, ct);
        _shop = MapShop(dto);
        return _shop;
    }

    // ── Orders ──────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<OrderRecord> Orders, int Total)> ListOrdersAsync(
        OrderStatus? status = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var q = $"?limit={limit}&offset={offset}";
        if (status is { } s) q += $"&status={s.ToWire()}";
        var resp = await SendAuthedAsync(HttpMethod.Get, $"shops/me/orders{q}", ct);
        var envelope = await ReadEnvelopeWithPaginationAsync<OrderDto>(resp, ct);
        var orders = envelope.Data?.Select(d => MapOrder(d)).ToList() ?? new();
        return (orders, envelope.Pagination?.Total ?? orders.Count);
    }

    public async Task<OrderRecord> GetOrderAsync(string orderId, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Get, $"shops/me/orders/{orderId}", ct);
        var dto = await ReadEnvelopeAsync<OrderDto>(resp, ct);
        return MapOrder(dto);
    }

    public async Task<OrderRecord> UpdateOrderNotesAsync(string orderId, string? notes, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Patch, $"shops/me/orders/{orderId}", ct, new { notes });
        var dto = await ReadEnvelopeAsync<OrderDto>(resp, ct);
        return MapOrder(dto);
    }

    public async Task<OrderRecord> StartOrderAsync(string orderId, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Post, $"shops/me/orders/{orderId}/start", ct);
        // The /start endpoint returns { orderId, status, dispatched:[...] }, not the full OrderDto.
        // Re-fetch the order to get the full record.
        await ReadEnvelopeAsync<JsonElement>(resp, ct);
        return await GetOrderAsync(orderId, ct);
    }

    public async Task<OrderRecord> CompleteOrderAsync(string orderId, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Post, $"shops/me/orders/{orderId}/complete", ct);
        await ReadEnvelopeAsync<JsonElement>(resp, ct);
        return await GetOrderAsync(orderId, ct);
    }

    public async Task<OrderRecord> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Post, $"shops/me/orders/{orderId}/cancel", ct);
        await ReadEnvelopeAsync<JsonElement>(resp, ct);
        return await GetOrderAsync(orderId, ct);
    }

    public async Task<Stream> DownloadFileAsync(string orderId, string fileId, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Get, $"shops/me/orders/{orderId}/files/{fileId}/download", ct);
        if (resp.StatusCode == HttpStatusCode.Redirect)
        {
            // Follow the redirect to the presigned S3/R2 URL manually.
            var location = resp.Headers.Location;
            if (location is null) throw new InvalidOperationException("Redirect has no Location header");
            var redirectResp = await _http.GetAsync(location, ct);
            redirectResp.EnsureSuccessStatusCode();
            return await redirectResp.Content.ReadAsStreamAsync(ct);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    // ── Analytics + reports ────────────────────────────────────────

    public async Task<ShopAnalytics> GetAnalyticsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Get, "shops/me/analytics", ct);
        var dto = await ReadEnvelopeAsync<AnalyticsDto>(resp, ct);
        return MapAnalytics(dto);
    }

    public async Task<IReadOnlyList<DailyReportRow>> GetDailyReportsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var q = $"?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        var resp = await SendAuthedAsync(HttpMethod.Get, $"shops/me/reports/daily{q}", ct);
        var dto = await ReadEnvelopeAsync<List<DailyReportRowDto>>(resp, ct);
        return dto.Select(MapDaily).ToList();
    }

    // ── Devices ─────────────────────────────────────────────────────

    public async Task<DeviceToken> RegisterDeviceAsync(string name, string? os = null, string? version = null, string? printerName = null, CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Post, "agents/register", ct, new { name, os, version, printerName });
        var dto = await ReadEnvelopeAsync<RegisterAgentResponseDto>(resp, ct);
        return new DeviceToken
        {
            AgentId = dto.AgentId,
            ShopId  = dto.ShopId,
            Token   = dto.DeviceToken,
            IssuedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<IReadOnlyList<DeviceRegistration>> ListDevicesAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthedAsync(HttpMethod.Get, "shops/me/agents", ct);
        var dto = await ReadEnvelopeAsync<List<AgentDto>>(resp, ct);
        return dto.Select(a => new DeviceRegistration
        {
            DeviceId = a.Id,
            ShopId = "",
            Hostname = "",
            OperatingSystem = a.Os ?? "",
            AppVersion = a.Version ?? "",
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = a.LastSeenAt ?? DateTimeOffset.UtcNow,
            Revoked = false,
            Name = a.Name,
            IsOnline = a.IsOnline,
            PrinterName = a.PrinterName,
            Version = a.Version,
            Os = a.Os
        }).ToList();
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        _session = null;
        _shop = null;
        return Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string relative, CancellationToken ct, object? body = null)
    {
        EnsureAuthenticated();

        var attempt = 0;
        while (true)
        {
            attempt++;
            using var req = new HttpRequestMessage(method, ApiUrl(relative));
            AddAuth(req);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            // Auto-refresh on 401 (only one retry — avoid infinite loop).
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 1 && _session is { RefreshToken: { } refresh })
            {
                _logger.LogInformation("Access token expired; refreshing");
                try { await RefreshAsync(refresh, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Token refresh failed — surfacing 401");
                    return resp;
                }
                resp.Dispose();
                continue;  // retry with the new access token
            }
            return resp;
        }
    }

    private void EnsureAuthenticated()
    {
        if (_session is null) throw new InvalidOperationException("Not signed in");
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (_session is null) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", "0.2.0"));
    }

    private Uri ApiUrl(string relative)
    {
        var o = _opts.CurrentValue;
        var baseUri = !string.IsNullOrWhiteSpace(o.BaseUrl) ? new Uri(o.BaseUrl) : throw new InvalidOperationException("Backend BaseUrl not set");
        var path = $"{o.ApiBasePath.TrimStart('/')}/{relative.TrimStart('/')}";
        return new Uri(baseUri, path);
    }

    private static async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var err = await TryReadErrorAsync(resp, ct);
            throw new PrintShopApiException(resp.StatusCode, err.Code, err.Message);
        }
        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<T>>(Json, ct)
            ?? throw new PrintShopApiException(resp.StatusCode, "PARSE_ERROR", "Empty envelope");
        if (!envelope.Success || envelope.Data is null)
        {
            throw new PrintShopApiException(resp.StatusCode,
                envelope.Error?.Code ?? "UNKNOWN",
                envelope.Error?.Message ?? "Unknown error");
        }
        return envelope.Data;
    }

    private static async Task<ApiEnvelope<T>> ReadEnvelopeWithPaginationAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var err = await TryReadErrorAsync(resp, ct);
            throw new PrintShopApiException(resp.StatusCode, err.Code, err.Message);
        }
        return await resp.Content.ReadFromJsonAsync<ApiEnvelope<T>>(Json, ct)
            ?? throw new PrintShopApiException(resp.StatusCode, "PARSE_ERROR", "Empty envelope");
    }

    private static async Task<(string? Code, string? Message)> TryReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var env = await resp.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(Json, ct);
            return (env?.Error?.Code, env?.Error?.Message);
        }
        catch { return (null, null); }
    }

    // ── DTO → domain model mappers ─────────────────────────────────

    private static ShopInfo MapShop(ShopDto d) => new()
    {
        Id = d.Id, Name = d.Name, Slug = d.Slug,
        UpiId = d.UpiId, PaymentProvider = d.PaymentProvider,
        PricingConfig = d.PricingConfig, Address = d.Address, Phone = d.Phone,
        Timezone = d.Timezone ?? "Asia/Kolkata",
        IsActive = d.IsActive,
        SubscriptionTier = d.SubscriptionTier ?? "free",
        SubscriptionExpiresAt = d.SubscriptionExpiresAt,
        Description = d.Description, BusinessHours = d.BusinessHours,
        LogoUrl = d.LogoUrl, ServicesJson = d.ServicesJson,
        PaymentPhone = d.PaymentPhone, PaymentInstructions = d.PaymentInstructions,
        CashOnPickup = d.CashOnPickup
    };

    private static OrderRecord MapOrder(OrderDto d)
    {
        var status = OrderStatusExtensions.ParseWire(d.Status);
        return new OrderRecord
        {
            Id = d.Id, ShopId = d.ShopId, CustomerToken = d.CustomerToken,
            Status = status,
            TotalAmountPaise = d.TotalAmountPaise, Currency = d.Currency,
            PaymentProvider = d.PaymentProvider,
            PaymentTxnId = d.PaymentTxnId, PaymentUtr = d.PaymentUtr,
            PaidAt = d.PaidAt, CustomerPhone = d.CustomerPhone,
            Notes = d.Notes, TokenNumber = d.TokenNumber,
            CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
            Files = d.Files?.Select(MapFile).ToList()
        };
    }

    private static OrderFile MapFile(OrderFileDto f) => new()
    {
        Id = f.Id, OrderId = f.OrderId, OriginalFilename = f.OriginalFilename,
        StorageKey = f.StorageKey, MimeType = f.MimeType, SizeBytes = f.SizeBytes,
        PageCount = f.PageCount, Copies = f.Copies,
        ColorMode = f.ColorMode, PageRange = f.PageRange,
        PaperSize = f.PaperSize, Duplex = f.Duplex,
        PricePaise = f.PricePaise, Status = f.Status,
        PrintedAt = f.PrintedAt, CreatedAt = f.CreatedAt, UpdatedAt = f.UpdatedAt
    };

    private static ShopAnalytics MapAnalytics(AnalyticsDto d) => new()
    {
        Revenue = new RevenueSummary(d.Revenue.TotalPaise, d.Revenue.Formatted),
        OrderCount = new OrderCountSummary(d.OrderCount.Total, d.OrderCount.Paid),
        ByStatus = d.ByStatus.ToDictionary(
            kv => OrderStatusExtensions.ParseWire(kv.Key),
            kv => kv.Value),
        Popular = new PopularSettings(
            d.Popular.Color, d.Popular.Bw, d.Popular.Duplex,
            d.Popular.Simplex, d.Popular.A4, d.Popular.A3, d.Popular.Other),
        PeakHourUtc = d.PeakHourUtc,
        Hours = d.Hours
    };

    private static DailyReportRow MapDaily(DailyReportRowDto d) => new()
    {
        Date = d.Date,
        Orders = d.Orders,
        RevenuePaise = d.RevenuePaise,
        Files = d.Files,
        ByStatus = d.ByStatus.ToDictionary(
            kv => OrderStatusExtensions.ParseWire(kv.Key),
            kv => kv.Value)
    };

    public void Dispose() => _http.Dispose();
}

/// <summary>Thrown when a PrintShop API call fails (non-2xx response).</summary>
public sealed class PrintShopApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }

    public PrintShopApiException(HttpStatusCode status, string? code, string? message)
        : base($"[{(int)status}] {code}: {message}")
    {
        StatusCode = status;
        ErrorCode = code;
    }
}
