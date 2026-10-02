using System.IO;
using System.IO.Pipes;
using System.Text;

namespace CcxShell.Core;

/// <summary>
/// Typing "ccx" in a second folder should add a session to the window you already have,
/// not open a fifth window. Otherwise we've rebuilt the problem we're solving.
/// </summary>
public sealed class SingleInstance : IDisposable
{
#if DEBUG
    // A dev build runs beside the installed (Release) Clayo instead of handing its folder over.
    private const string MutexName = @"Local\CcxShell.SingleInstance.Dev";
    private const string PipeName = "CcxShell.Handoff.Dev";
#else
    private const string MutexName = @"Local\CcxShell.SingleInstance";
    private const string PipeName = "CcxShell.Handoff";
#endif

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    /// <summary>Fired on a background thread when another launch hands us a folder.</summary>
    public event Action<string>? FolderReceived;

    /// <summary>True if we own the instance. False means we handed off and should exit.</summary>
    public bool TryAcquire(string folderToHandOff)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (createdNew)
        {
            StartServer();
            return true;
        }

        SendToRunningInstance(folderToHandOff);
        return false;
    }

    private void StartServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var folder = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(folder))
                        FolderReceived?.Invoke(folder.Trim());
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client vanished — keep serving */ }
            }
        }, token);
    }

    private static void SendToRunningInstance(string folder)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(folder);
            writer.Flush();
        }
        catch (TimeoutException) { /* stale mutex, nothing listening — caller just exits */ }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }
}
