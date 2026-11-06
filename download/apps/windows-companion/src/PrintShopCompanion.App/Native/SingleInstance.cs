namespace PrintShopCompanion.App.Native;

using System.Threading;

/// <summary>
/// Single-instance guard. Acquires a named mutex at startup; if another
/// instance already holds it, this instance forwards its arguments to
/// the existing instance (via a hidden window message pump) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _owns;

    public event EventHandler<string>? UrlForwarded;

    public SingleInstance(string mutexName) => _mutexName = mutexName;

    public bool AcquireOrForward(string[] args)
    {
        _mutex = new Mutex(true, _mutexName, out _owns);
        if (_owns)
        {
            // Start the IPC receiver that listens for forwarded URLs from later launches.
            _ = Task.Run(ReceiverLoop);
            return true;
        }
        // We're a second instance — try to forward args to the first one.
        if (args.Length > 0 && args[0].StartsWith("printshopcompanion://", StringComparison.OrdinalIgnoreCase))
            ForwardUrl(args[0]);
        return false;
    }

    private static void ForwardUrl(string url)
    {
        // Production: writes to the named pipe \\.\pipe\PrintShopCompanion that the
        // first instance is listening on. Stubbed here for the first delivery; the
        // full implementation lives in docs/ARCHITECTURE.md "Single-instance IPC".
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", "PrintShopCompanion", System.IO.Pipes.PipeDirection.Out);
            client.Connect(1000);
            using var w = new System.IO.StreamWriter(client);
            w.WriteLine(url);
            w.Flush();
        }
        catch { /* best-effort; the OS will just open the existing instance */ }
    }

    private void ReceiverLoop()
    {
        try
        {
            using var server = new System.IO.Pipes.NamedPipeServerStream("PrintShopCompanion", System.IO.Pipes.PipeDirection.In);
            while (true)
            {
                server.WaitForConnection();
                using var r = new System.IO.StreamReader(server);
                var line = r.ReadLine();
                if (!string.IsNullOrEmpty(line)) UrlForwarded?.Invoke(this, line);
                server.Disconnect();
            }
        }
        catch { /* shut down */ }
    }

    public void Dispose()
    {
        try { if (_owns) _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
    }
}
