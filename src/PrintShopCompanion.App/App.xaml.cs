namespace PrintShopCompanion.App;

using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrintShopCompanion.App.Native;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.App.Tray;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.App.Views;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Commands;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using PrintShopCompanion.Infrastructure.Sync;
using PrintShopCompanion.Infrastructure.Update;
using Serilog;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;

    // Single shared CancellationTokenSource for ALL background work. Cancelled
    // once on shutdown so every background task stops in a coordinated way
    // instead of each component owning its own CTS that fires at slightly
    // different times. The previous code had separate CTSes in SyncEngine,
    // JobProcessor, and the fire-and-forget startup Task.Run — leading to
    // 2s + 2s + 3s = 7s of shutdown latency in the worst case.
    private CancellationTokenSource? _appCts;

    // Background task references — needed so we can WaitAsync for them on
    // shutdown instead of letting them die mid-DB-write.
    private Task? _startupTask;
    private Task? _updateCheckTask;

    // v1 contract §3 + §6 — timers for the 15s heartbeat + the 2-3s command poll.
    // Disposed in OnExit. Null when the device is not paired (cloud connection deferred).
    private System.Threading.Timer? _heartbeatTimer;
    private System.Threading.Timer? _commandPollTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Global safety nets (professional crash handling) ──────────────
        // Previously NO handler existed, so any unhandled UI-thread exception
        // (a binding callback, a Loaded handler, a converter) killed the
        // process with the raw Windows crash dialog — "the app is crashing".
        // Now: log the full stack, show a readable dialog, and KEEP RUNNING.
        // A companion app that survives a bad interaction is better than one
        // that dies; genuinely fatal state still exits via the startup catch.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // CLI args
        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--register-autostart") { RegisterAutoStart(); Shutdown(0); return; }
            if (e.Args[0] == "--unregister-autostart") { UnregisterAutoStart(); Shutdown(0); return; }
            if (e.Args[0] == "--version")
            {
                Console.WriteLine(AppVersion.Short);
                Shutdown(0);
                return;
            }
        }

        // Serilog
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "logs");
        Directory.CreateDirectory(logDir);
        // Phase 4 (B-012): the File sink is wrapped in the DeviceTokenRedactor
        // so both property values AND message-template text are scrubbed of
        // device tokens / bearer / authorization secrets before they hit disk.
        // (The redactor existed but was never wired — tokens could reach logs.)
        // Cascade: outer logger → redactor (sink decorator) → inner File logger.
        var fileLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(Path.Combine(logDir, "companion-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new PrintShopCompanion.App.Services.DeviceTokenRedactor(fileLogger))
            .CreateLogger();

        Log.Information("=== PrintShop Companion v{Version} starting ===", AppVersion.Short);

        try
        {
            // Single instance — acquire mutex
            _singleInstance = new SingleInstance("PrintShop-Companion-Mutex-v4");
            if (!_singleInstance.AcquireOrForward(e.Args))
            {
                Log.Information("Another instance is already running — exiting");
                Shutdown(0);
                return;
            }
            Log.Information("Mutex acquired");

            // Create the shared CTS NOW so every subsequent background task
            // shares it. We pass its Token to SyncEngine.Start,
            // JobProcessor.StartAsync, and the fire-and-forget startup Task.
            _appCts = new CancellationTokenSource();

            // DI — build synchronously, no async calls here
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    // Per-user overrides. THREE sources, LAST one wins:
                    //  1. appsettings.Local.json next to the exe (legacy — kept
                    //     for existing installs).
                    //  2. %AppData%\PrintShop\Companion\appsettings.Local.json —
                    //     where LocalSettingsStore writes (Program Files is not
                    //     writable without elevation; Phase 0 audit fix).
                    //  3. The same AppData file is added LAST via a relative
                    //     path — Phase 1 verification: AddJsonFile with an
                    //     ABSOLUTE path is a silent no-op (PhysicalFileProvider
                    //     rejects rooted paths), so the Phase 0 override source
                    //     never actually loaded. SetBasePath is re-pointed at
                    //     the AppData directory and a RELATIVE file name is
                    //     used; the base path is restored afterwards.
                    cfg.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    var appDataDir = System.IO.Path.GetDirectoryName(
                        PrintShopCompanion.Infrastructure.Storage.LocalSettingsStore.SettingsPath)!;
                    cfg.SetBasePath(appDataDir);
                    cfg.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                    cfg.SetBasePath(AppContext.BaseDirectory);
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.Configure<BackendOptions>(ctx.Configuration.GetSection(BackendOptions.SectionName));
                    services.Configure<PrinterOptions>(ctx.Configuration.GetSection(PrinterOptions.SectionName));
                    services.Configure<StorageOptions>(ctx.Configuration.GetSection(StorageOptions.SectionName));
                    services.Configure<AppOptions>(ctx.Configuration.GetSection(AppOptions.SectionName));

                    services.AddPrintShopCompanionInfrastructure();

                    // Shared observable shop-data store: SyncEngine pulls and
                    // Socket.IO realtime events write into it; every page binds
                    // to it so the app matches the shopkeeper webpage live.
                    services.AddSingleton<PrintShopCompanion.Core.Services.ShopDataStore>(_ =>
                        new PrintShopCompanion.Core.Services.ShopDataStore(action =>
                        {
                            if (Dispatcher.CheckAccess()) action();
                            else Dispatcher.Invoke(action);
                        }));

                    // Auto-sign-in status shared between the startup restore task
                    // and the Login screen's status banner (login-first UX).
                    services.AddSingleton<PrintShopCompanion.App.Services.SessionRestoreState>();

                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<MainWindow>();
                    services.AddTransient<LoginViewModel>();
                    services.AddTransient<OverviewViewModel>();
                    services.AddTransient<OrdersViewModel>();
                    services.AddTransient<PrintersViewModel>();
                    services.AddTransient<PrintQueueViewModel>();
                    services.AddTransient<SyncCenterViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<OrderDetailsViewModel>();
                    services.AddTransient<LoginView>();
                    services.AddTransient<OverviewView>();
                    services.AddTransient<OrdersView>();
                    services.AddTransient<PrintersView>();
                    services.AddTransient<PrintQueueView>();
                    services.AddTransient<SyncCenterView>();
                    services.AddTransient<SettingsView>();
                    services.AddTransient<OrderDetailsView>();
                    services.AddSingleton<TrayIconHost>();
                })
                .Build();

            // StartAsync is synchronous-safe (it doesn't use the Dispatcher)
            _host.StartAsync().Wait();
            Log.Information("DI host started");

            // CHECK for saved credentials SYNCHRONOUSLY (no async, no .Result, no deadlock)
            // Check BOTH the current DeviceTokenStore path (%LocalAppData%\...\Security\device.dat)
            // and the legacy path (%AppData%\...\device.dat) that MigrateDeviceToken() handles.
            // The previous code checked only the legacy path, so a user with the new path landed
            // on Login even with a valid token — see docs/AUDIT.md §11 + §16 item #5.
            var layout = _host.Services.GetRequiredService<PrintShopCompanion.Infrastructure.Storage.LocalStorageLayout>();
            bool hasSavedCredentials = File.Exists(layout.DeviceTokenPath)
                || (layout.LegacyDeviceTokenPath is { } legacy && File.Exists(legacy));
            Log.Information("Saved credentials: {Has} (current={Current}, legacy={Legacy})",
                hasSavedCredentials ? "FOUND" : "NOT FOUND",
                File.Exists(layout.DeviceTokenPath),
                layout.LegacyDeviceTokenPath is { } lp && File.Exists(lp));

            // Navigate to the right view — ALWAYS Login first (login-first UX).
            // The previous code jumped straight to Overview when a saved
            // credential FILE existed, so the shopkeeper saw the orders page
            // before the app had verified anything with the server — with a
            // stale/revoked token it flashed Overview and then fell back to
            // Login, and after an unclean shutdown it showed stale data as if
            // signed in. Now the app opens on Login with a visible
            // "Signing you in automatically…" state; the background restore
            // below navigates to Overview ONLY after the credentials are
            // actually verified, and otherwise explains why it stayed here.
            var nav = _host.Services.GetRequiredService<INavigationService>();
            nav.NavigateTo("Login");
            var restoreState = _host.Services.GetRequiredService<PrintShopCompanion.App.Services.SessionRestoreState>();
            if (hasSavedCredentials)
                restoreState.Begin("Signing you in automatically…");
            Log.Information("Navigated to: {View} (auto-sign-in: {Auto})", nav.CurrentView,
                hasSavedCredentials ? "pending" : "none");

            // Deep-link pairing activation (integration spec §7.3): a
            // printshop://pair?ticket=… (or legacy printshopcompanion://) URI
            // from the website's Add Device dialog. Only route it when the
            // device is NOT already paired — a paired device silently
            // re-pairing itself from a URI click would be a security hole.
            // The Login screen consumes the URI once and PRE-FILLS the
            // pairing code; the user still confirms with an explicit click.
            if (UriActivator.GetActivationUri() is { } activationUri)
            {
                if (!hasSavedCredentials)
                {
                    Dispatcher.Invoke(() => nav.NavigateTo("Login"));
                    Log.Information("Protocol activation detected: {Scheme}://… — Login screen will offer the pairing code", activationUri.Scheme);
                }
                else
                {
                    Log.Warning(
                        "Protocol activation ignored: device already paired. To pair a different shop, unpair first (Settings) or use the pairing code manually.");
                }
            }

            // SHOW THE WINDOW IMMEDIATELY — before any async work
            var main = _host.Services.GetRequiredService<MainWindow>();
            main.Show();
            Log.Information("Main window shown");

            // ALL ASYNC WORK happens AFTER the window is shown.
            // We capture the Task so OnExit can WaitAsync for it before disposing.
            _startupTask = Task.Run(async () =>
            {
                try
                {
                    // Restore credentials — try BOTH paths:
                    //   1. The v0.4.7+ device-token flow (HttpPrintShopBackend.RestoreAsync)
                    //   2. The legacy shopkeeper session flow (HttpShopBackend.RestoreSession)
                    // The app STAYS on the Login screen (with a status line) until one
                    // of them succeeds; background services start only after that.
                    bool restored = false;

                    // Path 1: device-token restore (the recommended v0.4.7+ flow).
                    if (hasSavedCredentials)
                    {
                        try
                        {
                            var printShopBackend = _host.Services.GetRequiredService<IPrintShopBackend>() as HttpPrintShopBackend;
                            if (printShopBackend != null)
                            {
                                restored = await printShopBackend.RestoreAsync();
                                if (restored)
                                {
                                    Log.Information("Device token restored in background");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Device token restore failed; trying legacy shopkeeper session");
                        }
                    }

                    // Path 2: legacy shopkeeper session (kept for backward compat with shops
                    // that haven't set up a print agent yet — they used email+password login).
                    if (!restored && hasSavedCredentials)
                    {
                        try
                        {
                            var store = _host.Services.GetRequiredService<ICredentialStore>();
                            var creds = await store.LoadAsync();
                            if (creds?.Session != null)
                            {
                                var shopBackend = _host.Services.GetRequiredService<IShopBackend>() as HttpShopBackend;
                                if (shopBackend != null)
                                {
                                    shopBackend.RestoreSession(creds.Session);
                                    restored = true;
                                    Log.Information("Legacy shopkeeper session restored in background");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Legacy shopkeeper session restore failed");
                        }
                    }

                    if (restored)
                    {
                        // Credentials verified — NOW enter the app.
                        Dispatcher.Invoke(() =>
                        {
                            restoreState.Succeeded();
                            restoreState.Reset();
                            nav.NavigateTo("Overview");
                        });
                        Log.Information("Auto sign-in succeeded — navigated to Overview");
                    }
                    else
                    {
                        // Both restore paths failed — stay on Login and say why.
                        string reason = hasSavedCredentials
                            ? "Couldn't sign in automatically — the saved session is no longer valid. Sign in again below, or pair with a new pairing code."
                            : "Not paired yet — pair this computer with a pairing code from the web dashboard, or sign in.";
                        Dispatcher.Invoke(() => restoreState.Fail(reason));
                        Log.Information("Auto sign-in failed — staying on Login");
                        return; // no credentials → do NOT start background services
                    }

                    // Start background services — pass the shared token so they
                    // all stop together on shutdown. ONLY after credentials are
                    // verified: the previous code started the sync/print pipeline
                    // unconditionally, so a device with a dead token kept firing
                    // authenticated requests (and showed stale UI activity) before
                    // the restore verdict was in.
                    var queue = _host.Services.GetRequiredService<IJobQueue>();
                    await queue.ReplayInFlightAsync();
                    _host.Services.GetRequiredService<JobProcessor>().StartAsync(_appCts.Token);
                    Log.Information("JobProcessor started");

                    var sync = _host.Services.GetRequiredService<SyncEngine>();
                    sync.Start(_appCts.Token);
                    Log.Information("SyncEngine started");

                    // v0.5: Start the FileRetentionManager — deletes completed/failed
                    // PDFs after 60 min (configurable). Runs on startup (catch-up from
                    // crash) then every 60 seconds. Stopped via shared _appCts.Token.
                    var retention = _host.Services.GetRequiredService<FileRetentionManager>();
                    retention.Start(_appCts.Token);
                    Log.Information("FileRetentionManager started");

                    // v1 contract §3 + §6 — start the realtime client (Socket.IO), the
                    // command poller (REST fallback + immediate on device.command.issued),
                    // and a 15s heartbeat timer. These only run if the device is paired
                    // (the realtime client + poller require a valid device token).
                    await StartCloudConnectionAsync();
                    await TryRotateStaleCredentialAsync();
                }
                catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
                {
                    Log.Information("Background startup cancelled by shutdown");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Background startup failed (non-fatal)");
                    try
                    {
                        Dispatcher.Invoke(() => restoreState.Fail(
                            "Something went wrong while signing in automatically. Sign in manually below."));
                    }
                    catch { /* shutting down */ }
                }
            }, _appCts.Token);

            // Tray (synchronous — safe)
            try
            {
                _tray = _host.Services.GetRequiredService<TrayIconHost>();
                _tray.Show();
                Log.Information("Tray shown");
            }
            catch (Exception ex) { Log.Warning(ex, "Tray setup failed"); }

            // Auto-start (synchronous — safe)
            try { _host.Services.GetRequiredService<AutoStartService>().SyncFromOptions(); } catch { }

            // Update check (fire-and-forget but tracked for shutdown)
            _updateCheckTask = Task.Run(async () =>
            {
                try
                {
                    // First, check for a sentinel file from a previously failed
                    // .bat updater. If present, warn the user that the previous
                    // update failed.
                    var update = _host.Services.GetRequiredService<IUpdateService>();
                    if (update is GitHubUpdateService ghUpdate)
                    {
                        var failureReason = ghUpdate.CheckForFailedUpdateSentinel();
                        if (failureReason is not null)
                        {
                            Log.Warning("Previous update failed: {Reason}", failureReason);
                            Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(
                                    $"A previous auto-update failed ({failureReason}).\n\n" +
                                    "The app rolled back to the previous version. " +
                                    "You can retry from the in-app banner, or download manually from\n" +
                                    "https://github.com/Arjunyadavsoma/PrintEXE/releases/latest",
                                    "Previous update failed",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            });
                        }
                    }

                    if (await update.CheckForUpdatesAsync())
                    {
                        Log.Information("Update available: {Current} -> {Latest}", update.CurrentVersion, update.LatestVersion);
                        Dispatcher.Invoke(() => main.ShowUpdateBanner(update));
                    }
                }
                catch (OperationCanceledException) when (_appCts.IsCancellationRequested) { }
                catch (Exception ex) { Log.Warning(ex, "Update check failed"); }
            }, _appCts.Token);

            Log.Information("=== Startup complete ===");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show($"PrintShop Companion could not start.\n\n{ex.Message}\n\nLogs: {logDir}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // ── Global exception handlers (see OnStartup) ────────────────────────

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI-thread exception");
        try
        {
            MessageBox.Show(
                "Something went wrong, but the app kept running.\n\n" +
                e.Exception.Message + "\n\n" +
                "Technical details were written to the log:\n" +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PrintShop", "Companion", "logs"),
                "Unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* never let the handler itself crash the app */ }
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "Unhandled non-UI-thread exception (CLR terminating={IsTerminating})", e.IsTerminating);
        else
            Log.Fatal("Unhandled non-UI-thread exception object: {ExceptionObject}", e.ExceptionObject);
        // Domain-level exceptions on terminating paths cannot be converted to
        // survivable state — but they are now at least captured with a full
        // stack trace in the rolling log for diagnosis.
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception ({Count} inner exception(s))",
            e.Exception.InnerExceptions.Count);
        e.SetObserved(); // observed → the CLR will not tear the process down for it
    }

    /// <summary>
    /// v1 contract §3 + §6 — start the cloud connection: the Socket.IO realtime
    /// client, the 15s heartbeat timer, and the 2-3s command-poll timer. Wire
    /// the realtime client's <c>CommandIssued</c> + <c>Revoked</c> events to
    /// the poller + the UI. Called from the startup Task only when the device
    /// is paired (<c>restored == true</c>); re-pairing through the Login screen
    /// should call this again.
    /// </summary>
    private async Task StartCloudConnectionAsync()
    {
        try
        {
            var realtime = _host.Services.GetRequiredService<SocketIoRealtimeClient>();
            var poller = _host.Services.GetRequiredService<CommandPoller>();
            var backendOpts = _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<BackendOptions>>();

            // Phase F (brief §A) — restart recovery. BEFORE the first tick,
            // recover any commands interrupted by the previous process crash:
            // 'executing' rows → 'outcome_unknown' (re-reported to SaaS; NEVER
            // re-executed); 'claimed' rows → 'received' (re-claimed + re-executed
            // by the next tick, safe because the handler never started). This is
            // the brief's acceptance criterion #1: "Interrupted non-idempotent
            // commands cannot be silently re-executed." A failure here is logged
            // at Error but does NOT abort startup (the poller still runs; the
            // durable rows are intact; a manual recovery is possible).
            try
            {
                var recovered = await poller.RecoverInterruptedAsync(_appCts.Token);
                if (recovered.Count > 0)
                {
                    Log.Warning("Startup recovery: {Count} command(s) transitioned to outcome_unknown (interrupted by previous crash). The shopkeeper must resolve these on the web dashboard.", recovered.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Restart recovery failed — interrupted commands may be in an uncertain state. The poller will continue; investigate manually.");
            }

            // Wire the realtime client's events:
            //  - CommandIssued → immediate poll (don't wait for the next tick).
            //  - Revoked → clear credential + navigate to Login (the device token
            //    is no longer valid; the shopkeeper must re-pair or contact admin).
            //  - ConnectionChanged → could surface on the dashboard (Phase 5 follow-up).
            realtime.CommandIssued += commandType =>
            {
                _ = Task.Run(async () =>
                {
                    try { await poller.TickOnceAsync(_appCts.Token); }
                    catch (Exception ex) { Log.Debug(ex, "Immediate command poll after device.command.issued failed (will retry on next tick)"); }
                });
            };

            // Realtime → shared store: connection state feeds the "Live" pill
            // on Overview/Sync Center; a job.dispatch means the local print
            // queue changed → bump the revision so the Print Queue page
            // refreshes immediately instead of waiting for its poll tick.
            realtime.ConnectionChanged += connected =>
            {
                var store = _host.Services.GetRequiredService<PrintShopCompanion.Core.Services.ShopDataStore>();
                store.SetRealtimeConnected(connected);
            };
            realtime.JobDelivered += job =>
            {
                var store = _host.Services.GetRequiredService<PrintShopCompanion.Core.Services.ShopDataStore>();
                store.BumpQueueRevision();
            };
            realtime.Revoked += () =>
            {
                Log.Warning("Device revoked by the shopkeeper — clearing credential and returning to Login");
                // Phase 0 audit fix: the revoke handler previously ONLY navigated
                // to Login and left the DPAPI credential file on disk — the next
                // start would attempt to restore with a dead token. Clear the
                // persisted credential NOW (revocation invalidates it server-side
                // immediately; keeping a dead token locally is pointless).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var backend = _host.Services.GetRequiredService<IPrintShopBackend>();
                        await backend.RevokeDeviceAsync();
                        Log.Information("Revoked device credential cleared from local storage");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to clear credential after revoke (non-fatal — re-pairing still works)");
                    }
                });
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var nav = _host.Services.GetRequiredService<INavigationService>();
                        nav.NavigateTo("Login");
                    }
                    catch (Exception ex) { Log.Warning(ex, "Navigation to Login after revoke failed"); }
                });
            };

            // Start the realtime client (connects + emits `auth {token}`).
            try
            {
                await realtime.StartAsync(_appCts.Token);
                Log.Information("Socket.IO realtime client started");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Realtime client start failed (non-fatal — REST polling fallback will keep the device online)");
            }

            // v1 contract §4: heartbeat every 15s. The server marks the device
            // offline after 30s without a heartbeat. Use a periodic timer (not
            // Task.Delay) so shutdown cancels it promptly.
            var heartbeatInterval = backendOpts.CurrentValue.HeartbeatInterval;
            if (heartbeatInterval <= TimeSpan.Zero) heartbeatInterval = TimeSpan.FromSeconds(15);
            var backend = _host.Services.GetRequiredService<IPrintShopBackend>();
            _heartbeatTimer = new System.Threading.Timer(
                async _ =>
                {
                    // Phase 5 (B-017): the tray now reflects the REAL connection
                    // state — success ⇒ Connected, auth rejection ⇒ Error, any
                    // other failure or unpaired state ⇒ Offline. Previously the
                    // tray icon was static and lied about connectivity.
                    try
                    {
                        await backend.HeartbeatAsync(_appCts.Token);
                        _tray?.SetState(Tray.TrayIconHost.ConnectionState.Connected);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("Device token rejected", StringComparison.OrdinalIgnoreCase))
                    {
                        _tray?.SetState(Tray.TrayIconHost.ConnectionState.Error);
                        Log.Debug(ex, "Heartbeat rejected — tray set to Error");
                    }
                    catch (Exception ex)
                    {
                        _tray?.SetState(Tray.TrayIconHost.ConnectionState.Offline);
                        Log.Debug(ex, "Heartbeat tick failed (will retry) — tray set to Offline");
                    }
                },
                null,
                dueTime: TimeSpan.Zero,                       // send one immediately on connect
                period: heartbeatInterval);
            Log.Information("Heartbeat timer started (interval={Interval}s)", (int)heartbeatInterval.TotalSeconds);

            // v1 contract §6 + §13: poll commands every PollFallbackInterval (2-3s).
            // The poller's TickOnceAsync is serialized so the realtime CommandIssued
            // event + this timer don't overlap.
            var pollInterval = backendOpts.CurrentValue.PollFallbackInterval;
            if (pollInterval <= TimeSpan.Zero) pollInterval = TimeSpan.FromSeconds(3);
            _commandPollTimer = new System.Threading.Timer(
                async _ =>
                {
                    try { await poller.TickOnceAsync(_appCts.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log.Debug(ex, "Command poll tick failed (will retry)"); }
                },
                null,
                dueTime: TimeSpan.FromSeconds(1),           // first poll 1s after connect
                period: pollInterval);
            Log.Information("Command poll timer started (interval={Interval}s)", (int)pollInterval.TotalSeconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "StartCloudConnectionAsync failed (non-fatal — local queue still operates)");
        }
    }

    /// <summary>
    /// Token-rotation policy (integration spec §8.3 — "do not rotate on every
    /// heartbeat"). A credential whose file was last written more than 30 days
    /// ago is rotated once at startup, AFTER the cloud connection is up so the
    /// overlap window never matters for the first heartbeat. Failures are
    /// logged and retried on the next launch — a failed rotation must not
    /// lock out a healthy device, and the old token stays valid for the
    /// server-side 10-minute overlap regardless.
    /// </summary>
    private static readonly TimeSpan CredentialRotationAge = TimeSpan.FromDays(30);

    private async Task TryRotateStaleCredentialAsync()
    {
        try
        {
            var layout = _host!.Services.GetRequiredService<PrintShopCompanion.Infrastructure.Storage.LocalStorageLayout>();
            var credPath = layout.DeviceTokenPath;
            if (!File.Exists(credPath)) return;

            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(credPath);
            if (age < CredentialRotationAge) return;

            Log.Information("Device credential is {Days:F0} days old — rotating it (POST /api/v1/companion/token/rotate)", age.TotalDays);
            var backend = _host.Services.GetRequiredService<IPrintShopBackend>();
            var result = await backend.RotateDeviceTokenAsync();
            Log.Information("Device token rotated successfully (old token accepted until {OverlapExpiresAt})",
                result.OverlapExpiresAt?.ToString("u") ?? "(server did not specify)");
        }
        catch (OperationCanceledException) when (_appCts!.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Credential rotation failed (non-fatal — the previous token remains valid; rotation retries on next launch)");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== Shutdown starting ===");

        // 1. Release mutex FIRST so another instance can launch immediately
        //    while we clean up in the background.
        try { _singleInstance?.Dispose(); Log.Information("Mutex released"); } catch { }

        // 2. Signal ALL background work to stop
        try { _appCts?.Cancel(); Log.Information("Cancellation requested"); } catch { }

        // 2b. Dispose the v1 cloud-connection timers (heartbeat + command poll).
        // Disposing after Cancel so an in-flight tick doesn't fire after dispose.
        try { _heartbeatTimer?.Dispose(); _heartbeatTimer = null; } catch { }
        try { _commandPollTimer?.Dispose(); _commandPollTimer = null; } catch { }

        // 3. Wait for the background startup task + update check task with a
        //    BOUNDED total shutdown of 5 seconds. We don't want a hung task
        //    to keep the process alive forever, but we also don't want to
        //    cut off a SQLite write mid-flight.
        var shutdownDeadline = TimeSpan.FromSeconds(5);
        try
        {
            var tasks = new List<Task>();
            if (_startupTask is not null) tasks.Add(_startupTask);
            if (_updateCheckTask is not null) tasks.Add(_updateCheckTask);

            if (tasks.Count > 0)
            {
                var allTask = Task.WhenAll(tasks);
                var winner = Task.WhenAny(allTask, Task.Delay(shutdownDeadline)).Result;
                if (winner != allTask)
                    Log.Warning("Background tasks did not finish within {Seconds}s — proceeding with shutdown",
                        shutdownDeadline.TotalSeconds);
                else
                    Log.Information("Background tasks finished cleanly");
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Background task wait failed (non-fatal)"); }

        // 4. Stop the JobProcessor and SyncEngine (now they should already
        //    be cancelled via the shared token; this call disposes their CTSes).
        if (_host != null)
        {
            try
            {
                var proc = _host.Services.GetRequiredService<JobProcessor>();
                proc.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            try
            {
                var sync = _host.Services.GetRequiredService<SyncEngine>();
                sync.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            // v0.5: Stop the FileRetentionManager (dispose its timer).
            try
            {
                var retention = _host.Services.GetRequiredService<FileRetentionManager>();
                retention.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            // 5. Stop and dispose the DI host.
            try { _host.StopAsync(TimeSpan.FromSeconds(2)).Wait(); _host.Dispose(); }
            catch { }
        }

        // 6. Dispose the shared CTS.
        try { _appCts?.Dispose(); } catch { }

        // 7. Hide the tray icon.
        try { _tray?.Hide(); } catch { }

        Log.Information("=== Shutdown complete ===");
        Log.CloseAndFlush();
        base.OnExit(e);

        // 8. Final safety net: if the process is still alive 3 seconds after
        //    OnExit returns (because some background thread is hung), force
        //    the process to exit. This is the LAST line of defense against
        //    the "cannot reopen after closing" bug — if the process is still
        //    alive, the single-instance mutex stays held and the user can't
        //    reopen. A forced Environment.Exit() releases all OS resources
        //    including the mutex.
        //
        //    We schedule the kill on a background thread so the current
        //    thread can finish flushing the log file before the process dies.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            // If we're still alive, something is hung. Force exit.
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch { }
        });
    }

    private static void RegisterAutoStart()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            Directory.CreateDirectory(dir);
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut(\"{Path.Combine(dir, "PrintShop Companion.lnk")}\");$s.TargetPath=\"{exe}\";$s.Save()";
            System.Diagnostics.Process.Start("powershell.exe", $"-NoProfile -Command \"{script}\"")?.WaitForExit(3000);
        }
        catch { }
    }

    private static void UnregisterAutoStart()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "PrintShop Companion.lnk");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }
}
