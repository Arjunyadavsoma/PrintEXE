namespace PrintShopCompanion.Core.Models;

/// <summary>
/// Domain model for a pending remote command fetched via
/// <c>GET /api/v1/companion/commands</c> (v1 contract §6). The companion
/// validates <see cref="ExpiresAt"/> locally before executing (defense in
/// depth: a command may expire between fetch and execution).
/// </summary>
public sealed class PendingCommand
{
    /// <summary>Server-assigned command id (UUID). Used for ack + result reporting.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Command type from the v1 contract allowlist
    /// (see <see cref="CommandType"/>). The companion rejects any value not
    /// in the allowlist.
    /// </summary>
    public required string CommandType { get; init; }

    /// <summary>The command payload as a JSON string (e.g. "{}" or '{"jobId":"uuid"}').</summary>
    public string PayloadJson { get; init; } = "{}";

    /// <summary>UTC instant the command expires. The companion must not execute an expired command.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>True if <see cref="ExpiresAt"/> is in the past relative to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
}

/// <summary>
/// The v1 contract remote-command allowlist (contract §8). The companion
/// rejects any command whose type is not in this enum. Adding a value here
/// is a contract change — coordinate with Agent 1.
/// </summary>
public static class CommandType
{
    public const string RefreshPrinters = "refresh_printers";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string CancelJob = "cancel_job";
    public const string RetryJob = "retry_job";
    public const string TestPrint = "test_print";
    public const string Diagnostics = "diagnostics";

    /// <summary>All allowlisted command types. Used by the executor to reject unsupported commands.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        RefreshPrinters, Pause, Resume, CancelJob, RetryJob, TestPrint, Diagnostics,
    };

    /// <summary>True if <paramref name="type"/> is in the v1 allowlist (case-insensitive).</summary>
    public static bool IsAllowed(string? type) =>
        !string.IsNullOrWhiteSpace(type) && All.Contains(type);
}

/// <summary>
/// Status values for <c>POST /api/v1/companion/commands</c> (v1 contract §7).
/// The companion reports <c>delivered</c> on receipt, <c>executing</c> when
/// starting, then <c>succeeded</c> or <c>failed</c> on completion. The
/// backend is idempotent on (commandId, status).
/// </summary>
public static class CommandStatus
{
    public const string Delivered = "delivered";
    public const string Accepted = "accepted";
    public const string Executing = "executing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>All valid status strings the companion may report.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered, Accepted, Executing, Succeeded, Failed,
    };
}

/// <summary>
/// Result of <c>POST /api/v1/companion/pair</c> (v1 contract §2). Returned to
/// the Pair Computer screen; the token is DPAPI-persisted before this method
/// returns so a crash immediately after cannot lose the credential.
/// </summary>
public sealed record PairResult
{
    /// <summary>The agent/device id assigned by the backend.</summary>
    public required string AgentId { get; init; }

    /// <summary>The long-lived device token (64 hex chars). Returned exactly once.</summary>
    public required string DeviceToken { get; init; }

    /// <summary>The shop the device is now bound to.</summary>
    public required string ShopId { get; init; }

    /// <summary>Human-readable shop name for the dashboard header.</summary>
    public required string ShopName { get; init; }

    /// <summary>The companion API protocol version (1 for this contract).</summary>
    public int ProtocolVersion { get; init; } = 1;
}

/// <summary>
/// Result of <c>POST /api/v1/companion/commands</c> (v1 contract §7).
/// <see cref="Applied"/> is true when the backend accepted the status update
/// (idempotent re-reports return <c>applied:true</c> without re-processing).
/// </summary>
public sealed record CommandReportResult(bool Applied);
