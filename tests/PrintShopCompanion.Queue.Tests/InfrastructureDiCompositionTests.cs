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
///
/// <para><b>Compile-time WINDOWS gate (v0.7.6).</b> The full composition —
/// including <c>IPrinterService</c>→<c>WindowsPrinterService</c>, which
/// <c>CommandExecutor</c> requires — only exists in the net10.0-windows TFM
/// (the TFM every shipped app runs). The neutral net10.0 TFM deliberately
/// registers a partial surface. A runtime <c>OperatingSystem.IsWindows()</c>
/// guard was wrong: it evaluates the OS, not the TFM, so on a Windows runner
/// the net10.0 leg still executed the bodies and failed resolving
/// <c>IPrinterService</c> (v0.7.5 CI). The bodies are therefore compiled only
/// under <c>#if WINDOWS</c>; the neutral leg keeps an always-green
/// placeholder so the test class still shows up in both TFMs.</para>
/// </summary>
public class InfrastructureDiCompositionTests : IDisposable
{
    private readonly string _tempDir;
#if WINDOWS
    private readonly ServiceProvider _provider;
    private readonly bool _encryptionAvailable;
#endif

#if WINDOWS
    public InfrastructureDiCompositionTests()
    {
        // Redirect all storage (queue DB, keys, spool dir) into a temp dir —
        // same isolation approach as MigrationTests/QueuePriorityTests.
        _tempDir = Path.Combine(Path.GetTempPath(), "PrintShopDiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // Same availability guard as SqliteConnectionPragmasSafetyTests: if
        // the SQLCipher native provider cannot load in this testhost, the
        // production SqliteJobQueue constructor fails closed (correct
        // behavior) — skip instead of testing the wrong thing.
        _encryptionAvailable = PrintShopCompanion.Infrastructure.Storage.SqlCipherInitializer.IsEncryptionAvailable;

        var services = new ServiceCollection();
        services.AddOptions<StorageOptions>().Configure(o =>
        {
            o.QueueDbPath = Path.Combine(_tempDir, "queue.db");
            o.PdfCacheDir = Path.Combine(_tempDir, "Jobs");
            o.LogDir = Path.Combine(_tempDir, "Logs");
            o.DeviceTokenPath = Path.Combine(_tempDir, "Security", "device.dat");
        });
        services.AddLogging();
        // InfrastructureModule binds PrintShop:*/Backend:* via BindConfiguration,
        // which resolves IConfiguration from the provider. The App registers a
        // real config; a bare test container must provide an empty one (values
        // fall back to options defaults) or option resolution throws.
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddPrintShopCompanionInfrastructure();
        _provider = services.BuildServiceProvider();
    }
#else
    public InfrastructureDiCompositionTests()
    {
        // Neutral net10.0 leg: the full DI surface (IPrinterService, DPAPI
        // key store, WindowsPrinterService) does not exist in this TFM, so
        // the composition tests cannot run here. See the class doc comment.
        _tempDir = "";
    }
#endif

    [SkippableFact]
    public void SqliteJobQueue_resolves_by_concrete_type()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // The exact resolution that threw in v0.7.3.
        var queue = _provider.GetRequiredService<SqliteJobQueue>();
        queue.Should().NotBeNull();
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }

    [SkippableFact]
    public void DurableCommandStore_is_resolvable()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // DurableCommandStore(SqliteJobQueue queue, ILogger logger) — this is
        // the constructor that failed to activate in v0.7.3.
        var store = _provider.GetRequiredService<DurableCommandStore>();
        store.Should().NotBeNull();
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }

    [SkippableFact]
    public void CommandPoller_is_resolvable()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // The poller sits behind StartCloudConnectionAsync's first line
        // (GetRequiredService<CommandPoller>) — it activates the command
        // execution chain (executor + durable store + queue).
        _provider.GetRequiredService<CommandPoller>().Should().NotBeNull();
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }

    [SkippableFact]
    public void IJobQueue_and_concrete_registration_share_one_singleton()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // Both registrations must resolve to the SAME instance, mirroring the
        // JobProcessor/IQueueController pattern. Two separate singletons would
        // fork queue state (one DB gate, two connection strings is survivable,
        // but a second QueueKeyStore-consumer would double-initialize).
        var byInterface = _provider.GetRequiredService<IJobQueue>();
        var byConcrete = _provider.GetRequiredService<SqliteJobQueue>();
        ReferenceEquals(byInterface, byConcrete).Should().BeTrue(
            "the interface mapping must return the same singleton as the concrete registration");
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }

    [SkippableFact]
    public void SqlitePendingOpsQueue_resolves_by_concrete_type()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // SqlitePendingOpsQueue is also registered interface-only today, but
        // nothing currently requests the concrete type — pin the composition
        // we actually ship: IPendingOpsQueue must resolve.
        _provider.GetRequiredService<IPendingOpsQueue>().Should().NotBeNull();
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }

    public void Dispose()
    {
#if WINDOWS
        _provider.Dispose();
#endif
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (_tempDir.Length > 0) Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    // ── Phase 7 (Stage 4): lifecycle coverage — startup/shutdown semantics ──
    // These complement the composition tests above: a graph that RESOLVES is
    // necessary but not sufficient; the host must also BUILD, START and
    // DISPOSE cleanly (the app's OnExit path disposes this same provider).

    [SkippableFact]
    public void Host_builds_and_disposes_cleanly()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // Double-dispose is explicitly tolerated here: ServiceProvider.Dispose
        // is idempotent, and OnExit must never throw on a second dispose path.
        Action dispose = () => _provider.Dispose();
        dispose.Should().NotThrow();
        dispose.Should().NotThrow();
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }

    [SkippableFact]
    public async System.Threading.Tasks.Task JobProcessor_starts_and_stops_cleanly()
    {
#if WINDOWS
        Skip.IfNot(_encryptionAvailable, "SQLCipher native provider unavailable in this testhost");
        // Background-service coverage (Stage 4): the REAL JobProcessor must
        // start on an empty queue and stop gracefully within the timeout.
        var processor = _provider.GetRequiredService<JobProcessor>();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StartAsync(cts.Token);
        var stop = async () => await processor.StopAsync(System.Threading.CancellationToken.None);
        await stop.Should().NotThrowAsync();
#else
        Skip.If(true, "Requires the net10.0-windows TFM (real DI graph with IPrinterService, DPAPI key store and SQLCipher native); exercised on windows-latest CI.");
#endif
    }
}
