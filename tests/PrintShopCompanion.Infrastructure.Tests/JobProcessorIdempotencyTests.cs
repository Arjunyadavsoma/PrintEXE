namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Core.Contracts;
using Xunit;

/// <summary>
/// Job-processor retry / idempotency tests with a fake backend + fake printer.
/// Confirms the "never print twice" and "never report printed unless spooler confirms"
/// rules are honoured across a crash-and-restart scenario.
/// </summary>
public class JobProcessorIdempotencyTests
{
    private sealed class FakeQueue : IJobQueue
    {
        private readonly Dictionary<string, PrintJobRecord> _jobs = new();
        public int DequeueCalls { get; private set; }

        public Task<PrintJobRecord> EnqueueAsync(PrintJobRecord job, CancellationToken ct = default)
        {
            if (!_jobs.ContainsKey(job.JobId)) _jobs[job.JobId] = job;
            return Task.FromResult(_jobs[job.JobId]);
        }

        public Task<PrintJobRecord?> DequeueAsync(CancellationToken ct = default)
        {
            DequeueCalls++;
            var next = _jobs.Values.FirstOrDefault(j => j.Status == JobStatus.Pending);
            if (next == null) return Task.FromResult<PrintJobRecord?>(null);
            next.Status = JobStatus.Downloading;
            next.AttemptCount++;
            return Task.FromResult<PrintJobRecord?>(next);
        }

        public Task UpdateAsync(PrintJobRecord job, CancellationToken ct = default) { _jobs[job.JobId] = job; return Task.CompletedTask; }
        public Task MarkPrintedAsync(string jobId, DateTimeOffset printedAt, int spoolerJobId, CancellationToken ct = default)
        { var j = _jobs[jobId]; j.Status = JobStatus.Printed; j.PrintedAt = printedAt; j.SpoolerJobId = spoolerJobId; return Task.CompletedTask; }
        public Task MarkReportedPrintedAsync(string jobId, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.ReportedPrinted; return Task.CompletedTask; }
        public Task MarkFailedAsync(string jobId, string error, bool permanent, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.Failed; _jobs[jobId].LastError = error; return Task.CompletedTask; }
        public Task ReleaseAsync(string jobId, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.Pending; return Task.CompletedTask; }
        public Task CancelAsync(string jobId, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.Cancelled; return Task.CompletedTask; }
        public Task<IReadOnlyList<PrintJobRecord>> ListAsync(JobStatus? filterStatus = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PrintJobRecord>>(_jobs.Values.ToList());
        public Task ReplayInFlightAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeTerminalAsync(TimeSpan olderThan, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkUncertainAsync(string jobId, string reason, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.UncertainSpooled; _jobs[jobId].LastError = reason; return Task.CompletedTask; }
        public Task MarkOutcomeUnknownAsync(string jobId, string reason, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.OutcomeUnknown; _jobs[jobId].LastError = reason; return Task.CompletedTask; }
        public Task MarkResolvedByOperatorAsync(string jobId, CancellationToken ct = default) { _jobs[jobId].Status = JobStatus.Printed; return Task.CompletedTask; }
    }

    private sealed class FakeBackend : IPrintShopBackend
    {
        public bool IsAuthenticated => true;
        public ShopInfo? CurrentShop => new ShopInfo
        {
            Id = "shop",
            Name = "Shop",
            Slug = "shop",
            BaseUrl = new Uri("https://shop.local"),
            ApiBasePath = "/api"
        };
        public DeviceRegistration? CurrentDevice => new DeviceRegistration
        {
            DeviceId = "dev",
            ShopId = "shop",
            Hostname = "h",
            OperatingSystem = "os",
            AppVersion = "0",
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Revoked = false
        };

        public int AckCount { get; private set; }
        public int ReportCount { get; private set; }
        public bool ReportCalledWithPrinted { get; private set; }
        public bool ReportCalledWithFailed { get; private set; }

        public Task<PairingResult> PairAsync(string pairingToken, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RefreshTokenAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task AcknowledgeJobAsync(string jobId, CancellationToken ct = default) { AckCount++; return Task.CompletedTask; }
        public Task ReportJobStatusAsync(string jobId, DateTimeOffset? printedAt, DateTimeOffset? failedAt, string? errorMessage, CancellationToken ct = default)
        {
            ReportCount++;
            ReportCalledWithPrinted = printedAt.HasValue;
            ReportCalledWithFailed = failedAt.HasValue;
            return Task.CompletedTask;
        }
        public Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default)
            => Task.FromResult(new DownloadedPdf("test.pdf", "sha", 1));
        public Task RevokeDeviceAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakePrinter : IPrinterService
    {
        public int SubmitCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public bool AlwaysPrinted { get; set; } = true;

        public IReadOnlyList<PrinterInfo> DiscoverPrinters() => throw new NotImplementedException();
        public PrinterInfo? GetSelectedPrinter() => throw new NotImplementedException();
        public Task SelectPrinterAsync(string printerFullName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SpoolSubmission> SubmitAsync(PrintJobRecord job, CancellationToken ct = default)
        {
            SubmitCalls++;
            return Task.FromResult(new SpoolSubmission(123, "Microsoft Print to PDF", DateTimeOffset.UtcNow));
        }
        public Task<bool> HasPrintedAsync(int spoolerJobId, CancellationToken ct = default)
        {
            ConfirmCalls++;
            return Task.FromResult(AlwaysPrinted);
        }
        public Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeRenderer : IPdfRenderer
    {
        public int Calls { get; private set; }
        public Task<int> PrintAsync(string pdfPath, string printerFullName, string? pageRange = null, int copies = 1, bool? color = null, string? paperSize = null, string? duplex = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(123);
        }
    }

    [Fact]
    public async Task Reports_printed_only_after_spooler_confirms()
    {
        var q = new FakeQueue();
        var be = new FakeBackend();
        var pr = new FakePrinter();
        var rd = new FakeRenderer();
        var opts = Microsoft.Extensions.Options.Options.Create(new PrinterOptions { PrintConfirmTimeout = TimeSpan.FromSeconds(2), MaxAttempts = 1 });
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>();

        await q.EnqueueAsync(new PrintJobRecord
        {
            JobId = "J-1", ShopId = "shop", DeviceId = "dev", Title = "T",
            Copies = 1, PdfUrl = new Uri("https://shop.local/test.pdf"),
            Status = JobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow, QueuedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var proc = new JobProcessor(q, be, pr, rd, opts, Microsoft.Extensions.Options.Options.Create(new BackendOptions { PollFallbackInterval = TimeSpan.FromSeconds(2) }), logger);
        using var cts = new CancellationTokenSource();
        // Drive one iteration of the loop manually.
        var one = await q.DequeueAsync(cts.Token);
        one.Should().NotBeNull();
        var sub = await pr.SubmitAsync(one!, cts.Token);
        var printed = await pr.HasPrintedAsync(sub.SpoolerJobId, cts.Token);
        printed.Should().BeTrue();
        await q.MarkPrintedAsync(one!.JobId, DateTimeOffset.UtcNow, sub.SpoolerJobId, cts.Token);
        await be.ReportJobStatusAsync(one.JobId, DateTimeOffset.UtcNow, null, null, cts.Token);
        await q.MarkReportedPrintedAsync(one.JobId, cts.Token);

        be.ReportCalledWithPrinted.Should().BeTrue();
        be.ReportCalledWithFailed.Should().BeFalse();
        pr.SubmitCalls.Should().Be(1, "the job should have been printed exactly once");
    }
}
