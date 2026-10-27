using System.IO;
using System.IO.Pipes;

namespace FileHub.Platform;

/// <summary>Mutex + named pipe so a second launch forwards its arguments to the running instance.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Local\\FileHub.SingleInstance.v1";
    private const string PipeName = "FileHub.Pipe.v1";
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool IsFirstInstance { get; private set; }
    public event Action<string[]>? ArgsReceived;

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        IsFirstInstance = created;
        return created;
    }

    public void StartServer()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server);
                    var text = await reader.ReadToEndAsync(ct);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray();
                    ArgsReceived?.Invoke(args);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Error("Pipe server error", ex); await Task.Delay(500, CancellationToken.None); }
            }
        }, ct);
    }

    public static bool SendToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            foreach (var a in args) w.WriteLine(a);
            if (args.Length == 0) w.WriteLine("--show");
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsFirstInstance) { try { _mutex?.ReleaseMutex(); } catch { } }
        _mutex?.Dispose();
    }
}
