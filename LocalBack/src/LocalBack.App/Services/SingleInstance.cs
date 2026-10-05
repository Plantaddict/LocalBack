using System.IO.Pipes;
using System.Text;

namespace LocalBack.App.Services;

/// <summary>One LocalBack per user. A second launch (e.g. from the Explorer menu) hands its arguments to the first.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _name = "LocalBack-" + Environment.UserName;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public event Action<string[]>? ArgumentsReceived;

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, @"Local\" + _name, out bool created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return created;
    }

    public void Listen()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    ArgumentsReceived?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
            }
        }, ct);
    }

    public static void Send(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", "LocalBack-" + Environment.UserName, PipeDirection.Out);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? new[] { "--show" } : args));
            client.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
