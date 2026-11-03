using System.Net;

namespace Vnc.Service;

public sealed record ServiceConfiguration
{
    public bool RemoteSharingEnabled { get; init; }
    public string BindAddress { get; init; } = IPAddress.Loopback.ToString();
    public int Port { get; init; } = 5901;
    public bool AuthenticationRequired { get; init; } = true;
    public int MaxSessions { get; init; } = 1;

    public static ServiceConfiguration SafeDefaults { get; } = new();

    public void Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out var address) || !IsAllowedBindAddress(address))
        {
            throw new InvalidDataException("The service bind address must be an explicit loopback, private, VPN, or link-local IP address.");
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
    }

    public static bool IsAllowedBindAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
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
