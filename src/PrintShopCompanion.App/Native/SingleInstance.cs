namespace PrintShopCompanion.App.Native;

using System.Threading;

/// <summary>
/// Single-instance guard using a named Mutex.
///
/// <para><b>Design:</b></para>
/// <list type="bullet">
///   <item>If we're the first instance, we own the Mutex and run a background
///     pipe server to receive forwarded URLs from later instances.</item>
///   <item>If a second instance launches, it can't acquire the Mutex, so it
///     forwards its URL via the pipe and exits.</item>
///   <item>On dispose, we cancel the pipe server and release the Mutex so
///     the OS frees it immediately for the next launch.</item>
/// </list>
///
/// <para><b>Key fix:</b> The Mutex is a local variable held alive by the
/// field reference. Previous versions had the pipe server thread keeping
/// the process alive after Dispose. Now the pipe server is disposed
/// BEFORE the Mutex is released, ensuring no threads are stuck.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _owns;
    private CancellationTokenSource? _cts;
    private NamedPipeServerStream? _pipeServer;
    private Thread? _receiverThread;

    public event EventHandler<string>? UrlForwarded;
    public event Action? AnotherInstanceLaunched;

    public SingleInstance(string mutexName) => _mutexName = mutexName;

    public bool AcquireOrForward(string[] args)
    {
        // Create the Mutex. If we get ownership, we're the first instance.
        _mutex = new Mutex(true, _mutexName, out _owns);
        if (_owns)
        {
            // Start the pipe server on a dedicated background thread (not ThreadPool
            // — ThreadPool threads can keep the process alive via Task.Run).
            _cts = new CancellationTokenSource();
            _pipeServer = new NamedPipeServerStream(
                "PrintShopCompanion",
                System.IO.Pipes.PipeDirection.In,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.None);
            _receiverThread = new Thread(() => ReceiverLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "SingleInstance-PipeServer"
            };
            _receiverThread.Start();
            return true;
        }

        // We're a second instance. Try to forward the URL via the pipe.
        if (args.Length > 0 && args[0].StartsWith("printshopcompanion://", StringComparison.OrdinalIgnoreCase))
            ForwardUrl(args[0]);

        // Also signal that another instance tried to launch (the caller can
        // use this to bring the existing window to the foreground).
        AnotherInstanceLaunched?.Invoke();

        return false;
    }

    private static void ForwardUrl(string url)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", "PrintShopCompanion",
                System.IO.Pipes.PipeDirection.Out);
            client.Connect(500);
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
            while (!ct.IsCancellationRequested)
            {
                // WaitForConnection blocks until a client connects or the pipe is disposed.
                _pipeServer?.WaitForConnection();

                if (ct.IsCancellationRequested) break;
                if (_pipeServer == null || !_pipeServer.IsConnected) continue;

                try
                {
                    using var r = new System.IO.StreamReader(_pipeServer);
                    var line = r.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                        UrlForwarded?.Invoke(this, line);
                }
                catch { /* read error */ }

                try { _pipeServer?.Disconnect(); } catch { }
            }
        }
        catch (ObjectDisposedException) { /* pipe disposed — normal shutdown */ }
        catch (Exception) { /* other error — exit silently */ }
    }

    public void Dispose()
    {
        // 1. Cancel the token (signals the receiver loop to stop).
        try { _cts?.Cancel(); } catch { }

        // 2. Dispose the pipe server — this unblocks WaitForConnection()
        //    by throwing ObjectDisposedException on the receiver thread.
        try { _pipeServer?.Dispose(); } catch { }
        _pipeServer = null;

        // 3. Wait for the receiver thread to exit (max 1 second — it should
        //    exit immediately when the pipe is disposed).
        try { _receiverThread?.Join(1000); } catch { }
        _receiverThread = null;

        // 4. Dispose the CTS.
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        // 5. Release and dispose the Mutex — CRITICAL for reopening.
        try
        {
            if (_owns && _mutex is not null)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch { /* already released or abandoned */ }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}
