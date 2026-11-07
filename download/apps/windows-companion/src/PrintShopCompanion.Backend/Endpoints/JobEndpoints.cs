namespace PrintShopCompanion.Backend.Endpoints;

using Microsoft.AspNetCore.Http;
using PrintShopCompanion.Backend.Data;
using PrintShopCompanion.Shared.Constants;
using PrintShopCompanion.Shared.Dto;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        // Get pending jobs for this device (HTTP poll fallback / mock realtime).
        app.MapGet("/api/companion/v1/jobs/pending", (HttpRequest req, MockStore store) =>
        {
            if (!DeviceEndpoints.IsAuthorized(req, store, out var device)) return Results.Unauthorized();
            var pending = store.Jobs.Values
                .Where(j => !j.Acked && j.Dto.DeviceId == device.DeviceId)
                .Select(j => j.Dto)
                .ToList();
            return Results.Ok(pending);
        });

        // Acknowledge receipt of a job.
        app.MapPost("/api/companion/v1/jobs/{jobId}/ack", (string jobId, AckJobRequestDto body, MockStore store, HttpRequest req) =>
        {
            if (!DeviceEndpoints.IsAuthorized(req, store, out _)) return Results.Unauthorized();
            if (store.Jobs.TryGetValue(jobId, out var j))
                store.Jobs[jobId] = j with { Acked = true };
            return Results.Ok();
        });

        // Report status — printed / failed / both nulls = cancelled.
        app.MapPost("/api/companion/v1/jobs/{jobId}/status", (string jobId, JobStatusReportDto body, MockStore store, HttpRequest req) =>
        {
            if (!DeviceEndpoints.IsAuthorized(req, store, out _)) return Results.Unauthorized();
            if (store.Jobs.TryGetValue(jobId, out var j))
            {
                store.Jobs[jobId] = j with
                {
                    PrintedAt = body.PrintedAt,
                    FailedAt = body.FailedAt,
                    Error = body.ErrorMessage,
                    SpoolerJobId = body.SpoolerJobId
                };
            }
            return Results.Ok();
        });

        // Download the PDF for a job. Returns the bundled test-print PDF or a synthetic one.
        app.MapGet("/api/companion/v1/jobs/{jobId}/pdf", (string jobId, MockStore store, HttpRequest req) =>
        {
            if (!DeviceEndpoints.IsAuthorized(req, store, out _)) return Results.Unauthorized();
            if (!store.Jobs.TryGetValue(jobId, out var j)) return Results.NotFound();
            return Results.File(store.TestPdf, "application/pdf", fileDownloadName: $"{jobId}.pdf");
        });

        // Helper: enqueue a test job for a paired device. Used by the UI's "Test print" flow.
        app.MapPost("/api/companion/v1/jobs/test", (HttpRequest req, MockStore store) =>
        {
            if (!DeviceEndpoints.IsAuthorized(req, store, out var device)) return Results.Unauthorized();
            var dto = new LegacyPrintJobDto
            {
                JobId = Guid.NewGuid().ToString("N"),
                ShopId = device.ShopId,
                DeviceId = device.DeviceId,
                Title = "Test print from PrintShop Companion",
                Copies = 1,
                PdfUrl = new Uri($"https://localhost:5241/api/companion/v1/jobs/{Guid.NewGuid():N}/pdf"),
                PdfSha256 = null,
                CreatedAt = DateTimeOffset.UtcNow
            };
            store.Jobs[dto.JobId] = new JobRecord(dto, false, null, null, null, null);
            return Results.Ok(dto);
        });
    }
}
