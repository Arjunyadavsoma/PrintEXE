namespace PrintShopCompanion.App.Native;

using System.Threading;

/// <summary>
/// Minimal single-instance guard. Uses a named Mutex.
/// On dispose: releases mutex immediately so the app can be reopened.
/// No pipe server, no background threads — just the mutex.
/// </summary>
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
            _mutex = new Mutex(true, Name, out _owns);
            return _owns;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_owns && _mutex != null)
                _mutex.ReleaseMutex();
        }
        catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}
