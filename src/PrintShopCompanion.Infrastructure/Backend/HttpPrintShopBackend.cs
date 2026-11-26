namespace PrintShopCompanion.Infrastructure.Backend;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Storage;
using PrintShopCompanion.Shared.Constants;
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
    private readonly IOptionsMonitor<PrinterOptions>? _printerOpts;
    private readonly ILogger<HttpPrintShopBackend> _logger;
    private readonly LocalStorageLayout? _layout;
    private DeviceToken? _token;
    private DeviceRegistration? _device;
    private ShopInfo? _shop;
    /// <summary>v0.7.2 sync: agent id assigned by the SERVER (pair/heartbeat/pending responses) — see <see cref="IPrintShopBackend.ServerAgentId"/>.</summary>
    private string? _serverAgentId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Write-side serializer: NEVER emit JSON nulls. v0.7.2 sync with the SaaS
    /// route zod (<c>agents/jobs/[id]/status</c>): <c>lastError</c> and
    /// <c>printerName</c> are <c>.optional()</c> but NOT <c>.nullable()</c> —
    /// a literal <c>"lastError":null</c> in the body fails validation with
    /// 400 VALIDATION_ERROR on the production backend (the integration fake
    /// now enforces the same rule). Internal so tests can assert the omission.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonWrite = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpPrintShopBackend(
        HttpClient http,
        IOptionsMonitor<BackendOptions> opts,
        ILogger<HttpPrintShopBackend> logger,
        LocalStorageLayout? layout = null,
        IOptionsMonitor<PrinterOptions>? printerOpts = null)
    {
        _http = http;
        _opts = opts;
        _printerOpts = printerOpts;
        _logger = logger;
        _layout = layout;
    }

    public bool IsAuthenticated => _token is not null;
    public ShopInfo? CurrentShop => _shop;
    public DeviceRegistration? CurrentDevice => _device;

    /// <inheritdoc cref="IPrintShopBackend.ServerAgentId"/>
    public string? ServerAgentId => _serverAgentId;

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

        // Persist the token via the atomic DPAPI helper (.tmp/.bak/verify —
        // the same safety pattern used for rotation).
        try
        {
            await PersistDeviceTokenAsync(deviceToken, ct);
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
    /// v1 contract §2 — exchange a pairing ticket for a device token.
    /// POST /api/v1/companion/pair with NO Authorization header (the ticket
    /// IS the authorization). On success, persist the returned device token
    /// via the same DPAPI path as <see cref="PairWithDeviceTokenAsync"/> and
    /// store the <see cref="PairResult.ProtocolVersion"/>.
    /// <para>Throws <see cref="PairingTicketException"/> for the contract's
    /// ticket-specific error codes so the UI can map each to a user state.</para>
    /// </summary>
    public async Task<PairResult> PairWithTicketAsync(
        string ticket,
        string deviceName,
        string? version = null,
        string? os = null,
        string? printerName = null,
        CancellationToken ct = default)
    {
        // Local validation BEFORE the network call — fail fast with a clear error.
        // Caps match the actual SaaS zod schema (Phase 3 fix — the published contract
        // said deviceName 1-255 but the implementation caps at 120; version/os at 40;
        // printerName at 200). See docs/PHASE3_CONTRACT_MATRIX.md Mismatch #4.
        if (string.IsNullOrWhiteSpace(ticket))
            throw new PairingTicketException("VALIDATION_ERROR", "Pairing code cannot be empty.");
        // Contract §2: ticket is 64 hex chars. Tolerate whitespace paste.
        var trimmed = ticket.Trim();
        if (trimmed.Length != 64 || !trimmed.All(c => "0123456789abcdefABCDEF".Contains(c)))
            throw new PairingTicketException("VALIDATION_ERROR",
                "Pairing code must be 64 hex characters. Check that you copied the whole code.");
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new PairingTicketException("VALIDATION_ERROR", "Device name is required.");
        if (deviceName.Length > 120)
            throw new PairingTicketException("VALIDATION_ERROR", "Device name must be 120 characters or fewer.");
        if (version is { Length: > 40 })
            throw new PairingTicketException("VALIDATION_ERROR", "Version string must be 40 characters or fewer.");
        if (os is { Length: > 40 })
            throw new PairingTicketException("VALIDATION_ERROR", "OS string must be 40 characters or fewer.");
        if (printerName is { Length: > 200 })
            throw new PairingTicketException("VALIDATION_ERROR", "Printer name must be 200 characters or fewer.");

        var o = _opts.CurrentValue;
        var baseUri = !string.IsNullOrWhiteSpace(o.BaseUrl)
            ? new Uri(o.BaseUrl)
            : throw new InvalidOperationException("Backend BaseUrl is not configured");

        // Set up a minimal shop so ApiUrl() can resolve. The real shopId/name
        // come from the pair response and overwrite these below.
        _shop = new ShopInfo
        {
            Id = "",
            Slug = "",
            Name = "(pairing...)",
            BaseUrl = baseUri,
            ApiBasePath = o.ApiBasePath,
        };

        var url = ApiUrl("companion/pair");
        var body = new PairTicketRequestDto
        {
            Ticket = trimmed,
            DeviceName = deviceName,
            Version = version ?? ThisApp.Version,
            Os = os ?? "win32",
            PrinterName = printerName,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        // NO AddAuth — the ticket IS the authorization for this endpoint.
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", ThisApp.Version));

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var (code, message) = await ReadErrorAsync(resp, ct);
            // Actual SaaS error codes (Phase 3 fix — see docs/PHASE3_CONTRACT_MATRIX.md Mismatch #4):
            // 400 VALIDATION_ERROR (zod fail)
            // 401 UNAUTHORIZED (no ticket / unknown ticket / EXPIRED ticket — server deliberately
            //    uses the same code for unknown+expired to avoid ticket enumeration)
            // 409 PAIRING_TICKET_UNAVAILABLE (status != 'active' but not consumed — e.g. 'revoked')
            // 409 PAIRING_TICKET_CONSUMED (lost the consume race — another request got there first)
            // 404 NOT_FOUND (shop deleted between begin & pair)
            // 429 RATE_LIMITED
            throw new PairingTicketException(
                code ?? "UNKNOWN",
                $"Pairing failed: {message ?? resp.StatusCode.ToString()}");
        }

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<PairTicketResponseDto>>(Json, ct)
            ?? throw new PairingTicketException("UNKNOWN", "Empty pair response.");
        var dto = envelope.Data
            ?? throw new PairingTicketException("UNKNOWN", "Pair response missing data field.");

        // v0.7.2 sync: capture the SERVER-assigned agent id — Socket.IO
        // agent.revoked / device.command.issued events carry it.
        _serverAgentId = dto.AgentId ?? _serverAgentId;

        // Persist the returned device token via the same path as paste-token
        // pairing (DPAPI, current-user scope). This reuses PairWithDeviceTokenAsync
        // so the heartbeat-validation + storage logic is identical.
        _shop = new ShopInfo
        {
            Id = dto.ShopId,
            Slug = "",
            Name = dto.ShopName,
            BaseUrl = baseUri,
            ApiBasePath = o.ApiBasePath,
        };
        await PairWithDeviceTokenAsync(dto.DeviceToken, ct);

        _logger.LogInformation(
            "Paired via ticket: agentId={AgentId} shop={ShopName} protocolVersion={ProtocolVersion}",
            dto.AgentId, dto.ShopName, dto.ProtocolVersion);

        return new PairResult
        {
            AgentId = dto.AgentId,
            DeviceToken = dto.DeviceToken,
            ShopId = dto.ShopId,
            ShopName = dto.ShopName,
            ProtocolVersion = dto.ProtocolVersion,
        };
    }

    /// <summary>
    /// v1 contract §6 — GET /api/v1/companion/commands. Fetch pending remote
    /// commands for this device. The backend returns only commands with
    /// status='requested' AND expiresAt > now; the companion still validates
    /// <see cref="PendingCommand.IsExpired"/> locally before executing.
    /// <para><b>Phase 3 fix:</b> the actual SaaS returns commands with field
    /// <c>commandId</c> (not <c>id</c>), <c>payload</c> as a parsed JSON object,
    /// and an <c>issuedAt</c> field. We re-serialize the payload object back
    /// to a string for the executor's <c>JsonDocument.Parse</c> path.</para>
    /// </summary>
    public async Task<IReadOnlyList<PendingCommand>> FetchPendingCommandsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("companion/commands");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureNotDisabledAsync(resp, ct);   // 403 → DeviceDisabledException
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<PendingCommandsEnvelopeDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty commands response");
        var cmds = envelope.Data?.Commands ?? new List<PendingCommandDto>();
        return cmds.Select(c => new PendingCommand
        {
            Id = c.CommandId,
            CommandType = c.CommandType,
            // The server sends payload as a parsed JSON object (or null/garbage
            // → empty object). Re-serialize to a string for the executor's
            // JsonDocument.Parse path. JsonElement with ValueKind=Undefined (the
            // default when the field is absent) → "{}".
            PayloadJson = c.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined
                || c.Payload.ValueKind == System.Text.Json.JsonValueKind.Null
                ? "{}"
                : c.Payload.GetRawText(),
            IssuedAt = c.IssuedAt,
            ExpiresAt = c.ExpiresAt,
        }).ToList();
    }

    /// <summary>
    /// v1 contract §7 — POST /api/v1/companion/commands. Acknowledge receipt
    /// and/or report execution result. Idempotent on (commandId, status).
    /// <para><b>Phase 3 fix:</b> the actual SaaS zod wants <c>result</c> as an
    /// object (not a JSON string) and returns <c>{commandId, status, updatedAt}</c>
    /// (not <c>{applied}</c>). We parse the executor's result JSON string back
    /// to a JsonElement before sending.</para>
    /// </summary>
    public async Task<CommandReportResult> ReportCommandStatusAsync(
        string commandId, string status, string? resultJson = null, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));
        if (!CommandStatus.All.Contains(status))
            throw new ArgumentException(
                $"status must be one of {string.Join(", ", CommandStatus.All)} (got '{status}')", nameof(status));

        // Parse the executor's result JSON string back to a JsonElement so the
        // SaaS zod (`z.record(z.string(), z.unknown())`) accepts it. A null or
        // malformed string → omit `result` (it's optional in the zod schema).
        System.Text.Json.JsonElement? resultElement = null;
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
                // Clone the element (the doc is disposed) — GetRawText + reparse
                // is the simplest clone that survives the using scope.
                resultElement = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    doc.RootElement.GetRawText());
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse result JSON for command {CommandId}; omitting result field", commandId);
            }
        }

        var url = ApiUrl("companion/commands");
        var body = new ReportCommandRequestDto
        {
            CommandId = commandId,
            Status = status,
            Result = resultElement,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureNotDisabledAsync(resp, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<ReportCommandResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty command-report response");
        var dto = envelope.Data;
        return new CommandReportResult(
            CommandId: dto?.CommandId,
            Status: dto?.Status,
            UpdatedAt: dto?.UpdatedAt);
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
        await EnsureNotDisabledAsync(resp, ct);
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

    /// <summary>
    /// GET /api/v1/companion/shop-snapshot — read-only shop data pulled with
    /// the DEVICE credential. Used when the companion is paired but has no
    /// shopkeeper session: <c>shops/me/*</c> are session endpoints and are
    /// closed to device JWTs, so a paired-only PC reads its own shop's queue
    /// through its device token instead (Universal Print model). The response
    /// reuses the exact wire shapes of <c>shops/me</c> + <c>shops/me/orders</c>
    /// + <c>shops/me/analytics</c>, so the mapping is shared with
    /// <see cref="HttpShopBackend.MapOrder"/> / <see cref="HttpShopBackend.MapAnalytics"/>.
    /// <para>Returns null when the device token is rejected (revoked device) —
    /// the caller then treats the device as unauthenticated.</para>
    /// </summary>
    public async Task<ShopSnapshot?> GetShopSnapshotAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("companion/shop-snapshot");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureNotDisabledAsync(resp, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Shop snapshot: device token rejected (revoked or rotated away)");
            return null;
        }
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<ShopSnapshotDto>>(Json, ct);
        var d = envelope?.Data;
        if (d?.Shop is null)
        {
            _logger.LogWarning("Shop snapshot: no shop in response");
            return null;
        }
        return new ShopSnapshot
        {
            ShopName = d.Shop.Name,
            Analytics = d.Analytics is null ? null : HttpShopBackend.MapAnalytics(d.Analytics),
            Orders = (d.Orders ?? new List<OrderDto>()).Select(HttpShopBackend.MapOrder).ToList()
        };
    }

    private sealed class ShopSnapshotDto
    {
        [JsonPropertyName("shop")]
        public SnapshotShopDto? Shop { get; init; }

        [JsonPropertyName("orders")]
        public List<OrderDto>? Orders { get; init; }

        [JsonPropertyName("analytics")]
        public AnalyticsDto? Analytics { get; init; }
    }

    private sealed class SnapshotShopDto
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("slug")]
        public required string Slug { get; init; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; init; }
    }

    /// <summary>
    /// POST /api/v1/agents/heartbeat — refresh presence.
    /// <para><b>printerName fix (Phase 0 audit):</b> previously this sent the
    /// machine HOSTNAME in the contract's printerName field, so the website's
    /// Devices page showed a computer name where a printer belongs. It now
    /// sends the actually-selected printer (explicit parameter, then the
    /// configured <c>Printer:SelectedPrinter</c>), falling back to the
    /// hostname only when no printer is known.</para>
    /// </summary>
    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default, string? printerName = null)
    {
        EnsureAuthenticated();
        var url = ApiUrl("agents/heartbeat");
        var resolvedPrinter = printerName
            ?? _printerOpts?.CurrentValue.SelectedPrinter
            ?? _device?.Hostname
            ?? Environment.MachineName;
        var body = new
        {
            printerName = resolvedPrinter,
            version = ThisApp.Version,
            os = "win32"
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureNotDisabledAsync(resp, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<AgentHeartbeatResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty heartbeat response");
        var dto = envelope.Data;
        // v0.7.2 sync: heartbeat response carries the server agent id — capture it.
        _serverAgentId = dto?.AgentId ?? _serverAgentId;
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
        await EnsureNotDisabledAsync(resp, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<PendingJobsEnvelopeDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty pending-jobs response");
        // v0.7.2 sync: capture the server agent id from the pending response.
        _serverAgentId = envelope.Data?.AgentId ?? _serverAgentId;
        // v0.7.2 sync: the real route also returns failedJobs (jobs the backend
        // could not prepare: DOCUMENT_MISSING, JOB_INVALID, STORAGE_UNAVAILABLE).
        // They are dead server-side — log them, never enqueue them.
        if (envelope.Data?.FailedJobs is { Count: > 0 } failed)
            _logger.LogWarning(
                "Backend reported {FailedJobCount} failed job(s): {FailedJobSummary}",
                failed.Count,
                string.Join("; ", failed.Take(5).Select(f => $"{f.JobId}={f.Code ?? "UNKNOWN"}")));
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
            // v0.7.2 sync: the SaaS zod caps lastError at 500 and printerName at
            // 200 — truncate client-side instead of failing the whole report
            // with 400 VALIDATION_ERROR.
            PrinterName = TruncateForWire(printerName, 200),
            LastError = TruncateForWire(errorMessage, 500),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // JsonWrite (WhenWritingNull): a literal "lastError":null body is
            // rejected by the SaaS zod (.optional(), not .nullable()) with 400.
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonWrite),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureNotDisabledAsync(resp, ct);
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

    /// <summary>
    /// Rotate the device credential — POST /api/v1/companion/token/rotate.
    /// The backend mints a new 256-bit token, stores only its HMAC verifier,
    /// and keeps the previous hash for a 10-minute overlap window.
    /// <para><b>Persist-before-transition:</b> the new token is DPAPI-encrypted
    /// and atomically written BEFORE the in-memory credential is switched. If
    /// the persist fails, the old (still-valid-in-window) credential is kept
    /// and the rotation can be retried — a failed rotation must not lock out a
    /// healthy device.</para>
    /// <para>The caller decides WHEN to rotate (e.g. credential older than a
    /// policy threshold); this method never rotates automatically on a timer.</para>
    /// </summary>
    public async Task<TokenRotationResult> RotateDeviceTokenAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = ApiUrl("companion/token/rotate");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { })
        };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureNotDisabledAsync(resp, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Device token rejected by backend — rotation requires a currently-valid token");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<TokenRotationResponseDto>>(Json, ct)
            ?? throw new InvalidOperationException("Empty token-rotation response");
        var dto = envelope.Data
            ?? throw new InvalidOperationException("Token-rotation response missing data field");
        if (string.IsNullOrWhiteSpace(dto.DeviceToken))
            throw new InvalidOperationException("Token-rotation response returned an empty device token");

        // 1. Persist the NEW credential FIRST (DPAPI, atomic .tmp/.bak write).
        //    Only after the write verifies do we switch the in-memory token.
        await PersistDeviceTokenAsync(dto.DeviceToken, ct);

        var oldToken = _token;
        _token = new DeviceToken
        {
            ShopId = oldToken?.ShopId ?? "",
            AgentId = dto.AgentId ?? oldToken?.AgentId ?? "",
            Token = dto.DeviceToken,
            IssuedAt = DateTimeOffset.UtcNow,
        };

        _logger.LogInformation(
            "Device token rotated (agentId={AgentId}, old token accepted until {OverlapExpiresAt})",
            dto.AgentId, dto.OverlapExpiresAt?.ToString("O") ?? "(unspecified)");

        return new TokenRotationResult(
            DeviceToken: dto.DeviceToken,
            RotatedAt: dto.RotatedAt ?? DateTimeOffset.UtcNow,
            OverlapExpiresAt: dto.OverlapExpiresAt,
            Warning: dto.Warning);
    }

    /// <summary>
    /// DPAPI-encrypt + atomically persist a device token to the credential
    /// file: write <c>.tmp</c> in the same directory, back up the existing
    /// file to <c>.bak</c>, atomic <c>File.Move</c>, then a decrypt
    /// round-trip verification; restore <c>.bak</c> on any failure.
    /// Mirrors <c>DeviceTokenStore</c>'s write-safety pattern for the raw
    /// token file used by <see cref="RestoreAsync"/>.
    /// </summary>
    public async Task PersistDeviceTokenAsync(string deviceToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new InvalidOperationException("Refusing to persist an empty device token");

        var credPath = ResolveDeviceTokenPath();
        var dir = Path.GetDirectoryName(credPath)!;
        Directory.CreateDirectory(dir);
        var bakPath = credPath + ".bak";

        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(deviceToken);
        byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(
            plaintext, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);

        // v0.7.17: the CI test matrix runs two testhost processes in
        // parallel on one runner (net10.0 + net10.0-windows legs) and both
        // legs exercise token persistence against the SAME %APPDATA% file
        // set. The shared ".tmp" staging name collided (IOException from
        // WriteAllBytesAsync on the second writer). Retry with a
        // process-unique staging name — each writer stages its own file,
        // and the final atomic Move (overwrite: true) makes the last
        // writer win cleanly, which is the same outcome the single-process
        // app always had.
        var tmpPath = $"{credPath}.{Environment.ProcessId}.tmp";
        var wroteTmp = false;
        for (var attempt = 0; attempt < 5 && !wroteTmp; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(tmpPath, encrypted, ct);
                wroteTmp = true;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50, ct);
            }
        }
        if (!wroteTmp)
            await File.WriteAllBytesAsync(tmpPath, encrypted, ct); // final attempt surfaces the real error

        // Round-trip verify the .tmp file BEFORE replacing the live credential.
        var verify = System.Security.Cryptography.ProtectedData.Unprotect(
            await File.ReadAllBytesAsync(tmpPath, ct), null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        if (System.Text.Encoding.UTF8.GetString(verify) != deviceToken)
        {
            try { File.Delete(tmpPath); } catch { }
            throw new InvalidOperationException("DPAPI round-trip verification failed — credential not persisted");
        }

        if (File.Exists(credPath))
        {
            try { File.Copy(credPath, bakPath, overwrite: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not back up the previous credential file (continuing)"); }
        }

        try
        {
            File.Move(tmpPath, credPath, overwrite: true);
        }
        catch
        {
            // Restore path: the old credential file was NOT touched if Move
            // failed before replacing; clean the tmp and rethrow so the
            // caller keeps the old in-memory token.
            try { File.Delete(tmpPath); } catch { }
            throw;
        }

        try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { }
        _logger.LogInformation("Device token persisted to {Path}", credPath);
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
        await EnsureNotDisabledAsync(resp, ct);
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
        if (_token is null) TryRestoreTokenFromDiskSync();
        if (_token is null)
            throw new InvalidOperationException("Not paired with a shop — paste the device token from the web dashboard first");
    }

    /// <summary>
    /// Phase 1 (Defect C fix): lazy, synchronous DPAPI restore when this
    /// instance has never been authenticated. The typed client is registered
    /// TRANSIENT, so every DI resolution used to yield a fresh instance with
    /// <c>_token == null</c> — EnsureAuthenticated threw even though the
    /// device WAS paired (heartbeat timer, rotation policy, revocation all
    /// resolved their own instance and silently failed). This loads the
    /// DPAPI-protected credential from disk WITHOUT network validation (the
    /// first real call validates it); once, per instance.
    /// </summary>
    private bool _restoreAttempted;
    private void TryRestoreTokenFromDiskSync()
    {
        if (_restoreAttempted) return;
        _restoreAttempted = true;
        try
        {
            var credPath = ResolveDeviceTokenPath();
            if (!File.Exists(credPath)) return;
            byte[] encrypted = File.ReadAllBytes(credPath);
            byte[] plaintext = System.Security.Cryptography.ProtectedData.Unprotect(
                encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            string deviceToken = System.Text.Encoding.UTF8.GetString(plaintext);
            if (string.IsNullOrWhiteSpace(deviceToken)) return;
            _token = new DeviceToken
            {
                ShopId = "",
                AgentId = "",
                Token = deviceToken,
                IssuedAt = File.GetLastWriteTimeUtc(credPath),
            };
            // Phase 5 fix: the restore must also seed _shop, exactly like the
            // paste-token flow does before its validation heartbeat. Without
            // it, ApiUrl() dereferences a null _shop (NullReferenceException)
            // on the very first authenticated call from any instance that did
            // not pair in-process — the captured transient backends in the
            // job processor, heartbeat timer, and rotation policy. BaseUrl and
            // ApiBasePath are deployment configuration, not per-pairing state.
            var o = _opts.CurrentValue;
            _shop = new ShopInfo
            {
                Id = "",
                Slug = "",
                Name = "(restored — shop name pending first sync)",
                BaseUrl = new Uri(o.BaseUrl),
                ApiBasePath = o.ApiBasePath,
            };
            _logger.LogInformation("Device credential lazily restored from disk (unvalidated — first call will validate)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lazy credential restore failed — treating device as unpaired");
        }
    }

    /// <summary>
    /// Read the backend's standard error envelope <c>{success:false, error:{code, message}}</c>
    /// from a non-2xx response. Returns (null, null) if the body is empty or not JSON.
    /// Never throws — used by callers to produce contract-specific exceptions.
    /// </summary>
    private async Task<(string? Code, string? Message)> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope<object>>(Json, ct);
            return (envelope?.Error?.Code, envelope?.Error?.Message);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// v1 contract §3 + §14 — distinguish 403 FORBIDDEN / DEVICE_DISABLED from
    /// 401 UNAUTHORIZED. A disabled device still exists (token hash valid) but
    /// the shop admin suspended it; the companion must NOT clear the credential
    /// (the admin may re-enable it). Throws <see cref="DeviceDisabledException"/>
    /// for 403; leaves 401 handling to the caller.
    /// </summary>
    private async Task EnsureNotDisabledAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.Forbidden) return;
        var (code, message) = await ReadErrorAsync(resp, ct);
        throw new DeviceDisabledException(
            $"This device is disabled by the shop administrator{(code is null ? "" : $" ({code})")}. " +
            $"Contact your shop admin to re-enable it. {(message ?? "").Trim()}".Trim());
    }

    /// <summary>
    /// Add <c>Authorization: Bearer &lt;deviceToken&gt;</c> to the request.
    /// This is the auth model the production backend expects per the
    /// integration guide (section 2.1). The previous v0.4.6 code used
    /// custom headers (X-PrintShop-Device-Token) — the production backend
    /// does NOT recognize those.
    /// <para>Also sends <c>X-PrintShop-Companion-Version</c> so the backend's
    /// version telemetry / future 426 gate works (Phase 0 audit: the header
    /// was declared in the contract but never actually sent).</para>
    /// </summary>
    private void AddAuth(HttpRequestMessage req)
    {
        if (_token is null) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", ThisApp.Version));
        if (!string.IsNullOrEmpty(CompanionProtocol.AppVersionHeader))
        {
            try { req.Headers.Add(CompanionProtocol.AppVersionHeader, ThisApp.Version); }
            catch { /* header add is best-effort */ }
        }
    }

    private Uri ApiUrl(string relative) => new(_shop!.BaseUrl, $"{_shop.ApiBasePath.TrimStart('/')}/{relative.TrimStart('/')}");

    /// <summary>
    /// Resolve the path to the DPAPI-encrypted device token file. Defaults
    /// to <c>%AppData%\PrintShop\Companion\device.dat</c>. Override via
    /// <c>Storage:DeviceTokenPath</c> in appsettings.json.
    /// </summary>
    private string ResolveDeviceTokenPath()
    {
        // v0.5: use LocalStorageLayout.DeviceTokenPath if the layout is
        // available (the new %LocalAppData%\PrintShop\Companion\Security\
        // device.dat path). Fall back to the old %AppData% path for
        // backward compat with tests that don't inject the layout.
        if (_layout is not null)
            return _layout.DeviceTokenPath;

        var o = _opts.CurrentValue;
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

    private PrintJobRecord MapToRecord(PrintJobDto dto) => new()
    {
        JobId = dto.JobId,
        // D-12: stamp the paired shop id (audit: ShopId was always "" here).
        ShopId = _shop?.Id ?? "",
        DeviceId = CurrentDevice?.DeviceId ?? "",
        Title = dto.Filename,
        PrinterName = null,
        Copies = dto.Copies,
        PageRange = dto.PageRange,
        PaperSize = dto.PaperSize,
        ColorMode = dto.ColorMode,
        Color = dto.ColorMode == "color",
        Duplex = dto.Duplex,
        PdfUrl = Uri.TryCreate(dto.DownloadUrl, UriKind.Absolute, out var u) ? u : null,
        // v0.7.2 sync: the pending route sends pdfSha256 (nullable) — carry it so
        // the download integrity gate can verify without a second fetch.
        PdfSha256 = dto.PdfSha256,
        Status = JobStatus.Pending,
        AttemptCount = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Truncate a string to the SaaS zod cap (the server 400s over-length strings).</summary>
    private static string? TruncateForWire(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

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

        // v0.7.16: the CI test matrix runs two testhost processes in
        // parallel on ONE machine (net10.0 + net10.0-windows legs), and
        // both legs static-initialize ThisApp. The old read-then-write
        // raced on the shared file: one process held device-id.txt while
        // the other's File.ReadAllText threw IOException, poisoning the
        // type initializer (TypeInitializationException) for every later
        // test in that leg. Read with FileShare so a concurrent writer
        // doesn't fault us, retry briefly, and never let this path throw:
        // a minted-but-unshared id is fine for a test process; the app
        // itself is single-process and keeps the durable behavior.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (TryReadDeviceId(path, out var existing)) return existing;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fresh = Guid.NewGuid();
                File.WriteAllText(path, fresh.ToString());
                return fresh;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return Guid.NewGuid();
            }
        }
    }

    private static bool TryReadDeviceId(string path, out Guid id)
    {
        id = default;
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Guid.TryParse(reader.ReadToEnd(), out id);
        }
        catch (IOException)
        {
            // Locked even with FileShare (writer mid-replace) — treat as
            // absent so the caller retries / re-mints.
            return false;
        }
    }
}
