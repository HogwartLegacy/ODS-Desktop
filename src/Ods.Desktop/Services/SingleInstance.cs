using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Ods.Core;
using Ods.Core.Diagnostics;

namespace Ods.Desktop.Services;

/// <summary>
/// One ODS window per user and data folder. A second launch (Start menu,
/// shortcut, sign-in) asks the running instance to show itself over a named
/// pipe, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public event Action<string[]>? Activated;

    private static string Name
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var root = OdsPaths.Default().Root.ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "|" + root)))[..16];
            return $"ODS.Desktop.{hash}";
        }
    }

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        if (!createdNew)
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous instance crashed; this process now owns the mutex.
            }
        }
        return new SingleInstance(mutex);
    }

    public static void ActivateRunningInstance(string[] args)
    {
        using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
        client.Connect(3000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false));
        writer.Write(string.Join('\n', args));
    }

    public void StartListening()
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await server.WaitForConnectionAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(_stop.Token);
                var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                AppLog.Info("Another ODS launch asked this window to show itself.");
                Activated?.Invoke(args);
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
