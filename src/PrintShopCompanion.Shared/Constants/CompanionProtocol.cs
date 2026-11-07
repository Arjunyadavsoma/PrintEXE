namespace PrintShopCompanion.Shared.Constants;

/// <summary>
/// Protocol-level constants shared between the companion and the PrintShop backend.
/// Keep this file in sync with the backend's contract.
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
    public const int DefaultLocalApiPort = 8421;

    /// <summary>Header name carrying the device token. <b>Never logged.</b></summary>
    public const string DeviceTokenHeader = "X-PrintShop-Device-Token";

    /// <summary>Header name carrying the device ID.</summary>
    public const string DeviceIdHeader = "X-PrintShop-Device-Id";

    /// <summary>Header name carrying the companion app version.</summary>
    public const string AppVersionHeader = "X-PrintShop-Companion-Version";

    /// <summary>Header name used by the website to authenticate to the companion's local API.</summary>
    public const string LocalApiAuthHeader = "X-PrintShop-Local-Auth";

    /// <summary>Job ID used by the test-print flow. Hard-wired on the backend.</summary>
    public const string TestPrintJobId = "test-print";

    /// <summary>Maximum size of a single PDF the companion will accept, in bytes. 100 MB.</summary>
    public const long MaxPdfBytes = 100 * 1024 * 1024;

    /// <summary>SignalR/Socket.IO method name the backend invokes to push a new job.</summary>
    public const string HubMethodDeliverJob = "DeliverJob";

    /// <summary>SignalR/Socket.IO method name the companion invokes to ACK a job.</summary>
    public const string HubMethodAckJob = "AckJob";

    /// <summary>SignalR/Socket.IO method name the companion invokes to report a final status.</summary>
    public const string HubMethodReportStatus = "ReportStatus";

    /// <summary>SignalR/Socket.IO method name the companion invokes to send a heartbeat.</summary>
    public const string HubMethodHeartbeat = "Heartbeat";
}
