namespace PrintShopCompanion.Shared.Constants;

/// <summary>
/// Protocol-level constants shared between the companion and the PrintShop backend.
/// Reconciled against the actual SaaS contract (Arjunyadavsoma/printshop-saas,
/// docs/integration/PRINTEXE_*), 2026-11-16. See docs/CONTRACT_COMPATIBILITY.md.
/// </summary>
public static class CompanionProtocol
{
    /// <summary>Custom URL scheme registered with Windows during install.</summary>
    public const string UrlScheme = "printshopcompanion";

    /// <summary>Path under the scheme that completes pairing.</summary>
    public const string PairHost = "pair";

    /// <summary>Path under the scheme that revokes the device.</summary>
    public const string RevokeHost = "revoke";

    /// <summary>Default HTTP port the companion's local API listens on. Loopback only.</summary>
    /// <remarks>
    /// NOTE: no local HTTP listener exists in the current codebase. This constant is
    /// reserved for a future local-API feature; see docs/AUDIT.md §16 item #2.
    /// </remarks>
    public const int DefaultLocalApiPort = 8421;

    /// <summary>
    /// Header name carrying the device token. <b>Never logged.</b>
    /// </summary>
    /// <remarks>
    /// SUPERSEDED: the real SaaS backend authenticates devices with
    /// <c>Authorization: Bearer &lt;deviceToken&gt;</c>, not this custom header
    /// (zero grep hits for <c>X-PrintShop-Device-Token</c> in the SaaS repo).
    /// The production client <see cref="Infrastructure.Backend.HttpPrintShopBackend"/>
    /// already uses <c>Authorization: Bearer</c>. This constant is retained only for
    /// the local-dev mock backend (PrintShopCompanion.Backend) which still uses it;
    /// do NOT use it for production backend calls.
    /// </remarks>
    public const string DeviceTokenHeader = "X-PrintShop-Device-Token";

    /// <summary>Header name carrying the device ID. Mock-backend only; not used by the production client.</summary>
    public const string DeviceIdHeader = "X-PrintShop-Device-Id";

    /// <summary>
    /// Header name carrying the companion app version. Advisory on the real SaaS
    /// backend (logged, not enforced; a <c>426 Upgrade Required</c> gate is planned at v1.0.0).
    /// </summary>
    public const string AppVersionHeader = "X-PrintShop-Companion-Version";

    /// <summary>Header name used by the website to authenticate to the companion's local API. Reserved (no local API today).</summary>
    public const string LocalApiAuthHeader = "X-PrintShop-Local-Auth";

    /// <summary>Job ID used by the test-print flow. Hard-wired on the backend.</summary>
    public const string TestPrintJobId = "test-print";

    /// <summary>Maximum size of a single PDF the companion will accept, in bytes. 100 MB.</summary>
    public const long MaxPdfBytes = 100 * 1024 * 1024;

    // ─── Socket.IO events (real SaaS contract, confirmed against source) ──────────
    // The backend is Socket.IO (Node socket.io), NOT ASP.NET SignalR. The single
    // inbound handler the companion invokes is `auth` with {token: <jwt>}. The
    // server→client events the companion must listen for are listed below. See
    // docs/CONTRACT_COMPATIBILITY.md §2.5 for the exhaustive event catalogue.

    /// <summary>Socket.IO event the companion emits to authenticate the connection. Payload: <c>{token: &lt;jwt&gt;}</c>. The ONLY client→server event.</summary>
    public const string SocketIoEventAuth = "auth";

    /// <summary>Socket.IO event the server emits on successful auth. Payload: <c>{shopId, role}</c>.</summary>
    public const string SocketIoEventAuthOk = "auth-ok";

    /// <summary>Socket.IO event the server emits on auth failure. Payload: <c>{message}</c>. Companion should refresh the JWT and re-emit <see cref="SocketIoEventAuth"/>.</summary>
    public const string SocketIoEventAuthError = "auth-error";

    /// <summary>Socket.IO event the server emits to push a new job to the device. Payload: <c>PrintJobDto</c> + <c>deliveryAttemptId</c>. No wire-level ACK; claim is via REST <c>POST /agents/jobs/{id}/status {status:"printing"}</c>.</summary>
    public const string SocketIoEventJobDispatch = "job.dispatch";

    /// <summary>Socket.IO event the server broadcasts when a job transitions to printing. Payload: <c>{jobId, fileId}</c>. Companion does NOT need to listen (it drove the transition).</summary>
    public const string SocketIoEventJobPrinting = "job.printing";

    /// <summary>Socket.IO event the server broadcasts when a job completes. Payload: <c>{jobId, fileId}</c>.</summary>
    public const string SocketIoEventJobCompleted = "job.completed";

    /// <summary>Socket.IO event the server broadcasts when a job fails. Payload: <c>{jobId, fileId, error}</c>.</summary>
    public const string SocketIoEventJobFailed = "job.failed";

    /// <summary>Socket.IO event the server emits when the stale-recovery worker moves a job to needs_review (5-min threshold). Payload: <c>{jobId, fileId}</c>.</summary>
    public const string SocketIoEventJobNeedsReview = "job.needs_review";

    /// <summary>Socket.IO event the server emits when the device is revoked. Payload: <c>{agentId, agentName, cancelledJobs}</c>. Companion should clear its local credential and stop.</summary>
    public const string SocketIoEventAgentRevoked = "agent.revoked";

    /// <summary>Socket.IO event the server emits on successful receipt ack. Payload: <c>{jobId, agentId, agentName, receivedAt, checksum?}</c>.</summary>
    public const string SocketIoEventDeliveryReceived = "delivery.received";

    /// <summary>Socket.IO event the server emits when a sync push completes. Payload: <c>{count, deviceId}</c>.</summary>
    public const string SocketIoEventSyncPushed = "sync.pushed";

    // ─── DEPRECATED — do not use; kept for source archaeology ────────────────────
    // These were speculative SignalR-style method names from an earlier draft of
    // docs/PROTOCOL.md. The real backend has no inbound handler by these names
    // (emitting them is silently dropped). Use the SocketIoEvent* constants above.
    // Removal is deferred until docs/PROTOCOL.md and docs/BACKEND-MAPPING.md are
    // updated and no references remain.
    [System.Obsolete("Use SocketIoEventJobDispatch. The real SaaS backend has no 'DeliverJob' handler.")]
    public const string HubMethodDeliverJob = "DeliverJob";

    [System.Obsolete("Use the REST endpoint POST /agents/jobs/{id}/status. The real SaaS backend has no 'AckJob' handler.")]
    public const string HubMethodAckJob = "AckJob";

    [System.Obsolete("Use the REST endpoint POST /agents/jobs/{id}/status. The real SaaS backend has no 'ReportStatus' handler.")]
    public const string HubMethodReportStatus = "ReportStatus";

    [System.Obsolete("Use the REST endpoint POST /agents/heartbeat. The real SaaS backend has no 'Heartbeat' handler.")]
    public const string HubMethodHeartbeat = "Heartbeat";
}
