namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using PrintShopCompanion.Infrastructure.Commands;
using PrintShopCompanion.Core.Models;
using Xunit;

/// <summary>
/// v1 contract §7 + Phase 8 §11 — tests for the in-process command dedup store.
/// The store prevents re-executing a command the backend re-served before our
/// terminal ack landed. Bounded to MaxEntries (1000) via LRU eviction.
/// </summary>
public class CommandDedupStoreTests
{
    [Fact]
    public void TryGet_returns_false_for_unseen_command()
    {
        var store = new CommandDedupStore();
        store.TryGet("never-seen", out var result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void Record_then_TryGet_returns_the_stored_status_and_result()
    {
        var store = new CommandDedupStore();
        store.Record("c1", CommandStatus.Succeeded, """{"printers":["HP"]}""", null);
        store.TryGet("c1", out var result).Should().BeTrue();
        result!.Status.Should().Be(CommandStatus.Succeeded);
        result.ResultJson.Should().Be("""{"printers":["HP"]}""");
        result.Error.Should().BeNull();
        result.ProcessedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Record_is_idempotent_on_re_record_with_same_commandId()
    {
        var store = new CommandDedupStore();
        store.Record("c2", CommandStatus.Succeeded, "{}", null);
        store.Record("c2", CommandStatus.Failed, null, "late failure");
        store.Count.Should().Be(1, "re-recording the same commandId updates in place, doesn't add a new entry");
        store.TryGet("c2", out var result).Should().BeTrue();
        result!.Status.Should().Be(CommandStatus.Failed);
        result.Error.Should().Be("late failure");
    }

    [Fact]
    public void Evicts_oldest_when_MaxEntries_exceeded()
    {
        var store = new CommandDedupStore();
        // Fill to exactly MaxEntries.
        for (int i = 0; i < CommandDedupStore.MaxEntries; i++)
            store.Record($"c{i}", CommandStatus.Succeeded, "{}", null);
        store.Count.Should().Be(CommandDedupStore.MaxEntries);

        // Add one more — the oldest (c0) should be evicted.
        store.Record("c-new", CommandStatus.Succeeded, "{}", null);
        store.Count.Should().Be(CommandDedupStore.MaxEntries, "eviction keeps the count at the cap");
        store.TryGet("c0", out _).Should().BeFalse("the oldest entry is evicted");
        store.TryGet("c-new", out _).Should().BeTrue();
        // The newest of the original batch (c{MaxEntries-1}) should survive.
        store.TryGet($"c{CommandDedupStore.MaxEntries - 1}", out _).Should().BeTrue();
    }

    [Fact]
    public void LRU_evicts_in_fifo_order_when_not_re_touched()
    {
        var store = new CommandDedupStore();
        // Record three commands; "first" is the oldest.
        store.Record("first", CommandStatus.Succeeded, "{}", null);
        store.Record("second", CommandStatus.Succeeded, "{}", null);
        store.Record("third", CommandStatus.Succeeded, "{}", null);

        // Fill to the cap + one more to trigger one eviction.
        for (int i = 0; i < CommandDedupStore.MaxEntries; i++)
            store.Record($"fill-{i}", CommandStatus.Succeeded, "{}", null);

        // "first" was the LRU oldest (never re-touched) → evicted first.
        store.TryGet("first", out _).Should().BeFalse("first is the LRU oldest and was evicted");
        // "second" + "third" were also old enough to be evicted by the 1000-fill.
        store.TryGet("second", out _).Should().BeFalse();
        store.TryGet("third", out _).Should().BeFalse();
        // The newest of the fill batch survived.
        store.TryGet($"fill-{CommandDedupStore.MaxEntries - 1}", out _).Should().BeTrue();
    }

    [Fact]
    public void Re_record_updates_status_in_place_without_growing_count()
    {
        var store = new CommandDedupStore();
        store.Record("c", CommandStatus.Delivered, null, null);
        store.Record("c", CommandStatus.Succeeded, "{}", null);
        store.Count.Should().Be(1);
        store.TryGet("c", out var result).Should().BeTrue();
        result!.Status.Should().Be(CommandStatus.Succeeded);
    }
}
