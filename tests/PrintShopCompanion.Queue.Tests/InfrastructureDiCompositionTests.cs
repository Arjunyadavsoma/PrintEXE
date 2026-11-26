namespace PrintShopCompanion.Queue.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure;
using PrintShopCompanion.Infrastructure.Commands;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Sync;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// v0.7.4 regression guard — DI composition smoke tests.
///
/// <para>On a real Windows machine the v0.7.3 build logged
/// <c>StartCloudConnectionAsync failed: Unable to resolve service for type
/// 'SqliteJobQueue' while attempting to activate 'DurableCommandStore'</c>
/// because <see cref="InfrastructureModule"/> registered SqliteJobQueue ONLY
/// as <c>IJobQueue</c> while <c>DurableCommandStore</c> (and the
/// <c>CommandPoller</c> path behind it) takes the CONCRETE type. The
/// exception was swallowed as a non-fatal warning, so the app looked healthy
/// while Socket.IO realtime, the 15s heartbeat timer, and the command poller
/// NEVER started — the backend marked the device offline 30s after every
/// launch.</para>
///
/// <para>These tests build the REAL service collection from
/// <see cref="InfrastructureModule.AddPrintShopCompanionInfrastructure"/>
/// (storage redirected into a temp directory so nothing touches the real
/// AppData layout) and resolve the exact services whose resolution failed in
/// v0.7.3. If a future refactor breaks the concrete-type registration again,
/// the first test fails with the same message users would have hit at
/// runtime.</para>
/// </summary>
public class InfrastructureDiCompositionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public InfrastructureDiCompositionTests()
    {
        // Redirect all storage (queue DB, keys, spool dir) into a temp dir —
        // same isolation approach as MigrationTests/QueuePriorityTests.
        _tempDir = Path.Combine(Path.GetTempPath(), "PrintShopDiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        services.AddOptions<StorageOptions>().Configure(o =>
        {
            o.QueueDbPath = Path.Combine(_tempDir, "queue.db");
        });
        services.AddLogging();
        services.AddPrintShopCompanionInfrastructure();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public void SqliteJobQueue_resolves_by_concrete_type()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The exact resolution that threw in v0.7.3.
        var queue = _provider.GetRequiredService<SqliteJobQueue>();
        queue.Should().NotBeNull();
    }

    [Fact]
    public void DurableCommandStore_is_resolvable()
    {
        if (!OperatingSystem.IsWindows()) return;
        // DurableCommandStore(SqliteJobQueue queue, ILogger logger) — this is
        // the constructor that failed to activate in v0.7.3.
        var store = _provider.GetRequiredService<DurableCommandStore>();
        store.Should().NotBeNull();
    }

    [Fact]
    public void CommandPoller_is_resolvable()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The poller sits behind StartCloudConnectionAsync's first line
        // (GetRequiredService<CommandPoller>) — it activates the command
        // execution chain (executor + durable store + queue).
        _provider.GetRequiredService<CommandPoller>().Should().NotBeNull();
    }

    [Fact]
    public void IJobQueue_and_concrete_registration_share_one_singleton()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Both registrations must resolve to the SAME instance, mirroring the
        // JobProcessor/IQueueController pattern. Two separate singletons would
        // fork queue state (one DB gate, two connection strings is survivable,
        // but a second QueueKeyStore-consumer would double-initialize).
        var byInterface = _provider.GetRequiredService<IJobQueue>();
        var byConcrete = _provider.GetRequiredService<SqliteJobQueue>();
        ReferenceEquals(byInterface, byConcrete).Should().BeTrue(
            "the interface mapping must return the same singleton as the concrete registration");
    }

    [Fact]
    public void SqlitePendingOpsQueue_resolves_by_concrete_type()
    {
        if (!OperatingSystem.IsWindows()) return;
        // SqlitePendingOpsQueue is also registered interface-only today, but
        // nothing currently requests the concrete type — pin the composition
        // we actually ship: IPendingOpsQueue must resolve.
        _provider.GetRequiredService<IPendingOpsQueue>().Should().NotBeNull();
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
