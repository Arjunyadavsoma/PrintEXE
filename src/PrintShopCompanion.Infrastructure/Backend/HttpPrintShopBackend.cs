namespace PrintShopCompanion.Infrastructure.Backend;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Shared.Constants;
using PrintShopCompanion.Shared.Dto;

/// <summary>
/// HTTP REST client for the PrintShop backend. Implements <see cref="IPrintShopBackend"/>.
///
/// <para>This client carries the device token in <see cref="CompanionProtocol.DeviceTokenHeader"/>
/// on every authenticated request. The token is never logged; the <c>Serilog</c> setup
/// redacts headers named <c>X-PrintShop-Device-Token</c> and <c>Authorization</c>.</para>
/// </summary>
public sealed class HttpPrintShopBackend : IPrintShopBackend, IDisposable
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _store;
    private readonly IOptionsMonitor<BackendOptions> _opts;
    private readonly ILogger<HttpPrintShopBackend> _logger;
    private DeviceToken? _token;
    private DeviceRegistration? _device;
    private ShopInfo? _shop;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpPrintShopBackend(
        HttpClient http,
        ICredentialStore store,
        IOptionsMonitor<BackendOptions> opts,
        ILogger<HttpPrintShopBackend> logger)
    {
        _http = http;
        _store = store;
        _opts = opts;
        _logger = logger;
    }

    public bool IsAuthenticated => _token is not null;
    public ShopInfo? CurrentShop => _shop;
    public DeviceRegistration? CurrentDevice => _device;

    /// <summary>Called by the App on startup to restore the saved device token.</summary>
    public async Task<bool> RestoreAsync(CancellationToken ct = default)
    {
        var creds = await _store.LoadAsync(ct); _token = creds?.DeviceToken;
        if (_token is null) return false;
        var o = _opts.CurrentValue;
        var baseUri = !string.IsNullOrWhiteSpace(o.BaseUrl)
            ? new Uri(o.BaseUrl)
            : null;
        if (baseUri is null)
        {
            _logger.LogWarning("Saved device token present but backend BaseUrl is unset; cannot authenticate");
            _token = null;
            return false;
        }
        _shop = new ShopInfo
        {
            ShopId = _token.ShopId,
            ShopName = "(restored)",
            BaseUrl = baseUri,
            ApiBasePath = o.ApiBasePath
        };
        // Confirm by issuing a heartbeat.
        try
        {
            var hb = await HeartbeatAsync(ct);
            _device = new DeviceRegistration
            {
                DeviceId = _token.DeviceId,
                ShopId = _token.ShopId,
                Hostname = Environment.MachineName,
                OperatingSystem = Environment.OSVersion.ToString(),
                AppVersion = ThisApp.Version,
                RegisteredAt = hb.ServerTime,
                LastSeenAt = hb.ServerTime,
                Revoked = !hb.TokenStillValid
            };
            return hb.TokenStillValid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat during restore failed; will assume unauthenticated");
            _token = null; _shop = null;
            return false;
        }
    }

    public async Task<PairingResult> PairAsync(string pairingToken, CancellationToken ct = default)
    {
        var o = _opts.CurrentValue;
        var baseUri = !string.IsNullOrWhiteSpace(o.BaseUrl) ? new Uri(o.BaseUrl) : throw new InvalidOperationException("Backend BaseUrl is not configured");
        var req = new PairingRequestDto
        {
            PairingToken = pairingToken,
            DeviceId = ThisApp.DeviceId.ToString("N"),
            Hostname = Environment.MachineName,
            OperatingSystem = Environment.OSVersion.ToString(),
            AppVersion = ThisApp.Version
        };
        var pairUrl = new Uri(baseUri, $"{o.ApiBasePath.TrimStart('/')}/pair");
        using var resp = await _http.PostAsJsonAsync(pairUrl, req, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<PairingResponseDto>(Json, ct)
                  ?? throw new InvalidOperationException("Pairing response was empty");

        _token = new DeviceToken
        {
            ShopId = dto.ShopId,
            DeviceId = dto.DeviceId,
            Token = dto.DeviceToken,
            RefreshToken = dto.RefreshToken,
            IssuedAt = dto.IssuedAt,
        };
        await _store.SaveAsync(new StoredCredentials { DeviceToken = _token }, ct);

        ThisApp.DeviceId = Guid.Parse(dto.DeviceId);

        _shop = new ShopInfo
        {
            ShopId = dto.ShopId,
            ShopName = dto.ShopName,
            BaseUrl = dto.BaseUrl,
            ApiBasePath = o.ApiBasePath
        };
        _device = new DeviceRegistration
        {
            DeviceId = dto.DeviceId,
            ShopId = dto.ShopId,
            Hostname = req.Hostname,
            OperatingSystem = req.OperatingSystem,
            AppVersion = req.AppVersion,
            RegisteredAt = dto.RegisteredAt,
            LastSeenAt = dto.LastSeenAt,
            Revoked = false
        };
        _logger.LogInformation("Paired device {DeviceId} with shop {ShopId} ({Shop})", dto.DeviceId, dto.ShopId, dto.ShopName);
        return new PairingResult(_token, _device, _shop);
    }

    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
    {
        if (_token?.RefreshToken is null) return false;
        var o = _opts.CurrentValue;
        var url = new Uri(_shop?.BaseUrl ?? new Uri(o.BaseUrl!), $"{o.ApiBasePath.TrimStart('/')}/refresh");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-PrintShop-Refresh-Token", _token.RefreshToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;
        var dto = await resp.Content.ReadFromJsonAsync<PairingResponseDto>(Json, ct);
        if (dto is null) return false;
        _token = _token with { Token = dto.DeviceToken, IssuedAt = dto.IssuedAt };
        await _store.SaveAsync(new StoredCredentials { DeviceToken = _token }, ct);
        return true;
    }

    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("heartbeat");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<HeartbeatResponseDto>(Json, ct)
                  ?? throw new InvalidOperationException("Empty heartbeat");
        return new HeartbeatResult(dto.ServerTime, dto.TokenStillValid, dto.RecommendedBackendVersion);
    }

    public async Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("agents/jobs/pending");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var dtos = await resp.Content.ReadFromJsonAsync<List<PrintJobDto>>(Json, ct) ?? new();
        return dtos.Select(MapToRecord).ToList();
    }

    public async Task AcknowledgeJobAsync(string jobId, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl($"jobs/{jobId}/ack");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new AckJobRequestDto
            {
                JobId = jobId,
                DeviceId = _token!.DeviceId,
                AcceptedAt = DateTimeOffset.UtcNow
            })
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ReportJobStatusAsync(string jobId, DateTimeOffset? printedAt, DateTimeOffset? failedAt, string? errorMessage, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl($"jobs/{jobId}/status");
        var dto = new JobStatusReportDto
        {
            JobId = jobId,
            PrintedAt = printedAt,
            FailedAt = failedAt,
            ErrorMessage = errorMessage
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(dto)
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var cacheDir = Path.Combine(Path.GetTempPath(), "PrintShop", "Companion", "pdfs");
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, $"{jobId}.pdf");

        // If we already have the file and the hash matches, skip download.
        if (File.Exists(path) && !string.IsNullOrEmpty(expectedSha256))
        {
            var actual = await Sha256Async(path, ct);
            if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return new DownloadedPdf(path, actual, new FileInfo(path).Length);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // Refuse oversized PDFs.
        if (resp.Content.Headers.ContentLength is { } len && len > CompanionProtocol.MaxPdfBytes)
            throw new InvalidOperationException($"PDF exceeds the {CompanionProtocol.MaxPdfBytes}-byte limit");

        await using (var fs = File.Create(path))
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(fs, 81920, ct);
        }
        var sha = await Sha256Async(path, ct);
        if (!string.IsNullOrEmpty(expectedSha256) &&
            !string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException($"PDF hash mismatch (expected {expectedSha256}, got {sha})");
        }
        return new DownloadedPdf(path, sha, new FileInfo(path).Length);
    }

    public async Task RevokeDeviceAsync(CancellationToken ct = default)
    {
        if (_token is null) { await _store.ClearAsync(ct); return; }
        try
        {
            var url = ApiUrl("device/revoke");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddAuth(req);
            await _http.SendAsync(req, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Revoke call to backend failed; clearing local token anyway"); }
        await _store.ClearAsync(ct);
        _token = null; _device = null; _shop = null;
    }

    // ────────────────────────── helpers ──────────────────────────

    private void EnsureAuthenticated()
    {
        if (_token is null) throw new InvalidOperationException("Not paired with a shop");
        if (false)
            throw new InvalidOperationException("Device token expired");
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (_token is null) return;
        req.Headers.Add(CompanionProtocol.DeviceTokenHeader, _token.Token);
        req.Headers.Add(CompanionProtocol.DeviceIdHeader, _token.DeviceId);
        req.Headers.Add(CompanionProtocol.AppVersionHeader, ThisApp.Version);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", ThisApp.Version));
    }

    private Uri ApiUrl(string relative) => new(_shop!.BaseUrl, $"{_shop.ApiBasePath.TrimStart('/')}/{relative.TrimStart('/')}");

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PrintJobRecord MapToRecord(PrintJobDto dto) => new()
    {
        JobId = dto.JobId,
        ShopId = "", // The real /agents/jobs/pending doesn't return shopId; the device token is shop-scoped
        DeviceId = "", // Same for deviceId
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

    public void Dispose() => _http.Dispose();
}

/// <summary>Process-wide statics: device id, app version, install path.</summary>
internal static class ThisApp
{
    public static Guid DeviceId { get; set; } = LoadOrMintDeviceId();
    public static string Version { get; } =
        typeof(ThisApp).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    private static Guid LoadOrMintDeviceId()
    {
        // Persisted alongside the device token; pairing sends this to the backend.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "device-id.txt");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out var g)) return g;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fresh = Guid.NewGuid();
        File.WriteAllText(path, fresh.ToString());
        return fresh;
    }
}
