using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ods.Core.Platform;

/// <summary>Local TCP port checks for the model server.</summary>
public static class PortProbe
{
    /// <summary>ODS's native llama-server port and its fallbacks (installers/windows/lib/native-llama-runtime.ps1).</summary>
    public static readonly int[] LlamaPortCandidates = [8080, 18080, 28080];

    /// <summary>True when nothing listens on the port on any address and loopback can bind it.</summary>
    public static bool IsFree(int port)
    {
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port)) return false;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The configured port, or the first free ODS default when the setting is automatic (0).</summary>
    public static int ChooseLlamaPort(int configured)
    {
        if (configured != 0) return configured;
        foreach (var port in LlamaPortCandidates)
        {
            if (IsFree(port)) return port;
        }
        throw new InvalidOperationException(
            $"Ports {string.Join(", ", LlamaPortCandidates)} are all in use. Close the program using one of them or set a model server port in Settings.");
    }
}
