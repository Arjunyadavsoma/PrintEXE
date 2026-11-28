using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>
/// Phase 2 §3/§8 — Windows-runtime security and lifecycle validation.
///
/// These tests COMPILE on every OS (Directory.Build.props sets
/// EnableWindowsTargeting). Tests that exercise Windows-only APIs (DPAPI)
/// guard with OperatingSystem.IsWindows() so they no-op on Linux and run for
/// real on the windows-latest CI workflow. SQLite and installer-contract
/// tests are genuinely cross-platform and run locally too.
/// </summary>
public sealed class WindowsRuntimeSecurityTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psh-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LocalStorageLayout NewLayout(string root) =>
        new(Options.Create(new StorageOptions()), root);

    private static HttpPrintShopBackend NewBackend(HttpClient http, LocalStorageLayout layout, PrinterOptions? printer = null) =>
        new(http,
            new StubOptionsMonitor<BackendOptions>(new BackendOptions { BaseUrl = "http://unit.test" }),
            NullLogger<HttpPrintShopBackend>.Instance,
            layout,
            new StubOptionsMonitor<PrinterOptions>(printer ?? new PrinterOptions()));

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StubOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>HTTP stub that serves canned envelopes and records requests.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public readonly List<(string Path, string? Auth)> Requests = new();

        public void On(string pathContains, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _routes[pathContains] = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add((req.RequestUri?.AbsolutePath ?? "", req.Headers.Authorization?.ToString()));
            foreach (var (contains, responder) in _routes)
            {
                if ((req.RequestUri?.AbsolutePath ?? "").Contains(contains, StringComparison.Ordinal))
                    return Task.FromResult(responder(req));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { success = false, error = new { code = "NOT_STUBBED", message = "no route" } }),
            });
        }
    }

    private static HttpResponseMessage OkEnvelope(object data) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { success = true, data }),
    };

    // ── 1. DPAPI credential round trip (Windows-only) ──────────────────
    // Phase 7 (Stage 4): guards became SKIPS — an unexecuted test must never
    // show up as "passed" in the neutral net10.0 leg.
    [SkippableFact]
    public async Task DPAPI_Credential_Persist_Restore_RoundTrip()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only; executed on windows-latest CI");
        var root = NewTempDir();
        var layout = NewLayout(root);
        var handler = new StubHandler();
        handler.On("/heartbeat", _ => OkEnvelope(new { online = true, jobsCount = 0 }));
        var backend = NewBackend(new HttpClient(handler), layout);

        var token = "a".Repeat(64);
        await backend.PersistDeviceTokenAsync(token, CancellationToken.None);
        Assert.True(File.Exists(layout.DeviceTokenPath), "credential file must exist after persist");

        // A FRESH instance (what the transient typed client resolves) restores lazily.
        var handler2 = new StubHandler();
        handler2.On("/heartbeat", _ => OkEnvelope(new { online = true, jobsCount = 0 }));
        var backend2 = NewBackend(new HttpClient(handler2), layout);
        var restored = await backend2.RestoreAsync(CancellationToken.None);
        Assert.True(restored);
        await backend2.HeartbeatAsync(CancellationToken.None, printerName: "HP Unit");
        Assert.Equal($"Bearer {token}", handler2.Requests.Last().Auth);
    }

    // ── 2. Rotation persists the new credential (Windows-only) ─────────
    [SkippableFact]
    public async Task Rotation_Persists_New_Credential_And_Uses_It()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only; executed on windows-latest CI");
        var root = NewTempDir();
        var layout = NewLayout(root);
        var handler = new StubHandler();
        var oldToken = "b".Repeat(64);
        var newToken = "c".Repeat(64);
        handler.On("/companion/token/rotate", _ => OkEnvelope(new
        {
            agentId = "agent-1",
            deviceToken = newToken,
            rotatedAt = DateTimeOffset.UtcNow.ToString("O"),
            overlapExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            warning = "store now",
        }));
        handler.On("/agents/token", _ => OkEnvelope(new { accessToken = "jwt", expiresIn = 900 }));
        var backend = NewBackend(new HttpClient(handler), layout);

        await backend.PersistDeviceTokenAsync(oldToken, CancellationToken.None);
        var result = await backend.RotateDeviceTokenAsync(CancellationToken.None);
        Assert.Equal(newToken, result.DeviceToken);

        // Persisted credential must be the NEW token (persist-before-transition).
        var handler2 = new StubHandler();
        handler2.On("/agents/token", _ => OkEnvelope(new { accessToken = "jwt2", expiresIn = 900 }));
        var backend2 = NewBackend(new HttpClient(handler2), layout);
        await backend2.RestoreAsync(CancellationToken.None);
        var jwt = await backend2.ExchangeDeviceTokenForJwtAsync(CancellationToken.None);
        Assert.Equal("jwt2", jwt);
        Assert.Equal($"Bearer {newToken}", handler2.Requests.Last().Auth);
    }

    // ── 3. Revocation deletes the local credential (Windows-only) ──────
    [SkippableFact]
    public async Task RevokeDevice_Removes_Credential_File()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only; executed on windows-latest CI");
        var root = NewTempDir();
        var layout = NewLayout(root);
        var backend = NewBackend(new HttpClient(new StubHandler()), layout);
        await backend.PersistDeviceTokenAsync("d".Repeat(64), CancellationToken.None);
        Assert.True(File.Exists(layout.DeviceTokenPath));
        await backend.RevokeDeviceAsync(CancellationToken.None);
        Assert.False(File.Exists(layout.DeviceTokenPath), "revocation must delete the DPAPI credential");
    }

    // ── 4. SQLite pragmas: WAL + busy_timeout (cross-platform, real) ───
    [Fact]
    public async Task Sqlite_Pragmas_Activate_Wal_And_BusyTimeout()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "queue.db");
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await SqliteConnectionPragmas.ApplyToAsync(conn, CancellationToken.None);

        var mode = await FetchPragma(conn, "PRAGMA journal_mode;");
        Assert.Equal("wal", mode, ignoreCase: true);
        var busy = await FetchPragma(conn, "PRAGMA busy_timeout;");
        Assert.Equal("5000", busy);
        var fk = await FetchPragma(conn, "PRAGMA foreign_keys;");
        Assert.Equal("1", fk);
    }

    private static async Task<string> FetchPragma(SqliteConnection conn, string pragma)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? "";
    }

    // ── 5. Installer registers both URI schemes (cross-platform) ───────
    [Fact]
    public void Installer_Registers_Primary_And_Legacy_Uri_Schemes()
    {
        var wxs = FindRepoFile("installer/PrintShopCompanion.Installer.wxs");
        var xml = File.ReadAllText(wxs);
        Assert.Contains(UriActivatorSchemes.Primary, xml);
        Assert.Contains(UriActivatorSchemes.Legacy, xml);
        Assert.Contains("shell\\open\\command", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%1", xml, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new FileNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}");
    }

    private static class UriActivatorSchemes
    {
        public const string Primary = "printshop://";
        public const string Legacy = "printshopcompanion://";
    }
}

internal static class RepeatExtensions
{
    public static string Repeat(this string s, int n) => string.Concat(System.Linq.Enumerable.Repeat(s, n));
}

