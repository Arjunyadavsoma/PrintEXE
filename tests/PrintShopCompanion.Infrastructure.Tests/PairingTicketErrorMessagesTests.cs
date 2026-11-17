namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using PrintShopCompanion.Core.Contracts;
using Xunit;

/// <summary>
/// Phase 3 (brief §4) — tests for <see cref="PairingTicketErrorMessages.Map"/>.
/// Verifies each pairing-ticket error code (from the actual SaaS, not the
/// published contract — see PHASE3_CONTRACT_MATRIX.md Mismatch #4) maps to a
/// distinct, actionable user-facing message on the Pair Computer screen.
/// </summary>
public class PairingTicketErrorMessagesTests
{
    [Fact]
    public void VALIDATION_ERROR_maps_to_a_format_hint()
    {
        var ex = new PairingTicketException("VALIDATION_ERROR", "zod fail");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("64 hex characters");
        msg.Should().Contain("1–120 characters");
    }

    [Fact]
    public void UNAUTHORIZED_expired_maps_to_an_expired_message()
    {
        var ex = new PairingTicketException("UNAUTHORIZED", "Pairing ticket has expired. Request a new one.");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("expired");
        msg.Should().Contain("5 minutes");
    }

    [Fact]
    public void UNAUTHORIZED_unknown_maps_to_a_not_recognized_message()
    {
        var ex = new PairingTicketException("UNAUTHORIZED", "Invalid or unknown pairing ticket.");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("not recognized");
        msg.Should().NotContain("expired", "the unknown case must NOT say 'expired'");
    }

    [Fact]
    public void PAIRING_TICKET_CONSUMED_maps_to_an_already_used_message()
    {
        var ex = new PairingTicketException("PAIRING_TICKET_CONSUMED", "lost the race");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("already used");
    }

    [Fact]
    public void PAIRING_TICKET_UNAVAILABLE_maps_to_a_no_longer_active_message()
    {
        var ex = new PairingTicketException("PAIRING_TICKET_UNAVAILABLE", "status: revoked");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("no longer active");
    }

    [Fact]
    public void NOT_FOUND_maps_to_a_shop_gone_message()
    {
        var ex = new PairingTicketException("NOT_FOUND", "shop gone");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("shop");
        msg.Should().Contain("support");
    }

    [Fact]
    public void RATE_LIMITED_maps_to_a_slow_down_message()
    {
        var ex = new PairingTicketException("RATE_LIMITED", "slow down");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().NotBeNull();
        msg.Should().Contain("Too many");
    }

    [Fact]
    public void Unknown_code_returns_null_so_caller_falls_back_to_ex_Message()
    {
        var ex = new PairingTicketException("SOME_NEW_CODE", "future error");
        var msg = PairingTicketErrorMessages.Map(ex);
        msg.Should().BeNull("an unrecognized code should fall back to the raw message");
    }
}
