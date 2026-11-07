namespace PrintShopCompanion.App.Native;

using System.Threading;

/// <summary>
/// Single-instance guard. Acquires a named mutex at startup; if another
/// instance already holds it, this instance forwards its arguments to the
/// existing instance (via a named pipe) and exits.
///
/// <para><b>Mutex lifecycle:</b> The Mutex is created with <c>owns=true</c> if
/// we're the first instance. On <see cref="Dispose"/>, we release and dispose
/// the Mutex so the OS frees it immediately — the next launch can acquire it.</para>
///
/// <para><b>Pipe lifecycle:</b> The receiver loop runs on a background thread
/// with a <see cref="CancellationTokenSource"/>. On dispose, we cancel the token
/// and dispose the pipe server so the thread exits cleanly.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _owns;
    private CancellationTokenSource? _cts;
    private Task? _receiverTask;

    public event EventHandler<string>? UrlForwarded;

    public SingleInstance(string mutexName) => _mutexName = mutexName;

    public bool AcquireOrForward(string[] args)
    {
        // Try to create the mutex. If we get ownership, we're the first instance.
        _mutex = new Mutex(true, _mutexName, out _owns);
        if (_owns)
        {
            // Start the IPC receiver that listens for forwarded URLs from later launches.
            _cts = new CancellationTokenSource();
            _receiverTask = Task.Run(() => ReceiverLoop(_cts.Token), _cts.Token);
            return true;
        }
        // We're a second instance — try to forward args to the first one.
        if (args.Length > 0 && args[0].StartsWith("printshopcompanion://", StringComparison.OrdinalIgnoreCase))
            ForwardUrl(args[0]);
        return false;
    }

    private static void ForwardUrl(string url)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", "PrintShopCompanion",
                System.IO.Pipes.PipeDirection.Out,
                System.IO.Pipes.PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.None);
            client.Connect(1000);
            using var w = new System.IO.StreamWriter(client);
            w.WriteLine(url);
            w.Flush();
        }
        catch { /* best-effort */ }
    }

    private void ReceiverLoop(CancellationToken ct)
    {
        try
        {
            var server = new System.IO.Pipes.NamedPipeServerStream(
                "PrintShopCompanion",
                System.IO.Pipes.PipeDirection.In,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.None);

            while (!ct.IsCancellationRequested)
            {
                // Wait for a connection with cancellation support.
                var waitTask = Task.Run(() => server.WaitForConnection(), ct);
                try { waitTask.Wait(ct); }
                catch (OperationCanceledException) { break; }

                if (!server.IsConnected) continue;

                try
                {
                    using var r = new System.IO.StreamReader(server);
                    var line = r.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                        UrlForwarded?.Invoke(this, line);
                }
                catch { /* ignore read errors */ }

                server.Disconnect();
            }

            server.Dispose();
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception) { /* pipe error — exit silently */ }
    }

    public void Dispose()
    {
        // 1. Cancel the receiver loop.
        try { _cts?.Cancel(); } catch { }
        // 2. Wait briefly for the receiver task to exit (max 2 seconds).
        try { _receiverTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        // 3. Dispose the cancellation token source.
        _cts?.Dispose();
        _cts = null;
        // 4. Release and dispose the Mutex so the OS frees it immediately.
        try
        {
            if (_owns && _mutex is not null)
                _mutex.ReleaseMutex();
        }
        catch { /* already released */ }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}
