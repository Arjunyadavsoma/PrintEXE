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
using PrintShopCompanion.Infrastructure.Storage;
using PrintShopCompanion.Shared.Dto;

/// <summary>
/// HTTP REST client for the PrintShop backend. Implements <see cref="IPrintShopBackend"/>.
///
/// <para><b>Auth model (v0.4.7+):</b> the companion authenticates with a
/// device token sent as <c>Authorization: Bearer &lt;deviceToken&gt;</c>.
/// The token is obtained by the shopkeeper on the web dashboard via
/// <c>POST /api/v1/agents/register</c> and pasted into PrintEXE.</para>
///
/// <para>The token is never logged; the Serilog setup redacts headers
/// named <c>Authorization</c> and any property containing "Token".</para>
/// </summary>
public sealed class HttpPrintShopBackend : IPrintShopBackend, IDisposable
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<BackendOptions> _opts;
    private readonly ILogger<HttpPrintShopBackend> _logger;
    private readonly LocalStorageLayout? _layout;
    private DeviceToken? _token;
    private DeviceRegistration? _device;
    private ShopInfo? _shop;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpPrintShopBackend(
        HttpClient http,
        IOptionsMonitor<BackendOptions> opts,
        ILogger<HttpPrintShopBackend> logger,
        LocalStorageLayout? layout = null)
    {
        _http = http;
        _opts = opts;
        _logger = logger;
        _layout = layout;
    }

    public bool IsAuthenticated => _token is not null;
    public ShopInfo? CurrentShop => _shop;
    public DeviceRegistration? CurrentDevice => _device;

    /// <summary>
    /// Store the device token obtained from the web dashboard. The shopkeeper
    /// pastes the token into PrintEXE; PrintEXE stores it (DPAPI-encrypted)
    /// and uses it as the Bearer credential on every subsequent call.
    /// <para>This method ALSO validates the token by calling heartbeat — if
    /// the backend rejects it, the token is not stored.</para>
    /// </summary>
    public async Task PairWithDeviceTokenAsync(string deviceToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new ArgumentException("Device token cannot be empty", nameof(deviceToken));

        var o = _opts.CurrentValue;
        var baseUri = !string.IsNullOrWhiteSpace(o.BaseUrl)
            ? new Uri(o.BaseUrl)
            : throw new InvalidOperationException("Backend BaseUrl is not configured");

        // Temporarily set the token and call heartbeat to validate it.
        _token = new DeviceToken
        {
            ShopId = "",  // will be filled in by heartbeat response (via shop resolution)
            AgentId = "",  // not known until heartbeat
            Token = deviceToken,
            IssuedAt = DateTimeOffset.UtcNow
        };
        _shop = new ShopInfo
        {
            Id = "",
            Slug = "",
            Name = "(validating...)",
            BaseUrl = baseUri,
            ApiBasePath = o.ApiBasePath
        };

        try
        {
            var hb = await HeartbeatAsync(ct);
            _logger.LogInformation("Device token validated: serverTime={ServerTime}", hb.ServerTime);
        }
        catch (Exception ex)
        {
            _token = null;
            _shop = null;
            throw new InvalidOperationException(
                "Device token validation failed. The token may be invalid, expired, or revoked. " +
                "Generate a new one from the web dashboard's Printer Setup page.", ex);
        }

        // Persist the token via the credential store (DPAPI-encrypted).
        // We do this through the static DeviceTokenStore because the credential
        // store interface is shared with the shopkeeper flow.
        try
        {
            var credPath = ResolveDeviceTokenPath();
            Directory.CreateDirectory(Path.GetDirectoryName(credPath)!);
            byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(deviceToken);
            byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(
                plaintext, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(credPath, encrypted, ct);
            _logger.LogInformation("Device token persisted to {Path}", credPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist device token (non-fatal — will need to re-paste on restart)");
        }

        _device = new DeviceRegistration
        {
            DeviceId = ThisApp.DeviceId.ToString("N"),
            ShopId = _shop.Id,
            Hostname = Environment.MachineName,
            OperatingSystem = "win32",
            AppVersion = ThisApp.Version,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Revoked = false
        };
    }

    /// <summary>
    /// Restore the saved device token from the DPAPI-encrypted file. Called
    /// by App startup to resume a previous session without re-pairing.
    /// </summary>
    public async Task<bool> RestoreAsync(CancellationToken ct = default)
    {
        try
        {
            var credPath = ResolveDeviceTokenPath();
            if (!File.Exists(credPath))
            {
                _logger.LogInformation("No saved device token — user needs to pair");
                return false;
            }
            byte[] encrypted = await File.ReadAllBytesAsync(credPath, ct);
            byte[] plaintext = System.Security.Cryptography.ProtectedData.Unprotect(
                encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            string deviceToken = System.Text.Encoding.UTF8.GetString(plaintext);
            await PairWithDeviceTokenAsync(deviceToken, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore device token — user needs to re-pair");
            return false;
        }
    }

    /// <summary>
    /// Exchange the device token for a short-lived JWT (15 min TTL) for
    /// Socket.IO auth. The JWT is returned as a raw string — PrintEXE does
    /// NOT need to verify it (the Socket.IO server does that).
    /// </summary>
    public async Task<string> ExchangeDeviceTokenForJwtAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("agents/token");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend (token may be revoked or expired)");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<AgentTokenResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty token response");
        if (envelope.Data is null)
            throw new InvalidOperationException("Token response missing data field");

        _logger.LogInformation("Exchanged device token for JWT (expires in {ExpiresIn}s)", envelope.Data.ExpiresIn);
        return envelope.Data.AccessToken;
    }

    /// <summary>POST /api/v1/agents/heartbeat — refresh presence.</summary>
    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("agents/heartbeat");
        var body = new
        {
            printerName = _device?.Hostname ?? Environment.MachineName,
            version = ThisApp.Version,
            os = "win32"
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<AgentHeartbeatResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty heartbeat response");
        var dto = envelope.Data;
        var serverTime = dto?.Ts ?? DateTimeOffset.UtcNow;
        // If the heartbeat returned a shopId, update our shop info.
        // (The /agents/heartbeat response doesn't currently include shopId,
        // but the auth middleware resolves it server-side. We use the
        // previously-stored shopId from the device token.)
        return new HeartbeatResult(serverTime, TokenStillValid: true, RecommendedBackendVersion: null);
    }

    /// <summary>GET /api/v1/agents/jobs/pending — fetch queued print jobs (up to 10).</summary>
    public async Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("agents/jobs/pending");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<PendingJobsEnvelopeDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty pending-jobs response");
        var jobs = envelope.Data?.Jobs ?? new List<PrintJobDto>();
        return jobs.Select(MapToRecord).ToList();
    }

    /// <summary>
    /// POST /api/v1/agents/jobs/{id}/status — claim / complete / fail a job.
    /// Atomic claim semantics: sending <c>status:"printing"</c> succeeds only
    /// if the job is still in <c>queued</c> state. If another agent claimed
    /// it first, the response has <c>skipped:true</c>.
    /// </summary>
    public async Task<JobStatusReport> ReportJobStatusAsync(
        string jobId, string status, string? printerName, string? errorMessage, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl($"agents/jobs/{jobId}/status");
        var body = new AgentJobStatusRequestDto
        {
            Status = status,
            PrinterName = printerName,
            LastError = errorMessage
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<AgentJobStatusResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty status response");
        var dto = envelope.Data;
        return new JobStatusReport(
            JobId: dto?.JobId,
            Status: dto?.Status,
            Applied: dto?.Applied ?? false,
            Skipped: dto?.Skipped ?? false,
            Reason: dto?.Reason);
    }

    /// <summary>
    /// Download the PDF bytes for a job. The <paramref name="pdfUrl"/> is a
    /// presigned S3/Supabase URL with 15-minute TTL — no auth needed (the
    /// signature is in the URL).
    /// </summary>
    public async Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default)
    {
        // v0.5: use LocalStorageLayout for managed paths with atomic rename.
        // Fall back to the old %Temp% path if layout is not injected (Linux build
        // without the DI registration, or backward compat).
        string finalPath;
        string tempPath;

        if (_layout is not null)
        {
            _layout.EnsureDirectories();
            finalPath = _layout.GetPdfPath(jobId, "queued");
            tempPath = _layout.GetIncomingTempPath(jobId);
        }
        else
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "PrintShop", "Companion", "pdfs");
            Directory.CreateDirectory(cacheDir);
            finalPath = Path.Combine(cacheDir, $"{jobId}.pdf");
            tempPath = finalPath + ".tmp";
        }

        // If we already have the FINAL file and the hash matches, skip download.
        if (File.Exists(finalPath) && !string.IsNullOrEmpty(expectedSha256))
        {
            var actual = await Sha256Async(finalPath, ct);
            if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return new DownloadedPdf(finalPath, actual, new FileInfo(finalPath).Length);
        }

        // Download to .tmp file (NOT the final path). If the download crashes
        // midway, the .tmp file is incomplete but the final path is untouched.
        // On next run, we detect the stale .tmp + re-download.
        using var req = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
        // Presigned URL — no auth header needed (the signature is in the URL).
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // Refuse oversized PDFs.
        if (resp.Content.Headers.ContentLength is { } len && len > 100 * 1024 * 1024)
            throw new InvalidOperationException($"PDF exceeds the 100 MB limit (got {len} bytes)");

        await using (var fs = File.Create(tempPath))
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(fs, 81920, ct);
        }

        // Verify SHA-256 of the .tmp file BEFORE atomic rename.
        var sha = await Sha256Async(tempPath, ct);
        if (!string.IsNullOrEmpty(expectedSha256) &&
            !string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Hash mismatch — delete the .tmp and throw. The cloud copy is NOT
            // deleted (the companion never called /received for this file).
            try { File.Delete(tempPath); } catch { }
            throw new InvalidOperationException($"PDF hash mismatch (expected {expectedSha256}, got {sha})");
        }

        // ATOMIC RENAME: .tmp → .pdf. On Windows, File.Move is atomic when both
        // paths are on the same volume (which they are — both under %LocalAppData%
        // or both under %Temp%). If the final file already exists (e.g. from a
        // previous run), delete it first.
        if (File.Exists(finalPath))
        {
            try { File.Delete(finalPath); } catch { /* may be locked — rename will fail below */ }
        }
        try
        {
            File.Move(tempPath, finalPath);
        }
        catch (IOException)
        {
            // If the rename fails (e.g. final file is locked by the spooler),
            // fall back to copy + delete. This is NOT atomic but is the best
            // we can do on Windows when the target is locked.
            File.Copy(tempPath, finalPath, overwrite: true);
            try { File.Delete(tempPath); } catch { }
        }

        return new DownloadedPdf(finalPath, sha, new FileInfo(finalPath).Length);
    }

    /// <summary>Revoke the device token locally. Clears the in-memory + persisted credentials.</summary>
    public async Task RevokeDeviceAsync(CancellationToken ct = default)
    {
        // Best-effort: notify the backend so the dashboard shows us offline immediately.
        // We don't have a DELETE /agents/{id} call here (that's a JWT-auth endpoint
        // called from the web dashboard). We just clear the local token.
        try
        {
            var credPath = ResolveDeviceTokenPath();
            if (File.Exists(credPath)) File.Delete(credPath);
            _logger.LogInformation("Device token deleted from {Path}", credPath);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete device token file"); }
        _token = null; _device = null; _shop = null;
    }

    // ────────────────────────── sync endpoints ──────────────────────────

    public async Task<SyncPullResult> SyncPullAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        // Initial cursor: 1970-01-01T00:00:00Z (pulls everything on first sync).
        var c = string.IsNullOrEmpty(cursor) ? "1970-01-01T00:00:00Z" : cursor;
        var url = ApiUrl($"sync/pull?cursor={Uri.EscapeDataString(c)}&limit={limit}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<SyncPullResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty sync/pull response");
        var dto = envelope.Data ?? throw new InvalidOperationException("sync/pull response missing data");
        return new SyncPullResult(
            Changes: dto.Changes.Select(c => new SyncChange(
                Entity: c.Entity, Id: c.Id, Action: c.Action,
                DataJson: c.Data.GetRawText() ?? "{}",
                Revision: c.Revision, UpdatedAt: c.UpdatedAt)).ToList(),
            NextCursor: dto.NextCursor,
            HasMore: dto.HasMore);
    }

    public async Task<SyncPushResult> SyncPushAsync(object changes, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("sync/push");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(changes)
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<SyncPushResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty sync/push response");
        var dto = envelope.Data ?? throw new InvalidOperationException("sync/push response missing data");
        return new SyncPushResult(
            Results: dto.Results.Select(r => new SyncPushResultItem(
                r.Id, r.Accepted, r.ServerRevision, r.Conflict)).ToList());
    }

    public async Task<SyncStatusResult> SyncStatusAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("sync/status");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<SyncStatusResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty sync/status response");
        var dto = envelope.Data ?? throw new InvalidOperationException("sync/status response missing data");
        return new SyncStatusResult(
            DeviceId: dto.DeviceId, ShopId: dto.ShopId,
            SyncCursor: dto.SyncCursor, LastSyncAt: dto.LastSyncAt,
            AgentName: dto.AgentName, IsOnline: dto.IsOnline);
    }

    /// <summary>
    /// Durable receipt acknowledgement — POST /api/v1/agents/jobs/{jobId}/received.
    /// Confirms the PDF is on the shop PC + SHA-256 verified + atomically persisted.
    /// The backend records the receipt + schedules cloud cleanup (15-min grace).
    /// Idempotent: calling twice returns success without re-scheduling.
    /// </summary>
    public async Task<ReceiptAcknowledgement> MarkJobReceivedAsync(
        string jobId, string checksum, long fileSize, DateTimeOffset receivedAt,
        CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl($"agents/jobs/{jobId}/received");

        // The backend zod schema expects checksum as a pure hex string
        // matching /^[a-f0-9]{64}$/i — NOT "sha256:" prefixed. Strip the
        // prefix if present (our internal code uses "sha256:" prefixed format).
        var hexChecksum = checksum.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? checksum.Substring("sha256:".Length)
            : checksum;

        var body = new JobReceivedRequestDto
        {
            Checksum = hexChecksum,           // pure hex, no prefix
            FileSize = fileSize,
            ReceivedAt = receivedAt.ToString("O"),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        // 404 = job not found or not in this shop (tenant isolation)
        // 409 = job already in terminal state (completed/cancelled)
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<JobReceivedResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty received response");
        var dto = envelope.Data;

        // Map the backend's actual response shape to our ReceiptAcknowledgement.
        // The backend returns { jobId, received, cleanupScheduled, cleanupScheduledAt?, message }
        return new ReceiptAcknowledgement(
            JobId: dto?.JobId,
            DeliveryStatus: dto?.Received == true ? "received" : null,
            StorageStatus: dto?.Received == true ? "local_verified" : null,
            CloudDeleteAfter: dto?.CleanupScheduledAt,
            Skipped: dto?.Received == true && dto?.CleanupScheduled == false,
            Reason: dto?.Message);
    }

    // ────────────────────────── helpers ──────────────────────────

    private void EnsureAuthenticated()
    {
        if (_token is null)
            throw new InvalidOperationException("Not paired with a shop — paste the device token from the web dashboard first");
    }

    /// <summary>
    /// Add <c>Authorization: Bearer &lt;deviceToken&gt;</c> to the request.
    /// This is the auth model the production backend expects per the
    /// integration guide (section 2.1). The previous v0.4.6 code used
    /// custom headers (X-PrintShop-Device-Token) — the production backend
    /// does NOT recognize those.
    /// </summary>
    private void AddAuth(HttpRequestMessage req)
    {
        if (_token is null) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", ThisApp.Version));
    }

    private Uri ApiUrl(string relative) => new(_shop!.BaseUrl, $"{_shop.ApiBasePath.TrimStart('/')}/{relative.TrimStart('/')}");

    /// <summary>
    /// Resolve the path to the DPAPI-encrypted device token file. Defaults
    /// to <c>%AppData%\PrintShop\Companion\device.dat</c>. Override via
    /// <c>Storage:DeviceTokenPath</c> in appsettings.json.
    /// </summary>
    private string ResolveDeviceTokenPath()
    {
        var o = _opts.CurrentValue;
        // The StorageOptions are separate from BackendOptions, but for the
        // token path we use a sensible default if the caller hasn't set one.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "device.dat");
        return path;
    }

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
        ShopId = "",
        DeviceId = "",
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
