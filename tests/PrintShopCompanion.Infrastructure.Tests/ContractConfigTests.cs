namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using Xunit;

/// <summary>
/// v1 contract §4 + §6 + §8 — config + allowlist pinning. These tests fail if
/// someone changes a default or the allowlist without updating the contract
/// guard, catching drift early.
/// </summary>
public class ContractConfigTests
{
    [Fact]
    public void BackendOptions_heartbeat_interval_default_is_15s_per_contract()
    {
        // v1 contract §4: heartbeat every 15s. The earlier default (60s) was too
        // slow — the device showed as offline during normal operation.
        var opts = new BackendOptions();
        opts.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));
        opts.HeartbeatIntervalSeconds.Should().Be(15);
    }

    [Fact]
    public void BackendOptions_poll_interval_default_is_within_contract_2_to_5s_range()
    {
        // v1 contract §13: poll every 2-5 seconds.
        var opts = new BackendOptions();
        opts.PollFallbackInterval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        opts.PollFallbackInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(CommandType.RefreshPrinters)]
    [InlineData(CommandType.Pause)]
    [InlineData(CommandType.Resume)]
    [InlineData(CommandType.CancelJob)]
    [InlineData(CommandType.RetryJob)]
    [InlineData(CommandType.TestPrint)]
    [InlineData(CommandType.Diagnostics)]
    public void CommandType_allowlist_accepts_all_seven_contract_types(string type)
    {
        // v1 contract §8: the allowlist is exactly these 7 command types.
        CommandType.IsAllowed(type).Should().BeTrue();
    }

    [Theory]
    [InlineData("run_shell")]
    [InlineData("exec")]
    [InlineData("delete_file")]
    [InlineData("restart")]       // close-but-not-a-real-command
    [InlineData("")]
    [InlineData(null)]
    public void CommandType_allowlist_rejects_unsupported_types(string? type)
    {
        // The allowlist is case-insensitive on contains (so "Refresh_Printers"
        // matches "refresh_printers" — server-case-variation tolerance). This
        // test asserts only truly-unsupported strings are rejected; the
        // case-sensitivity is NOT a contract requirement.
        CommandType.IsAllowed(type).Should().BeFalse();
    }

    [Fact]
    public void CommandType_All_has_exactly_seven_types()
    {
        CommandType.All.Should().HaveCount(7);
        CommandType.All.Should().Contain(new[]
        {
            "refresh_printers", "pause", "resume",
            "cancel_job", "retry_job", "test_print", "diagnostics",
        });
    }

    [Fact]
    public void CommandStatus_All_matches_the_SaaS_REPORTABLE_STATUSES_enum()
    {
        // Phase F: the SaaS REPORTABLE_STATUSES (route.ts, commit ce75cd26)
        // now includes 'outcome_unknown' — 6 values, not 5. The companion
        // may report all 6. This test guards against drift: if the SaaS adds
        // or removes a value, this test must be updated in lockstep.
        CommandStatus.All.Should().HaveCount(6);
        CommandStatus.All.Should().Contain(new[]
        {
            "delivered", "accepted", "executing", "succeeded", "failed", "outcome_unknown",
        });
        // The terminal subset the SaaS treats as terminal (sets executedAt).
        CommandStatus.Terminal.Should().BeEquivalentTo(new[]
        {
            "succeeded", "failed", "outcome_unknown",
        });
    }
}
