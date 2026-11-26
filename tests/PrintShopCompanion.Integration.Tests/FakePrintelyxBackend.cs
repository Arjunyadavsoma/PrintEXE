namespace PrintShopCompanion.Integration.Tests;

using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.Shared.Dto;

/// <summary>
/// A REAL HTTP server implementing the production print-agent contract
/// (decision D-15): the exact endpoints, envelope shape, auth scheme and
/// state machine the live SaaS exposes, verified against the production
/// client (docs/AUDIT.md §4 + §15). Replaces the legacy in-repo mock
/// (PrintShopCompanion.Backend), which speaks a different, incompatible
/// contract and cannot interoperate with the production client.
///
/// <para>Implemented contract (all envelope-wrapped <c>{success, data, error}</c>):</para>
/// <list type="bullet">
///   <item>POST /api/v1/agents/heartbeat — validates the Bearer device token.</item>
///   <item>POST /api/v1/agents/token — device-token → short-lived JWT exchange.</item>
///   <item>GET /api/v1/agents/jobs/pending — queued jobs for this device.</item>
///   <item>POST /api/v1/agents/jobs/{id}/status — atomic claim (queued→printing→completed/failed).</item>
///   <item>POST /api/v1/agents/jobs/{id}/received — durable receipt + cloud cleanup scheduling.</item>
///   <item>GET /files/{name} — the PDF bytes (the real backend serves presigned S3 URLs).</item>
/// </list>
/// </summary>
public sealed class FakePrintelyxBackend : IDisposable
{
    public const string ValidDeviceToken = "dev-token-unit-test-1";

    private sealed record JobState(string JobId, string Filename, byte[] PdfBytes, string Status, string? ClaimedBy);

    private readonly Dictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly WebApplication _app;
    private readonly List<(string JobId, string Status, string? Printer, string? Error)> _statusLog = new();

    public string BaseUrl { get; private set; } = "";
    public IReadOnlyList<(string JobId, string Status, string? Printer, string? Error)> StatusLog => _statusLog;

    public static byte[] TestPdfBytes => PrintShopCompanion.Infrastructure.Pdf.TestPagePdf.BuildBytes(
        "PrintEXE integration test", "Contract test page.");

    public FakePrintelyxBackend()
    {
        var pdf = TestPdfBytes;
        _jobs["job-e2e-1"] = new JobState("job-e2e-1", "customer-document.pdf", pdf, "queued", null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // OS-assigned port
        _app = builder.Build();

        bool AuthOk(HttpRequest req) =>
            req.Headers.Authorization.ToString() == $"Bearer {ValidDeviceToken}";

        static IResult Envelope<T>(T data) => Results.Json(new Envelope<T> { Success = true, Data = data });
        static IResult Err(int code, string err, string msg) =>
            Results.Json(new Envelope<object> { Success = false, Error = new ApiError { Code = err, Message = msg } }, statusCode: code);

        _app.MapPost("/api/v1/agents/heartbeat", (HttpRequest req) =>
        {
            if (!AuthOk(req)) return Err(401, "UNAUTHORIZED", "Device token rejected");
            return Envelope(new AgentHeartbeatResponseDto { AgentId = "agent-1", Online = true, Ts = DateTimeOffset.UtcNow });
        });

        _app.MapPost("/api/v1/agents/token", (HttpRequest req) =>
        {
            if (!AuthOk(req)) return Err(401, "UNAUTHORIZED", "Device token rejected");
            return Envelope(new AgentTokenResponseDto
            {
                AccessToken = "jwt-for-socketio-unit-test",
                ExpiresIn = 900,
                ShopId = "shop-1",
            });
        });

        _app.MapGet("/api/v1/agents/jobs/pending", (HttpRequest req) =>
        {
            if (!AuthOk(req)) return Err(401, "UNAUTHORIZED", "Device token rejected");
            lock (_lock)
            {
                var pending = _jobs.Values
                    .Where(j => j.Status == "queued")
                    .Select(j => new PrintJobDto
                    {
                        JobId = j.JobId,
                        FileId = "file-" + j.JobId,
                        OrderId = "order-" + j.JobId,
                        Filename = j.Filename,
                        DownloadUrl = $"{BaseUrl}/files/{j.JobId}.pdf",
                        Copies = 2,
                        ColorMode = "bw",
                        Duplex = false,
                        PaperSize = "a4",
                    })
                    .ToList();
                return Envelope(new PendingJobsEnvelopeDto { Jobs = pending, AgentId = "agent-1" });
            }
        });

        _app.MapPost("/api/v1/agents/jobs/{jobId}/status", (HttpRequest req, string jobId, AgentJobStatusRequestDto body) =>
        {
            if (!AuthOk(req)) return Err(401, "UNAUTHORIZED", "Device token rejected");
            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId, out var job))
                    return Err(404, "NOT_FOUND", "Job not found in this shop");
                var applied = false;
                var skipped = false;
                var reason = "";
                if (body.Status == "printing" && job.Status == "queued")
                {
                    _jobs[jobId] = job with { Status = "printing", ClaimedBy = body.PrinterName };
                    applied = true;
                }
                else if (body.Status == "printing" && job.Status == "printing")
                {
                    // Crash-leftover semantics: still ours → skipped with the enriched reason.
                    skipped = true;
                    reason = "already claimed by this device in a previous session";
                }
                else if (body.Status == "completed" && job.Status == "printing")
                {
                    _jobs[jobId] = job with { Status = "completed" };
                    applied = true;
                }
                else if (body.Status == "failed" && job.Status is "printing" or "queued")
                {
                    _jobs[jobId] = job with { Status = "failed" };
                    applied = true;
                }
                else
                {
                    skipped = true;
                    reason = $"job in state '{job.Status}'";
                }
                _statusLog.Add((jobId, body.Status, body.PrinterName, body.LastError));
                return Envelope(new AgentJobStatusResponseDto
                {
                    JobId = jobId, Status = body.Status, Applied = applied, Skipped = skipped, Reason = reason,
                });
            }
        });

        _app.MapPost("/api/v1/agents/jobs/{jobId}/received", (HttpRequest req, string jobId, JobReceivedRequestDto body) =>
        {
            if (!AuthOk(req)) return Err(401, "UNAUTHORIZED", "Device token rejected");
            if (!_jobs.ContainsKey(jobId))
                return Err(404, "NOT_FOUND", "Job not found in this shop");
            var cleanup = DateTimeOffset.UtcNow.AddMinutes(15);
            return Envelope(new JobReceivedResponseDto
            {
                JobId = jobId,
                Received = true,
                CleanupScheduled = true,
                CleanupScheduledAt = cleanup,
                Message = "Receipt recorded",
            });
        });

        _app.MapGet("/files/{name}", (string name) =>
        {
            var jobId = name.Replace(".pdf", "", StringComparison.OrdinalIgnoreCase);
            lock (_lock)
            {
                if (_jobs.TryGetValue(jobId, out var job))
                    return Results.Bytes(job.PdfBytes, "application/pdf");
            }
            return Results.NotFound();
        });

        _app.StartAsync().GetAwaiter().GetResult();
        BaseUrl = _app.Urls.First(u => u.StartsWith("http://"));
    }

    /// <summary>Marks the seeded job claimed (as if by a previous session) — for crash-recovery tests.</summary>
    public void SeedClaimed(string jobId) =>
        _jobs[jobId] = _jobs[jobId] with { Status = "printing", ClaimedBy = "previous-session" };

    public string Sha256HexOfTestPdf() => Convert.ToHexString(SHA256.HashData(TestPdfBytes)).ToLowerInvariant();

    public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();

    // ── envelope shapes (match the real SaaS ApiEnvelope) ──
    internal sealed class Envelope<T>
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("data")] public T? Data { get; init; }
        [JsonPropertyName("error")] public ApiError? Error { get; init; }
    }

    internal sealed class ApiError
    {
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
    }
}
