namespace PrintShopCompanion.App.Services;

/// <summary>
/// Shared DI-singleton state for the automatic sign-in performed at startup
/// when saved credentials exist.
/// <para>
/// The previous behaviour navigated straight to Overview on launch and tried
/// to restore the session in the background — the shopkeeper saw the orders
/// page flash up (or sit on stale data) BEFORE the app had verified anything
/// with the server. Professional desktop clients with mandatory auth instead
/// show the login/connect screen with a visible "signing you in…" state, and
/// only enter the app once the credentials are actually verified.
/// </para>
/// <para>The Login screen binds to this state so the user gets feedback in
/// both directions: auto-sign-in in progress, or auto-sign-in failed (with
/// the reason) while the manual forms stay available.</para>
/// </summary>
public sealed class SessionRestoreState
{
    private bool _inProgress;
    private bool _failed;
    private string? _message;

    /// <summary>Raised on whatever thread called the setters — subscribers
    /// must marshal to the UI thread themselves.</summary>
    public event Action? Changed;

    public bool InProgress
    {
        get => _inProgress;
        private set { _inProgress = value; Changed?.Invoke(); }
    }

    public bool Failed
    {
        get => _failed;
        private set { _failed = value; Changed?.Invoke(); }
    }

    /// <summary>Human-readable status line (in-progress message or failure reason).</summary>
    public string? Message
    {
        get => _message;
        private set { _message = value; Changed?.Invoke(); }
    }

    /// <summary>Auto-sign-in started (called at app startup before the window shows).</summary>
    public void Begin(string message)
    {
        InProgress = true;
        Failed = false;
        Message = message;
    }

    /// <summary>Credentials verified — the caller navigates to Overview.</summary>
    public void Succeeded()
    {
        InProgress = false;
        Failed = false;
        Message = null;
    }

    /// <summary>Credentials rejected/restore failed — the app stays on Login.</summary>
    public void Fail(string message)
    {
        InProgress = false;
        Failed = true;
        Message = message;
    }

    /// <summary>Reset to a clean state (e.g. after the user manually signs in or pairs).</summary>
    public void Reset()
    {
        InProgress = false;
        Failed = false;
        Message = null;
    }
}
