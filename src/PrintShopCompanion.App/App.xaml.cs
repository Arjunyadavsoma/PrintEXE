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

    // Phase 7 (CI startup verification): set by the `--ci-smoke` argument.
    // The app then runs the REAL startup path (real DI host, real MainWindow,
    // real XAML resource resolution) but (a) logs structured markers the CI
    // gate asserts on, (b) writes ci-smoke-result.txt next to the Serilog
    // logs, (c) NEVER opens a modal dialog — the exact mechanism that let
    // v0.7.15-v0.7.17 pass the old liveness-only smoke test while dead — and
    // (d) shuts itself down gracefully once MainWindow completes Loaded.
    private bool _ciSmoke;

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
            // Phase 7: dedicated CI startup-verification mode. Detected before
            // the Serilog init so fatal pre-log failures still exit without a
            // modal dialog blocking the runner.
            if (Array.IndexOf(e.Args, "--ci-smoke") >= 0)
            {
                _ciSmoke = true;
            }
        }

        // Serilog
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Decision D-06: the token redactor is now actually wired (Phase 0
            // audit found the enricher existed but was never attached). Device
            // tokens / secrets must never reach any log file.
            .Enrich.With(new PrintShopCompanion.Infrastructure.Logging.DeviceTokenRedactor())
            .WriteTo.File(Path.Combine(logDir, "companion-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
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
                    services.AddTransient<DiagnosticsViewModel>();
                    services.AddTransient<LoginView>();
                    services.AddTransient<OverviewView>();
                    services.AddTransient<OrdersView>();
                    services.AddTransient<PrintersView>();
                    services.AddTransient<PrintQueueView>();
                    services.AddTransient<SyncCenterView>();
                    services.AddTransient<SettingsView>();
                    services.AddTransient<OrderDetailsView>();
                    services.AddTransient<DiagnosticsView>();
                    services.AddSingleton<TrayIconHost>();
                })
                .Build();

            // StartAsync is synchronous-safe (it doesn't use the Dispatcher)
            _host.StartAsync().Wait();
            Log.Information("DI host started");
            // Phase 7: structured marker — the full DI graph was constructed
            // AND started. A failure anywhere in AddPrintShopCompanionInfrastructure
            // throws before this line and lands in the startup catch below.
            if (_ciSmoke) Log.Information("=== CI-SMOKE: DI HOST BUILT ===");

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
            if (_ciSmoke)
            {
                // Phase 7: assert the REAL MainWindow completes the WPF Loaded
                // lifecycle (visual tree built, merged dictionaries applied,
                // bindings evaluated) — the layer markup compilation cannot
                // validate and where both field crashes (v0.7.15 NavItem,
                // v0.7.19 InputField) died. On success the handler shuts the
                // app down gracefully; CI never has to kill a healthy app.
                main.Loaded += OnCiSmokeMainWindowLoaded;
            }

            // ALL ASYNC WORK happens AFTER the window is shown.
            // We capture the Task so OnExit can WaitAsync for it before disposing.
            _startupTask = Task.Run(async () =>
            {
                try
                {
                    // Restore credentials — try BOTH paths:
                    //   1. The v0.4.7+ device-token flow (HttpPrintShopBackend.RestoreAsync)
                    //   2. The legacy shopkeeper session flow (HttpShopBackend.RestoreSession)
                    //   3. (v0.7.18) Self-heal: if the shopkeeper session is valid but
                    //      the device token was REJECTED by the backend (401 — dead,
                    //      not merely unreachable), re-pair automatically with the
                    //      session's authority: free this machine's stale device slot,
                    //      register a fresh token, validate + persist it.
                    //
                    // Two independent flags — the endless-401 lesson: a valid
                    // shopkeeper session does NOT make the device token valid, and
                    // starting the device-token cloud services (heartbeat 15s +
                    // command poll 3s + Socket.IO) with a dead token just spams the
                    // backend with 401s forever (v0.7.14 field log: 20+ requests in
                    // 30 seconds). Cloud services start ONLY when devicePaired.
                    bool devicePaired = false;
                    bool sessionRestored = false;
                    var printShopBackend = _host.Services.GetRequiredService<IPrintShopBackend>() as HttpPrintShopBackend;

                    // Path 1: device-token restore (the recommended v0.4.7+ flow).
                    if (hasSavedCredentials)
                    {
                        try
                        {
                            if (printShopBackend != null)
                            {
                                devicePaired = await printShopBackend.RestoreAsync();
                                if (devicePaired)
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
                    if (!devicePaired && hasSavedCredentials)
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
                                    sessionRestored = true;
                                    Log.Information("Legacy shopkeeper session restored in background");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Legacy shopkeeper session restore failed");
                        }
                    }

                    // Path 3 (v0.7.18): self-heal auto re-pair. ONLY on a confirmed
                    // 401 rejection — a network outage must never trigger re-pairing
                    // (it would mint throwaway devices and race the entitlement
                    // limits). A 403 DEVICE_DISABLED is also excluded inside
                    // LastRestoreWasAuthRejection: an admin-disable is an explicit
                    // shopkeeper decision and must not be silently overridden.
                    if (!devicePaired && sessionRestored && printShopBackend is { LastRestoreWasAuthRejection: true })
                    {
                        devicePaired = await TryAutoRepairPairingAsync();
                    }

                    if (devicePaired)
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
                    else if (sessionRestored)
                    {
                        // v0.7.18: the shopkeeper session is valid, but there is NO
                        // working device token (restore failed AND auto re-pair did
                        // not succeed). Enter the app — shop data still syncs over
                        // the shopkeeper JWT — but do NOT start the device-token
                        // cloud services. This replaces the old behavior of starting
                        // everything whenever "restored" was true (which treated a
                        // valid session as a valid device and produced the endless
                        // 401 loop). The UI stays honest: no live connection until
                        // the device is paired again.
                        Dispatcher.Invoke(() =>
                        {
                            restoreState.Succeeded();
                            restoreState.Reset();
                            nav.NavigateTo("Overview");
                        });
                        Log.Warning(
                            "Auto sign-in succeeded, but the device is NOT paired — heartbeat/command-poll/Socket.IO stay stopped until re-pairing. Pair this PC from the web dashboard's Printer Setup page.");
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

                    // Start LOCAL background services — pass the shared token so
                    // they all stop together on shutdown. ONLY after credentials
                    // are verified: the previous code started the sync/print
                    // pipeline unconditionally, so a device with a dead token kept
                    // firing authenticated requests (and showed stale UI activity)
                    // before the restore verdict was in.
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

                    if (!devicePaired)
                    {
                        // v0.7.18: session-only sign-in. The device-token cloud
                        // connection (Socket.IO + heartbeat + command poll) requires
                        // a valid device token — starting it here is exactly the
                        // endless-401 bug. Local print queue + shopkeeper-JWT sync
                        // keep working; the user re-pairs from the dashboard.
                        return;
                    }

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
                            if (!_ciSmoke)
                            {
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
            if (_ciSmoke)
            {
                // Phase 7: in CI mode a fatal startup must NEVER open a modal
                // MessageBox — the dialog blocks the unattended runner forever
                // and a liveness-only smoke test reads it as "pass" (the exact
                // v0.7.15-v0.7.17 release incident). Log + result file + hard
                // exit instead; the CI gate treats exit 1 as failure.
                WriteCiSmokeResult("FAIL", "Startup failed: " + ex);
                Environment.Exit(1);
            }
            MessageBox.Show($"PrintShop Companion could not start.\n\n{ex.Message}\n\nLogs: {logDir}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // ── Global exception handlers (see OnStartup) ────────────────────────

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI-thread exception");
        if (_ciSmoke)
        {
            // Phase 7: an unhandled UI-thread exception during a CI smoke run
            // is fatal by definition (a Loaded-handler or binding crash killed
            // v0.7.15 in the field). Exit hard — no dialog, e.Handled stays
            // false — so the gate cannot confuse "survived with dialog" with
            // "healthy startup".
            Log.Fatal(e.Exception, "=== CI-SMOKE: FATAL (unhandled UI-thread exception) ===");
            WriteCiSmokeResult("FAIL", "Unhandled UI-thread exception: " + e.Exception);
            Environment.Exit(2);
        }
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
        // Phase 7: record the failure for the CI gate even if the process is
        // already terminating — the marker file is the runner-independent
        // diagnostic the workflow uploads on every run.
        if (_ciSmoke && e.ExceptionObject is Exception ciEx)
            WriteCiSmokeResult("FAIL", "Unhandled non-UI-thread exception: " + ciEx);
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

    // ── Phase 7: CI startup verification (--ci-smoke) ────────────────────

    private void OnCiSmokeMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        // The REAL MainWindow has completed the WPF Loaded lifecycle: the
        // visual tree is built, the merged dictionaries applied, bindings
        // evaluated. This is the strongest startup signal an unattended
        // runner can get — the v0.7.15 (NavItem) and v0.7.19 (InputField)
        // defect classes both died before this point, inside XAML resource
        // resolution that markup compilation cannot validate.
        Log.Information("=== CI-SMOKE: MAINWINDOW LOADED ===");
        WriteCiSmokeResult("PASS", "MainWindow Loaded lifecycle completed; DI host started");
        // Auto-shutdown after a successful smoke run — the graceful OnExit
        // path (host dispose, timers, tray) is exercised as part of the test,
        // and CI never has to kill a healthy process.
        Dispatcher.BeginInvoke(new Action(() => Shutdown(0)));
    }

    private void WriteCiSmokeResult(string status, string detail)
    {
        // Semantics (Phase 7.1 review, L1): single result file, LAST WRITER
        // WINS, deliberately fail-closed — a startup that logged PASS but
        // crashed later (e.g. during OnExit) gets overwritten with FAIL by
        // OnDomainUnhandledException, and the CI gate requires status=PASS
        // plus exit 0, so no late failure can ever be reported as success.
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrintShop", "Companion", "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(
                Path.Combine(logDir, "ci-smoke-result.txt"),
                $"status={status}\nutc={DateTime.UtcNow:O}\nversion={AppVersion.Short}\n{detail}\n");
        }
        catch { /* diagnostics only — never crash the smoke path itself */ }
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
                    try { await backend.HeartbeatAsync(_appCts.Token); }
                    catch (OperationCanceledException) { }
                    // v0.7.18 circuit breaker: a 401 mid-session (token revoked /
                    // DB reset) can NEVER succeed on retry. Previously this was a
                    // generic catch-and-debug-log, so the timer kept firing every
                    // 15s forever — the field log showed 401 spam every few
                    // seconds. Trip the breaker: stop timers, clear credential,
                    // return to the pairing state.
                    catch (DeviceTokenRejectedException ex) { HandleDeviceCredentialRejected(ex, "heartbeat"); }
                    catch (DeviceDisabledException ex) { HandleDeviceDisabled(ex, "heartbeat"); }
                    catch (Exception ex) { Log.Debug(ex, "Heartbeat tick failed (will retry)"); }
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
                    catch (DeviceTokenRejectedException ex) { HandleDeviceCredentialRejected(ex, "command poll"); }
                    catch (DeviceDisabledException ex) { HandleDeviceDisabled(ex, "command poll"); }
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

    // ── v0.7.18: device-credential self-heal + circuit breaker ───────────

    /// <summary>
    /// Idempotency guard for the rejection handlers — the heartbeat (15s) and
    /// command-poll (3s) timers can both trip in the same second; the breaker
    /// must fire once, not once per tick.
    /// </summary>
    private int _credentialRejectionHandled;

    /// <summary>
    /// v0.7.18 self-heal ("sync always"): the saved device token was rejected
    /// by the backend (confirmed 401 — <see cref="HttpPrintShopBackend.LastRestoreWasAuthRejection"/>),
    /// but the shopkeeper session is valid. Re-pair automatically instead of
    /// leaving the device dead and spamming 401s:
    ///   1. Disable this machine's stale device row(s) via the shopkeeper
    ///      session — free-tier shops are limited to ONE enabled device, and
    ///      a dead-but-enabled row makes agents/register fail with
    ///      ENTITLEMENT_REQUIRED. Only offline rows are touched: a row that
    ///      is actively heartbeating belongs to a healthy device (possibly a
    ///      different PC with the same hostname) and is left alone.
    ///   2. Register a fresh device token (POST /api/v1/agents/register,
    ///      shopkeeper JWT).
    ///   3. Validate + persist it through the production pairing path
    ///      (<see cref="HttpPrintShopBackend.PairWithDeviceTokenAsync"/> —
    ///      heartbeat validation + DPAPI atomic persist).
    /// Any failure leaves the device cleanly UNPAIRED (the caller then skips
    /// the cloud services) — never a half-paired state.
    /// </summary>
    private async Task<bool> TryAutoRepairPairingAsync()
    {
        if (_appCts is { IsCancellationRequested: true }) return false;
        try
        {
            var shopBackend = _host?.Services.GetService<IShopBackend>() as HttpShopBackend;
            if (shopBackend is null || !shopBackend.IsAuthenticated)
            {
                Log.Information("Auto re-pair skipped: no valid shopkeeper session");
                return false;
            }
            if (_host?.Services.GetService<IPrintShopBackend>() is not HttpPrintShopBackend printBackend)
            {
                return false;
            }

            Log.Warning("Device token was rejected by the backend — attempting automatic re-pairing with the shopkeeper session");

            // 1. Free the device slot: disable THIS machine's stale rows.
            try
            {
                var devices = await shopBackend.ListDevicesAsync();
                foreach (var stale in devices)
                {
                    if (!string.Equals(stale.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (stale.IsOnline)
                    {
                        Log.Information(
                            "Auto re-pair: device row {DeviceId} reports online — leaving it alone (belongs to a healthy device)",
                            stale.DeviceId);
                        continue;
                    }
                    if (stale.DeviceId is not { Length: > 0 })
                        continue;
                    Log.Information("Auto re-pair: disabling stale offline row {DeviceId} ({Name}) to free the device slot",
                        stale.DeviceId, stale.Name);
                    try { await shopBackend.SetDeviceEnabledAsync(stale.DeviceId, isEnabled: false); }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Auto re-pair: could not disable stale row {DeviceId} — registration may fail with ENTITLEMENT_REQUIRED", stale.DeviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto re-pair: device list fetch failed (continuing — registration may still succeed)");
            }

            // 2. Mint a fresh device token with the shopkeeper session.
            var token = await shopBackend.RegisterDeviceAsync(
                Environment.MachineName,
                os: "win32",
                version: AppVersion.Short);
            Log.Information("Auto re-pair: new device token minted (agent={AgentId})", token.AgentId);

            // 3. Validate against the backend + DPAPI-persist (production path).
            await printBackend.PairWithDeviceTokenAsync(token.Token);
            Log.Information("Auto re-pair succeeded — device re-paired and validated");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "Auto re-pair failed — the device stays unpaired. Pair this PC again from the web dashboard's Printer Setup page (generate a pairing code or paste a new device token).");
            return false;
        }
    }

    /// <summary>
    /// v0.7.18 circuit breaker for 401 rejections DURING a running session
    /// (revoked token, backend DB reset, rotated-away credential). The old
    /// behavior — debug-log and retry forever — produced the endless 401
    /// loop. Now: stop the timers, clear the dead credential (which also
    /// silences the Socket.IO HTTP fallback poller via its IsAuthenticated
    /// guard and SyncEngine's device-token paths), and return to the pairing
    /// state on the UI thread.
    /// </summary>
    private void HandleDeviceCredentialRejected(Exception ex, string source)
    {
        if (System.Threading.Interlocked.Exchange(ref _credentialRejectionHandled, 1) == 1) return;
        Log.Warning(ex,
            "Device credential REJECTED by backend ({Source}) — circuit breaker: stopping heartbeat + command poll, clearing credential, returning to pairing state",
            source);

        DisposeCloudTimers();

        _ = Task.Run(async () =>
        {
            try
            {
                var backend = _host?.Services.GetService<IPrintShopBackend>();
                if (backend is not null) await backend.RevokeDeviceAsync();
                Log.Information("Rejected device credential cleared from local storage");
            }
            catch (Exception e) { Log.Warning(e, "Failed to clear rejected credential (non-fatal)"); }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var nav = _host?.Services.GetService<INavigationService>();
                        nav?.NavigateTo("Login");
                    }
                    catch (Exception e) { Log.Warning(e, "Navigation to Login after credential rejection failed"); }
                });
            }
            catch { /* shutting down */ }
        });
    }

    /// <summary>
    /// v0.7.18 circuit breaker for 403 DEVICE_DISABLED mid-session: the
    /// shopkeeper (or admin) suspended this PC from the dashboard. Per the
    /// v1 contract the credential is KEPT (re-enabling restores the device)
    /// but all polling stops — no 403 spam. The Login screen explains why.
    /// </summary>
    private void HandleDeviceDisabled(DeviceDisabledException ex, string source)
    {
        if (System.Threading.Interlocked.Exchange(ref _credentialRejectionHandled, 1) == 1) return;
        Log.Warning(ex,
            "Device DISABLED by the shop administrator ({Source}) — stopping heartbeat + command poll. The credential is kept; ask the shopkeeper to re-enable this device from /shop/devices.",
            source);

        DisposeCloudTimers();

        try
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var nav = _host?.Services.GetService<INavigationService>();
                    nav?.NavigateTo("Login");
                }
                catch (Exception e) { Log.Warning(e, "Navigation to Login after device-disable failed"); }
            });
        }
        catch { /* shutting down */ }
    }

    /// <summary>v0.7.18: shared timer disposal for the circuit breaker + OnExit.</summary>
    private void DisposeCloudTimers()
    {
        try { _heartbeatTimer?.Dispose(); } catch { }
        _heartbeatTimer = null;
        try { _commandPollTimer?.Dispose(); } catch { }
        _commandPollTimer = null;
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
