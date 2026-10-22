namespace Ods.Core.Platform;

/// <summary>Another ODS process (the desktop app or ods-cli) owns this data folder.</summary>
public sealed class EngineBusyException(string message) : Exception(message);

/// <summary>
/// Exclusive ownership of a data folder: only the owner may start model
/// servers or clean up after a crashed session. Held as an open file handle
/// with no sharing, which Windows releases even if the owner crashes.
/// </summary>
public sealed class EngineLock : IDisposable
{
    private readonly FileStream _handle;

    private EngineLock(FileStream handle) => _handle = handle;

    public static EngineLock Acquire(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "engine.lock");
        try
        {
            var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            handle.Write(BitConverter.GetBytes(Environment.ProcessId));
            handle.Flush();
            return new EngineLock(handle);
        }
        catch (IOException)
        {
            throw new EngineBusyException(
                "ODS is already running and owns this data folder. Use the ODS window (or quit it from the tray) before running this command.");
        }
    }

    public void Dispose() => _handle.Dispose();
}
