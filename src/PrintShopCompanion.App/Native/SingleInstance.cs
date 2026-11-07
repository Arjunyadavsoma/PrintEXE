namespace PrintShopCompanion.App.Native;

using System.Threading;

/// <summary>
/// Minimal single-instance guard. Uses a named Mutex.
/// On dispose: releases mutex immediately so the app can be reopened.
/// No pipe server, no background threads — just the mutex.
/// </summary>
/// <remarks>
/// <para><b>Abandoned mutex handling:</b> if the previous process was killed
/// (e.g. taskkill, blue screen, power loss) while holding the mutex, the OS
/// marks the mutex as "abandoned". The next <c>WaitOne</c> call acquires it
/// but throws <c>AbandonedMutexException</c>. We catch that and treat it as
/// a successful acquire — the previous owner is gone, we're the new owner.</para>
/// <para><b>The "cannot reopen after closing" bug:</b> the v0.4.6 release
/// had a separate issue — the App used <c>ShutdownMode="OnExplicitShutdown"</c>
/// so closing the window didn't actually exit the process; the mutex stayed
/// held. That's fixed in <c>MainWindow.OnMainWindowClosing</c> which calls
/// <c>Application.Shutdown()</c> explicitly when the user clicks X (unless
/// they opted into MinimizeToTrayOnClose). This class is just the mutex guard;
/// the lifecycle fix is in the UI layer.</para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;
    private bool _owns;

    public SingleInstance(string name) => Name = name;

    public string Name { get; }

    public bool AcquireOrForward(string[] args)
    {
        try
        {
            // initialOwner: true → try to acquire on construction.
            // If the mutex already exists (another instance owns it),
            // _owns will be false and we return false.
            _mutex = new Mutex(initiallyOwned: true, Name, out _owns);
            return _owns;
        }
        catch (AbandonedMutexException)
        {
            // The previous holder was killed without releasing. We acquire
            // ownership and proceed. The mutex is in a valid state for us.
            _owns = true;
            return true;
        }
        catch
        {
            // Any other failure (e.g. UnauthorizedAccess because another
            // user owns the mutex) → behave as if another instance is
            // running. Don't crash the user's app.
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            // Only release if we actually own the mutex. Calling ReleaseMutex
            // on a mutex we don't own throws ApplicationException.
            if (_owns && _mutex != null)
                _mutex.ReleaseMutex();
        }
        catch { /* swallow — we're shutting down */ }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}
