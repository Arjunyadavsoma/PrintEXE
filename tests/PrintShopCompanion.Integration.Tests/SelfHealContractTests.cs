namespace PrintShopCompanion.Integration.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// v0.7.18 self-heal contract tests. Field incident (v0.7.14 runtime log): a
/// revoked device token produced an ENDLESS 401 loop — heartbeat every 15s +
/// command poll every 3s + Socket.IO token exchange, all logging 401 forever,
/// because the app treated a valid shopkeeper session as a valid device and
/// nothing distinguished "credential rejected" from "network down".
///
/// The fixes under test:
///   1. A 401 from a device-authed endpoint throws the TYPED
///      DeviceTokenRejectedException (still an InvalidOperationException so
///      existing catches keep working) — HttpPrintShopBackend.IsDeviceTokenRejection
///      classifies it TRUE even through the wrapper exception.
///   2. Network failures classify FALSE — re-pairing on a network hiccup
///      would mint throwaway devices.
///   3. DeviceDisabledException (403 — admin suspension) classifies FALSE:
///      an explicit shopkeeper decision must not be silently overridden.
///   4. The self-heal wire flow (disable stale device row → register fresh
///      token → pair-validate) works against the production contract shapes.
/// </summary>
public sealed class SelfHealContractTests : IDisposable
{
    private readonly FakePrintelyxBackend _backend;
    private readonly string _root;

    public SelfHealContractTests()
    {
        _backend = new FakePrintelyxBackend();
        _root = Path.Combine(Path.GetTempPath(), "psh-selfheal-" + Guid.NewGuid().ToString("N"));
    }

    private HttpPrintShopBackend NewPrintClient(string? baseUrl = null)
    {
        var url = baseUrl ?? _backend.BaseUrl;
        var layout = new LocalStorageLayout(
            Options.Create(new StorageOptions
            {
                DeviceTokenPath = Path.Combine(_root, "Security", "device-token.dat")
            }),
            rootOverride: _root);
        return new HttpPrintShopBackend(
            new HttpClient { BaseAddress = new Uri(url) },
            new StubMonitor(new BackendOptions { BaseUrl = url }),
            NullLogger<HttpPrintShopBackend>.Instance,
            layout);
    }

    private static HttpShopBackend NewShopClient(string baseUrl)
    {
        return new HttpShopBackend(
            new HttpClient { BaseAddress = new Uri(baseUrl) },
            new StubMonitor(new BackendOptions { BaseUrl = baseUrl }),
            NullLogger<HttpShopBackend>.Instance);
    }

    private sealed class StubMonitor : IOptionsMonitor<BackendOptions>
    {
        private readonly BackendOptions _value;
        public StubMonitor(BackendOptions value) => _value = value;
        public BackendOptions CurrentValue => _value;
        public BackendOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<BackendOptions, string?> listener) => null;
    }

    // ── 1. Typed rejection through the wrapper ─────────────────────────

    [Fact]
    public async Task Rejected_device_token_wraps_typed_rejection_and_classifies_true()
    {
        using var client = NewPrintClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PairWithDeviceTokenAsync("wrong-token"));

        // The wrapper preserves the typed cause — the startup self-heal keys on it.
        Assert.True(
            HttpPrintShopBackend.IsDeviceTokenRejection(ex),
            $"expected the chain to contain DeviceTokenRejectedException, got: {ex.GetType().Name} -> {ex.InnerException?.GetType().Name}");
        Assert.False(client.IsAuthenticated, "a rejected token must never be kept (fail-closed)");
    }

    // ── 2. Network failure must NOT look like a rejection ──────────────

    [Fact]
    public async Task Network_failure_classifies_false()
    {
        // Port 1 on loopback: nothing listens there; the TCP connect fails fast.
        using var client = NewPrintClient("http://127.0.0.1:1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken));

        Assert.False(
            HttpPrintShopBackend.IsDeviceTokenRejection(ex),
            "a network outage must not trigger auto re-pairing (it would mint throwaway devices)");
    }

    // ── 3. Disabled (403) is an admin decision, not a rejection ────────

    [Fact]
    public void DeviceDisabledException_classifies_false()
    {
        Assert.False(HttpPrintShopBackend.IsDeviceTokenRejection(
            new PrintShopCompanion.Core.Contracts.DeviceDisabledException("DEVICE_DISABLED")));
    }

    [Fact]
    public void Plain_errors_classify_false()
    {
        Assert.False(HttpPrintShopBackend.IsDeviceTokenRejection(new HttpRequestException("socket refused")));
        Assert.False(HttpPrintShopBackend.IsDeviceTokenRejection(new InvalidOperationException("empty response")));
        Assert.False(HttpPrintShopBackend.IsDeviceTokenRejection(null));
    }

    // ── 4. RestoreAsync flag: rejection vs. nothing-saved ──────────────

    [Fact]
    public async Task RestoreAsync_without_saved_token_returns_false_and_no_rejection()
    {
        using var client = NewPrintClient();

        var restored = await client.RestoreAsync();

        Assert.False(restored);
        Assert.False(client.LastRestoreWasAuthRejection,
            "no token file → nothing was rejected → auto re-pair must not run");
    }

    // Phase 7 (Stage 4): guard became a SKIP — an unexecuted test must never
    // show up as "passed" in the neutral net10.0 leg.
    [SkippableFact]
    public async Task RestoreAsync_with_rejected_saved_token_sets_rejection_flag()
    {
        // DPAPI is Windows-only; on Linux the Unprotect would throw
        // PlatformNotSupportedException, which classifies as NOT a rejection —
        // so the meaningful branch of this test only exists on Windows CI.
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only; executed on windows-latest CI");

        var root = Path.Combine(_root, "win-only");
        var layout = new LocalStorageLayout(
            Options.Create(new StorageOptions
            {
                DeviceTokenPath = Path.Combine(root, "Security", "device-token.dat")
            }),
            rootOverride: root);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.DeviceTokenPath)!);
        var garbageToken = System.Text.Encoding.UTF8.GetBytes("a-revoked-device-token");
        await File.WriteAllBytesAsync(layout.DeviceTokenPath,
            System.Security.Cryptography.ProtectedData.Protect(garbageToken, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

        using var client = new HttpPrintShopBackend(
            new HttpClient { BaseAddress = new Uri(_backend.BaseUrl) },
            new StubMonitor(new BackendOptions { BaseUrl = _backend.BaseUrl }),
            NullLogger<HttpPrintShopBackend>.Instance,
            layout);

        var restored = await client.RestoreAsync();

        Assert.False(restored, "a revoked token must not restore");
        Assert.True(client.LastRestoreWasAuthRejection,
            "confirmed 401 → the App's startup self-heal is allowed to re-pair");
    }

    // ── 5. The self-heal wire flow end-to-end ──────────────────────────

    [Fact]
    public async Task SelfHeal_wire_flow_disable_stale_register_pair_validate()
    {
        // A dedicated fake implementing the shopkeeper-authed contract the
        // self-heal uses (auth/login, shops/me/agents, PATCH shops/me/devices/{id},
        // agents/register) plus the device-authed heartbeat for validation.
        using var saas = new FakeShopSaas();
        var shop = NewShopClient(saas.BaseUrl);
        var print = NewPrintClient(saas.BaseUrl);

        await shop.LoginAsync("owner@shop.test", "pw");

        // The self-heal lists devices, disables THIS machine's stale row,
        // registers a fresh token, and validates it via heartbeat.
        var devices = await shop.ListDevicesAsync();
        var stale = Assert.Single(devices, d => d.Name == "DESKTOP-STALE" && !d.IsOnline);

        await shop.SetDeviceEnabledAsync(stale.DeviceId, isEnabled: false);
        Assert.True(saas.DisabledDeviceIds.Contains("agent-stale-1"),
            "SetDeviceEnabledAsync must PATCH /shops/me/devices/{id} with {isEnabled:false}");

        var token = await shop.RegisterDeviceAsync(Environment.MachineName, os: "win32", version: "test");
        Assert.False(string.IsNullOrWhiteSpace(token.Token));

        await print.PairWithDeviceTokenAsync(token.Token);
        Assert.True(print.IsAuthenticated, "the fresh token must pass heartbeat validation");
        Assert.Equal("agent-new-1", print.ServerAgentId);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Minimal fake of the shopkeeper-facing SaaS routes the self-heal uses.
    /// Shapes mirror the production zod contracts (envelope, {isEnabled} body,
    /// token returned once from register).
    /// </summary>
    private sealed class FakeShopSaas : IDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; private set; } = "";
        public HashSet<string> DisabledDeviceIds { get; } = new();

        public FakeShopSaas()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();

            const string shopkeeperJwt = "shopkeeper-jwt-unit-test";
            static IResult Envelope<T>(T data) => Results.Json(
                new { success = true, data });
            static IResult Err(int code, string err, string msg) => Results.Json(
                new { success = false, error = new { code = err, message = msg } }, statusCode: code);
            bool ShopAuthOk(HttpRequest req) =>
                req.Headers.Authorization.ToString() == $"Bearer {shopkeeperJwt}";

            _app.MapPost("/api/v1/auth/login", () => Envelope(new
            {
                accessToken = shopkeeperJwt,
                refreshToken = "refresh-unit-test",
                expiresIn = 900L,
                user = new { id = "user-1", email = "owner@shop.test", role = "owner", shopId = "shop-1" },
            }));

            _app.MapGet("/api/v1/shops/me/agents", (HttpRequest req) =>
            {
                if (!ShopAuthOk(req)) return Err(401, "UNAUTHORIZED", "No session");
                return Envelope(new[]
                {
                    new { id = "agent-stale-1", name = "DESKTOP-STALE", isOnline = false,
                          lastSeenAt = (System.DateTimeOffset?)null, printerName = (string?)null,
                          version = (string?)null, os = (string?)"win32" },
                });
            });

            // Body schema from the production route: { name?, isEnabled? },
            // at least one required. The client sends {isEnabled: false}.
            _app.MapPatch("/api/v1/shops/me/devices/{deviceId}", async (HttpRequest req, string deviceId) =>
            {
                if (!ShopAuthOk(req)) return Err(401, "UNAUTHORIZED", "No session");
                using var sr = new StreamReader(req.Body);
                var raw = await sr.ReadToEndAsync();
                if (!raw.Contains("\"isEnabled\":false", StringComparison.Ordinal))
                    return Err(400, "VALIDATION_ERROR", "expected {\"isEnabled\":false} in the PATCH body");
                DisabledDeviceIds.Add(deviceId);
                return Envelope(new
                {
                    device = new { id = deviceId, name = "DESKTOP-STALE", isOnline = false,
                                   isEnabled = false, lastSeenAt = (System.DateTimeOffset?)null,
                                   printerName = (string?)null, version = (string?)null,
                                   os = (string?)"win32", createdAt = "", updatedAt = "" },
                    commands = Array.Empty<object>(),
                });
            });

            _app.MapPost("/api/v1/agents/register", (HttpRequest req) =>
            {
                if (!ShopAuthOk(req)) return Err(401, "UNAUTHORIZED", "No session");
                return Envelope(new
                {
                    agentId = "agent-new-1",
                    deviceToken = "fresh-device-token-unit-test",
                    shopId = "shop-1",
                    shopName = "Test Shop",
                    warning = "Store this deviceToken securely — it is shown only once.",
                });
            });

            _app.MapPost("/api/v1/agents/heartbeat", (HttpRequest req) =>
            {
                if (req.Headers.Authorization.ToString() != "Bearer fresh-device-token-unit-test")
                    return Err(401, "UNAUTHORIZED", "Device token rejected");
                return Envelope(new { agentId = "agent-new-1", online = true, ts = System.DateTimeOffset.UtcNow });
            });

            _app.StartAsync().GetAwaiter().GetResult();
            BaseUrl = _app.Urls.First(u => u.StartsWith("http://"));
        }

        public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();
    }
}
