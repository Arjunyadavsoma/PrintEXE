namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Storage;
using PrintShopCompanion.Shared.Dto;
using Xunit;

/// <summary>
/// v1 contract §2 — tests for the pairing-ticket client's LOCAL validation
/// (before any HTTP call). The companion validates the ticket format + device
/// name on the client so the user gets immediate feedback without a round-trip.
/// These tests use a dummy HttpClient — no real network call is made because
/// the validation throws PairingTicketException before reaching SendAsync.
/// </summary>
public class PairingTicketValidationTests
{
    private static HttpPrintShopBackend MakeBackend(HttpMessageHandler? handler = null)
    {
        // Default handler: a stub that returns 401 UNAUTHORIZED so tests that
        // get past local validation don't hit the real network (which is
        // non-deterministic on CI — sandboxed runners may or may not reach
        // printshop-saas-three.vercel.app). Tests that fail-fast on local
        // validation never reach the handler.
        var http = new HttpClient(handler ?? new StubHandler401());
        var opts = Options.Create(new BackendOptions
        {
            BaseUrl = "https://printshop-saas-three.vercel.app",
            ApiBasePath = "/api/v1",
        });
        // IOptionsMonitor shim that returns our opts.Value.
        var monitor = new FakeOptionsMonitor(opts.Value);
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpPrintShopBackend>();
        return new HttpPrintShopBackend(http, monitor, logger, layout: null);
    }

    /// <summary>Deterministic 401 UNAUTHORIZED stub for tests that pass local validation.</summary>
    private sealed class StubHandler401 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(
                """{"success":false,"error":{"code":"UNAUTHORIZED","message":"Invalid or unknown pairing ticket."}}""",
                System.Text.Encoding.UTF8,
                "application/json");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = content });
        }
    }

    [Fact]
    public async Task Rejects_empty_ticket_with_VALIDATION_ERROR()
    {
        var backend = MakeBackend();
        var act = async () => await backend.PairWithTicketAsync("", "My-PC");
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Rejects_whitespace_only_ticket_with_VALIDATION_ERROR()
    {
        var backend = MakeBackend();
        var act = async () => await backend.PairWithTicketAsync("   ", "My-PC");
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]  // 64 non-hex
    [InlineData("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6X")]  // 63 hex + 1 non-hex
    public async Task Rejects_malformed_ticket_with_VALIDATION_ERROR(string ticket)
    {
        var backend = MakeBackend();
        var act = async () => await backend.PairWithTicketAsync(ticket, "My-PC");
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Rejects_empty_deviceName_with_VALIDATION_ERROR()
    {
        var backend = MakeBackend();
        // 64 hex chars (valid format) but empty deviceName → VALIDATION_ERROR before the network.
        var ticket = new string('a', 64);
        var act = async () => await backend.PairWithTicketAsync(ticket, "");
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Rejects_overlong_deviceName_with_VALIDATION_ERROR()
    {
        var backend = MakeBackend();
        var ticket = new string('a', 64);
        // Phase 3 fix: the actual SaaS zod caps deviceName at 120 (not 255 per
        // the published contract). 121 chars must fail locally before the network.
        var deviceName = new string('x', 121);
        var act = async () => await backend.PairWithTicketAsync(ticket, deviceName);
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Theory]
    [InlineData("version", 41)]
    [InlineData("os", 41)]
    [InlineData("printerName", 201)]
    public async Task Rejects_overlong_metadata_fields_with_VALIDATION_ERROR(string field, int len)
    {
        var backend = MakeBackend();
        var ticket = new string('a', 64);
        var overlong = new string('y', len);
        var act = async () => await backend.PairWithTicketAsync(
            ticket, "My-PC",
            version: field == "version" ? overlong : null,
            os: field == "os" ? overlong : null,
            printerName: field == "printerName" ? overlong : null);
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Trims_whitespace_around_ticket_before_format_check()
    {
        var backend = MakeBackend();
        // 64 hex chars surrounded by whitespace — should be trimmed + pass local
        // format validation. The HTTP call then fails (no real backend); the impl
        // wraps any non-2xx / network failure as a PairingTicketException with
        // code "UNKNOWN" (or the backend's error code). The point of this test
        // is that local VALIDATION_ERROR did NOT fire — the trim worked.
        var ticket = "  " + new string('a', 64) + "  ";
        var act = async () => await backend.PairWithTicketAsync(ticket, "My-PC");
        var ex = await act.Should().ThrowAsync<PairingTicketException>();
        ex.Which.Code.Should().NotBe("VALIDATION_ERROR",
            "a trimmed 64-hex ticket passes local format validation; the failure must be downstream (network/backend), not a local validation error");
    }

    [Fact]
    public void PairingTicketException_maps_the_actual_SaaS_error_codes_Phase3()
    {
        // Phase 3: the actual SaaS returns these codes (NOT the published contract's
        // TICKET_EXPIRED/TICKET_CONSUMED). See docs/PHASE3_CONTRACT_MATRIX.md Mismatch #4.
        var unauthorizedExpired = new PairingTicketException("UNAUTHORIZED", "Pairing ticket has expired. Request a new one.");
        var unauthorizedUnknown = new PairingTicketException("UNAUTHORIZED", "Invalid or unknown pairing ticket.");
        var unavailable = new PairingTicketException("PAIRING_TICKET_UNAVAILABLE", "status: revoked");
        var consumed = new PairingTicketException("PAIRING_TICKET_CONSUMED", "lost the race");
        var validation = new PairingTicketException("VALIDATION_ERROR", "bad body");
        var rateLimited = new PairingTicketException("RATE_LIMITED", "slow down");
        var notFound = new PairingTicketException("NOT_FOUND", "shop gone");
        var unknown = new PairingTicketException("UNKNOWN", "???");

        // All ticket-content errors → IsTicketError true (UI says "generate a new code")
        unauthorizedExpired.IsTicketError.Should().BeTrue();
        unauthorizedUnknown.IsTicketError.Should().BeTrue();
        unavailable.IsTicketError.Should().BeTrue();
        consumed.IsTicketError.Should().BeTrue();
        validation.IsTicketError.Should().BeTrue();
        // NOT_FOUND is NOT a ticket-content error (the shop was deleted — different recovery path)
        notFound.IsTicketError.Should().BeFalse();
        // Rate limiting + unknown are not ticket-content errors
        rateLimited.IsTicketError.Should().BeFalse();
        unknown.IsTicketError.Should().BeFalse();

        // IsExpired distinguishes the two UNAUTHORIZED sub-cases (server uses the
        // same code for unknown + expired; the message distinguishes them).
        unauthorizedExpired.IsExpired.Should().BeTrue("the message says 'expired'");
        unauthorizedUnknown.IsExpired.Should().BeFalse("the message says 'unknown'");

        rateLimited.IsRateLimited.Should().BeTrue();
        unknown.IsRateLimited.Should().BeFalse();
    }

    private sealed class FakeOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<BackendOptions>
    {
        private readonly BackendOptions _current;
        public FakeOptionsMonitor(BackendOptions current) => _current = current;
        public BackendOptions CurrentValue => _current;
        public BackendOptions Get(string? name) => _current;
        public IDisposable? OnChange(Action<BackendOptions, string?> listener) => null!;
    }
}
