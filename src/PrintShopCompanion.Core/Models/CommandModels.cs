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

    /// <summary>
    /// The command payload as a JSON string. The actual SaaS sends the payload
    /// as a parsed JSON OBJECT (not a string); the HTTP client re-serializes it
    /// to a string here so the executor can parse job-specific fields
    /// (e.g. <c>jobId</c> for cancel_job/retry_job) with <see cref="System.Text.Json.JsonDocument"/>.
    /// Defaults to <c>"{}"</c> if the server sent null/garbage.
    /// </summary>
    public string PayloadJson { get; init; } = "{}";

    /// <summary>ISO8601 of the command's creation (server's <c>issuedAt</c>). Null if the server omitted it. Phase 3 addition.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

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

    /// <summary>
    /// Phase F (brief §A) — the handler started but the process crashed
    /// before confirming the side effect's result. The SaaS accepts this
    /// (its <c>REPORTABLE_STATUSES</c> enum includes it, commit ce75cd26).
    /// The companion reports this on restart for any command left 'executing',
    /// AND a handler may return it directly when it detects an uncertain
    /// physical outcome (e.g. a spooler submission that returned no
    /// verifiable completion). The shopkeeper must resolve these manually —
    /// the system NEVER automatically re-executes or auto-fails them.
    /// </summary>
    public const string OutcomeUnknown = "outcome_unknown";

    /// <summary>
    /// Phase F — expired. Set locally when a fetched command's ExpiresAt has
    /// passed before execution started. The SaaS uses this server-side too
    /// (it flips fetched rows past expiry to 'expired' best-effort).
    /// </summary>
    public const string Expired = "expired";

    /// <summary>Phase F — cancelled. Server-side only (companion observes, never sets).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// All status strings the companion may report to the SaaS. Matches the
    /// SaaS <c>REPORTABLE_STATUSES</c> enum (route.ts): delivered, accepted,
    /// executing, succeeded, failed, outcome_unknown.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered, Accepted, Executing, Succeeded, Failed, OutcomeUnknown,
    };

    /// <summary>
    /// The terminal statuses (the SaaS treats these as terminal — sets executedAt).
    /// </summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, Failed, OutcomeUnknown,
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
/// Phase 3 fix: the actual SaaS returns <c>{commandId, status, updatedAt}</c>,
/// NOT <c>{applied}</c> as the published contract stated. The poller uses
/// <see cref="Status"/> to confirm the backend recorded the transition; a
/// non-null <see cref="UpdatedAt"/> means the row was touched (idempotent
/// re-reports return the same <see cref="Status"/> + an unchanged
/// <see cref="UpdatedAt"/>).
/// </summary>
public sealed record CommandReportResult(
    string? CommandId,
    string? Status,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>True when the backend returned a non-null status (the report was accepted, idempotently or not).</summary>
    public bool Accepted => !string.IsNullOrWhiteSpace(Status);

    /// <summary>Convenience for callers that only want a yes/no. Kept for backward compat with the Phase 2 poller.</summary>
    public bool Applied => Accepted;
}
