using System.Net;
using System.Security.Principal;

namespace Vnc.Service;

public sealed record ServiceConfiguration
{
    public bool RemoteSharingEnabled { get; init; }
    public string BindAddress { get; init; } = IPAddress.Loopback.ToString();
    public bool AllInterfacesConfirmed { get; init; }
    public int Port { get; init; } = 5900;
    public bool StartAutomatically { get; init; } = true;
    public bool AuthenticationRequired { get; init; } = true;
    public int MaxSessions { get; init; } = 1;
    public string AgentSid { get; init; } = string.Empty;
    public string AgentWorkerSid { get; init; } = string.Empty;

    public static ServiceConfiguration SafeDefaults { get; } = new();

    public void Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out var address) || !IsAllowedBindAddress(address, AllInterfacesConfirmed))
        {
            throw new InvalidDataException("The service bind address must be loopback, a private/VPN/link-local IP, or confirmed IPv4 wildcard.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidDataException("The service port must be between 1 and 65535.");
        }

        if (!AuthenticationRequired)
        {
            throw new InvalidDataException("Unauthenticated remote sharing is not supported.");
        }

        if (MaxSessions != 1)
        {
            throw new InvalidDataException("The service supports at most one remote session.");
        }

        if (!string.IsNullOrWhiteSpace(AgentSid))
        {
            _ = new SecurityIdentifier(AgentSid);
        }

        if (!string.IsNullOrWhiteSpace(AgentWorkerSid))
        {
            _ = new SecurityIdentifier(AgentWorkerSid);
        }
    }

    public static bool IsAllowedBindAddress(IPAddress address, bool allowAllInterfaces = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Equals(IPAddress.Any))
        {
            return allowAllInterfaces;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xfe) == 0xfc ||
                   bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80;
        }

        return false;
    }
}
