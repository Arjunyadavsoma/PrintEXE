namespace PrintShopCompanion.Infrastructure.Tests;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Commands;
using Xunit;

/// <summary>
/// v1 contract §4 + §8 — tests for the remote-command executor. Covers:
/// - Allowlist validation (reject unsupported command types).
/// - Expiry validation (reject expired commands).
/// - Per-type dispatch: refresh_printers, pause, resume, cancel_job, retry_job,
///   test_print, diagnostics.
/// - Payload parsing for cancel_job/retry_job (jobId from JSON; reject malformed).
/// - Idempotent test_print job id (test-print-&lt;commandId&gt;).
/// - Structured failure results (never throws; returns failed + error message).
/// These are contract-independent unit tests — Linux-buildable test bodies
/// (the project TFM is net10.0-windows but the test bodies use only fakes).
/// </summary>
public class CommandExecutorTests
{
    private static PendingCommand Cmd(string id, string type, string payload = "{}", DateTimeOffset? expiresAt = null) =>
        new()
        {
            Id = id,
            CommandType = type,
            PayloadJson = payload,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5),
        };

    private static CommandExecutor Make(
        out FakeQueue queue,
        out FakePrinter printers,
        out FakeBackend backend,
        out FakeQueueController queueController)
    {
        queue = new FakeQueue();
        printers = new FakePrinter();
        backend = new FakeBackend();
        queueController = new FakeQueueController();
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandExecutor>();
        var printerOpts = new FakeOptionsMonitor(new PrinterOptions());
        return new CommandExecutor(printers, queue, backend, queueController, printerOpts, logger);
    }

    /// <summary>Phase 3 — fake IQueueController for pause/resume command tests.</summary>
    private sealed class FakeQueueController : IQueueController
    {
        public bool IsPaused { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public Task PauseAsync(CancellationToken ct = default) { PauseCalls++; IsPaused = true; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken ct = default) { ResumeCalls++; IsPaused = false; return Task.CompletedTask; }
    }

    // ─── allowlist + expiry ───────────────────────────────────────────────

    [Fact]
    public async Task Rejects_unsupported_command_type()
    {
        var exec = Make(out _, out _, out _, out _);
        var result = await exec.ExecuteAsync(Cmd("c1", "run_shell"), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Contain("Unsupported command type 'run_shell'");
    }

    [Fact]
    public async Task Rejects_expired_command()
    {
        var exec = Make(out _, out _, out _, out _);
        var expired = Cmd("c2", CommandType.RefreshPrinters,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var result = await exec.ExecuteAsync(expired, CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Contain("Command expired");
    }

    // ─── refresh_printers ─────────────────────────────────────────────────

    [Fact]
    public async Task RefreshPrinters_returns_discovered_printers_and_pushes_heartbeat()
    {
        var exec = Make(out _, out var printers, out var backend, out _);
        var result = await exec.ExecuteAsync(Cmd("c3", CommandType.RefreshPrinters), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Succeeded);
        backend.Heartbeats.Should().Be(1, "refresh_printers pushes printerName via heartbeat");
        printers.DiscoverCalls.Should().Be(1);
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(printers.Printers.Count);
    }

    // ─── pause / resume (wiring pending — recorded as succeeded with a note) ─

    [Theory]
    [InlineData(CommandType.Pause)]
    [InlineData(CommandType.Resume)]
    public async Task Pause_and_Resume_drive_the_queue_controller_Phase3(string type)
    {
        // Phase 3 (brief §7): pause/resume now ACTUALLY call the JobProcessor
        // (via IQueueController) instead of returning a "wiring pending" stub.
        var exec = Make(out _, out _, out _, out var queueController);
        var result = await exec.ExecuteAsync(Cmd("c4", type), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Succeeded);
        using var doc = JsonDocument.Parse(result.ResultJson!);
        if (type == CommandType.Pause)
        {
            doc.RootElement.GetProperty("paused").GetBoolean().Should().BeTrue();
            queueController!.PauseCalls.Should().Be(1);
            queueController.IsPaused.Should().BeTrue();
        }
        else
        {
            doc.RootElement.GetProperty("resumed").GetBoolean().Should().BeTrue();
            queueController!.ResumeCalls.Should().Be(1);
            queueController.IsPaused.Should().BeFalse();
        }
    }

    // ─── cancel_job / retry_job ───────────────────────────────────────────

    [Fact]
    public async Task CancelJob_cancels_the_named_job()
    {
        var exec = Make(out var queue, out _, out _, out _);
        queue.Jobs["job-xyz"] = MakeJob("job-xyz", JobStatus.Pending);
        var result = await exec.ExecuteAsync(
            Cmd("c5", CommandType.CancelJob, """{"jobId":"job-xyz"}"""), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Succeeded);
        queue.Jobs["job-xyz"].Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task CancelJob_rejects_payload_missing_jobId()
    {
        var exec = Make(out _, out _, out _, out _);
        var result = await exec.ExecuteAsync(
            Cmd("c6", CommandType.CancelJob, "{}"), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Contain("missing 'jobId'");
    }

    [Fact]
    public async Task CancelJob_rejects_malformed_json_payload()
    {
        var exec = Make(out _, out _, out _, out _);
        var result = await exec.ExecuteAsync(
            Cmd("c7", CommandType.CancelJob, "not-json"), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Contain("not valid JSON");
    }

    [Fact]
    public async Task RetryJob_requeues_failed_job_to_Pending()
    {
        var exec = Make(out var queue, out _, out _, out _);
        queue.Jobs["job-f"] = MakeJob("job-f", JobStatus.Failed);
        var result = await exec.ExecuteAsync(
            Cmd("c8", CommandType.RetryJob, """{"jobId":"job-f"}"""), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Succeeded);
        queue.Jobs["job-f"].Status.Should().Be(JobStatus.Pending,
            "retry_job resets a Failed job to Pending so the JobProcessor can re-attempt");
        queue.Jobs["job-f"].AttemptCount.Should().Be(0, "attempt count is reset on retry");
        queue.Jobs["job-f"].LastError.Should().BeNull();
    }

    [Fact]
    public async Task RetryJob_rejects_non_Failed_job()
    {
        var exec = Make(out var queue, out _, out _, out _);
        queue.Jobs["job-p"] = MakeJob("job-p", JobStatus.Printed);
        var result = await exec.ExecuteAsync(
            Cmd("c9", CommandType.RetryJob, """{"jobId":"job-p"}"""), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Contain("only Failed jobs can be retried");
    }

    [Fact]
    public async Task RetryJob_reports_failed_for_unknown_job()
    {
        var exec = Make(out _, out _, out _, out _);
        var result = await exec.ExecuteAsync(
            Cmd("c10", CommandType.RetryJob, """{"jobId":"missing"}"""), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Contain("not found");
    }

    // ─── test_print (must enqueue a durable job, NOT call the spooler directly) ─

    [Fact]
    public async Task TestPrint_enqueues_durable_job_with_stable_idempotent_id()
    {
        var exec = Make(out var queue, out var printers, out _, out _);
        var result = await exec.ExecuteAsync(Cmd("c11", CommandType.TestPrint), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Succeeded);
        using var doc = JsonDocument.Parse(result.ResultJson!);
        var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
        jobId.Should().Be("test-print-c11",
            "the test-print JobId is derived from the command id so a duplicate test_print command is idempotent");
        queue.Jobs.Should().ContainKey(jobId);
        queue.Jobs[jobId].Status.Should().Be(JobStatus.Pending);
        queue.Jobs[jobId].Title.Should().Contain("Test Print");
        printers.SubmitCalls.Should().Be(0, "test_print MUST NOT call the spooler directly — it enqueues a durable job");
    }

    // ─── diagnostics ──────────────────────────────────────────────────────

    [Fact]
    public async Task Diagnostics_returns_companion_version_and_queue_summary()
    {
        var exec = Make(out var queue, out _, out _, out _);
        queue.Jobs["j1"] = MakeJob("j1", JobStatus.Pending);
        queue.Jobs["j2"] = MakeJob("j2", JobStatus.Printed);
        var result = await exec.ExecuteAsync(Cmd("c12", CommandType.Diagnostics), CancellationToken.None);
        result.Status.Should().Be(CommandStatus.Succeeded);
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("companionVersion").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("queueSummary").GetProperty("Pending").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("queueSummary").GetProperty("Printed").GetInt32().Should().Be(1);
    }

    // ─── fakes ────────────────────────────────────────────────────────────

    private static PrintJobRecord MakeJob(string id, JobStatus status) => new()
    {
        JobId = id,
        ShopId = "shop",
        DeviceId = "dev",
        Title = id,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeQueue : IJobQueue
    {
        public readonly Dictionary<string, PrintJobRecord> Jobs = new();
        public Task<PrintJobRecord> EnqueueAsync(PrintJobRecord job, CancellationToken ct = default)
        {
            if (!Jobs.ContainsKey(job.JobId)) Jobs[job.JobId] = job;
            return Task.FromResult(Jobs[job.JobId]);
        }
        public Task<PrintJobRecord?> DequeueAsync(CancellationToken ct = default) => Task.FromResult<PrintJobRecord?>(null);
        public Task UpdateAsync(PrintJobRecord job, CancellationToken ct = default) { Jobs[job.JobId] = job; return Task.CompletedTask; }
        public Task MarkPrintedAsync(string jobId, DateTimeOffset printedAt, int spoolerJobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReportedPrintedAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkFailedAsync(string jobId, string error, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReleaseAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelAsync(string jobId, CancellationToken ct = default)
        {
            if (Jobs.TryGetValue(jobId, out var j)) j.Status = JobStatus.Cancelled;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PrintJobRecord>> ListAsync(JobStatus? filterStatus = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PrintJobRecord>>(Jobs.Values.ToList());
        public Task ReplayInFlightAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeTerminalAsync(TimeSpan olderThan, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkUncertainAsync(string jobId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkOutcomeUnknownAsync(string jobId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkResolvedByOperatorAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePrinter : IPrinterService
    {
        public readonly List<PrinterInfo> Printers = new()
        {
            new() { FullName = "HP LaserJet", DisplayName = "HP LaserJet" },
            new() { FullName = "Canon MF", DisplayName = "Canon MF" },
        };
        public int DiscoverCalls;
        public int SubmitCalls;

        public IReadOnlyList<PrinterInfo> DiscoverPrinters() { DiscoverCalls++; return Printers; }
        public PrinterInfo? GetSelectedPrinter() => Printers[0];
        public Task SelectPrinterAsync(string printerFullName, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SpoolSubmission> SubmitAsync(PrintJobRecord job, CancellationToken ct = default)
        {
            SubmitCalls++;
            return Task.FromResult(new SpoolSubmission(1, job.PrinterName ?? "x", DateTimeOffset.UtcNow));
        }
        public Task<bool> HasPrintedAsync(int spoolerJobId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken ct = default)
            => Task.FromResult(new TestPrintResult { Success = true, SpoolerJobId = 1 });
    }

    private sealed class FakeBackend : IPrintShopBackend
    {
        public bool IsAuthenticated => true;
        public ShopInfo? CurrentShop => new() { Id = "shop", Name = "Shop", Slug = "s", BaseUrl = new Uri("https://x"), ApiBasePath = "/api/v1" };
        public DeviceRegistration? CurrentDevice => new() { DeviceId = "dev", ShopId = "shop", Hostname = "h", OperatingSystem = "os", AppVersion = "0", RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, Revoked = false };
        public int Heartbeats;
        public Task PairWithDeviceTokenAsync(string deviceToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PairResult> PairWithTicketAsync(string ticket, string deviceName, string? version, string? os, string? printerName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PendingCommand>> FetchPendingCommandsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PendingCommand>>(new List<PendingCommand>());
        public Task<CommandReportResult> ReportCommandStatusAsync(string commandId, string status, string? resultJson, CancellationToken ct = default)
            => Task.FromResult(new CommandReportResult(CommandId: "test", Status: "succeeded", UpdatedAt: DateTimeOffset.UtcNow));
        public Task<string> ExchangeDeviceTokenForJwtAsync(CancellationToken ct = default) => Task.FromResult("jwt");
        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default)
        {
            Heartbeats++;
            return Task.FromResult(new HeartbeatResult(DateTimeOffset.UtcNow, true, null));
        }
        public Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PrintJobRecord>>(new List<PrintJobRecord>());
        public Task<JobStatusReport> ReportJobStatusAsync(string jobId, string status, string? printerName, string? errorMessage, CancellationToken ct = default)
            => Task.FromResult(new JobStatusReport(jobId, status, true, false, null));
        public Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RevokeDeviceAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SyncPullResult> SyncPullAsync(string? cursor, int limit, CancellationToken ct = default)
            => Task.FromResult(new SyncPullResult(new List<SyncChange>(), null, false));
        public Task<SyncPushResult> SyncPushAsync(object changes, CancellationToken ct = default)
            => Task.FromResult(new SyncPushResult(new List<SyncPushResultItem>()));
        public Task<SyncStatusResult> SyncStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new SyncStatusResult("dev", "shop", null, null, null, true));
        public Task<ReceiptAcknowledgement> MarkJobReceivedAsync(string jobId, string checksum, long fileSize, DateTimeOffset receivedAt, CancellationToken ct = default)
            => Task.FromResult(new ReceiptAcknowledgement(jobId, "received", "local_verified", DateTimeOffset.UtcNow, false, null));
    }

    private sealed class FakeOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<PrinterOptions>
    {
        private readonly PrinterOptions _current;
        public FakeOptionsMonitor(PrinterOptions current) => _current = current;
        public PrinterOptions CurrentValue => _current;
        public PrinterOptions Get(string? name) => _current;
        public IDisposable? OnChange(Action<PrinterOptions, string?> listener) => null!;
    }
}
