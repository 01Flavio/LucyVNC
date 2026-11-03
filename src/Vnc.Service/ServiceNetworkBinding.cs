using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Vnc.Service;

public sealed record ServerNetworkInterface(string Name, string Type, IPAddress Address);

public static class ServiceNetworkBinding
{
    public static IReadOnlyList<ServerNetworkInterface> GetAvailableIpv4Interfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Select(item => new ServerNetworkInterface(
                    adapter.Name,
                    adapter.NetworkInterfaceType.ToString(),
                    item.Address)))
            .Where(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(item.Address) &&
                ServiceConfiguration.IsAllowedBindAddress(item.Address))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsPortAvailable(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }
}
