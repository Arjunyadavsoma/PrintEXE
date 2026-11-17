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
    private static HttpPrintShopBackend MakeBackend()
    {
        var http = new HttpClient();
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
        var deviceName = new string('x', 256);  // contract: 1-255
        var act = async () => await backend.PairWithTicketAsync(ticket, deviceName);
        (await act.Should().ThrowAsync<PairingTicketException>())
            .Which.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Trims_whitespace_around_ticket_before_format_check()
    {
        var backend = MakeBackend();
        // 64 hex chars surrounded by whitespace — should be trimmed + pass format
        // validation (then fail at the network because no real backend).
        var ticket = "  " + new string('a', 64) + "  ";
        var act = async () => await backend.PairWithTicketAsync(ticket, "My-PC");
        // Should NOT throw VALIDATION_ERROR — should throw something else (network/BaseUrl).
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().NotBeOfType<PairingTicketException>(
            "a trimmed 64-hex ticket passes local validation; the failure must come from the (absent) network, not local validation");
    }

    [Fact]
    public void PairingTicketException_IsTicketError_distinguishes_ticket_errors_from_rate_limits()
    {
        var expired = new PairingTicketException("TICKET_EXPIRED", "expired");
        var consumed = new PairingTicketException("TICKET_CONSUMED", "used");
        var unauthorized = new PairingTicketException("UNAUTHORIZED", "no ticket");
        var rateLimited = new PairingTicketException("RATE_LIMITED", "slow down");
        var unknown = new PairingTicketException("UNKNOWN", "???");

        expired.IsTicketError.Should().BeTrue();
        consumed.IsTicketError.Should().BeTrue();
        unauthorized.IsTicketError.Should().BeTrue();
        rateLimited.IsTicketError.Should().BeFalse("rate limiting is not a ticket-content error");
        rateLimited.IsRateLimited.Should().BeTrue();
        unknown.IsTicketError.Should().BeFalse();
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
