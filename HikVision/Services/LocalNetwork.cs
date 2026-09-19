using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HikVision.Services;

/// <summary>
/// Local IPv4 interface enumeration and reachability helpers shared by SADP discovery and the network planner.
/// </summary>
internal static class LocalNetwork
{
    public sealed record LocalIpv4Interface(
        string Name,
        int Index,
        IPAddress Address,
        IPAddress Mask,
        IPAddress? Gateway,
        IPAddress Broadcast,
        bool IsVirtual,
        bool SupportsMulticast);

    /// <summary>
    /// Returns every up, non-loopback, non-tunnel IPv4 interface address with its mask, gateway and broadcast address.
    /// Link-local (169.254.x.x) addresses are skipped.
    /// </summary>
    public static List<LocalIpv4Interface> GetIpv4Interfaces(bool includeVirtualInterfaces)
    {
        var interfaces = new List<LocalIpv4Interface>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsableNetworkInterface(ni, includeVirtualInterfaces))
            {
                continue;
            }

            var properties = ni.GetIPProperties();
            int index;
            try
            {
                index = properties.GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException)
            {
                index = -1;
            }

            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            foreach (var addr in properties.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask is null)
                {
                    continue;
                }

                if (IsLinkLocal(addr.Address))
                {
                    continue;
                }

                var ip = ToUInt32(addr.Address);
                var mask = ToUInt32(addr.IPv4Mask);

                interfaces.Add(new LocalIpv4Interface(
                    ni.Name,
                    index,
                    addr.Address,
                    addr.IPv4Mask,
                    gateway,
                    FromUInt32(ip | ~mask),
                    IsLikelyVirtualInterface(ni),
                    ni.SupportsMulticast));
            }
        }

        return interfaces;
    }

    public static List<(uint Network, uint Mask)> GetLocalSubnetMemberships(bool includeVirtualInterfaces)
    {
        return GetIpv4Interfaces(includeVirtualInterfaces)
            .Select(i => (ToUInt32(i.Address) & ToUInt32(i.Mask), ToUInt32(i.Mask)))
            .Distinct()
            .ToList();
    }

    public static bool IsInAnyLocalSubnet(IPAddress address, IReadOnlyCollection<(uint Network, uint Mask)> subnets)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = ToUInt32(address);
        return subnets.Any(subnet => (value & subnet.Mask) == subnet.Network);
    }

    /// <summary>Parses and normalizes an IPv4 address, rejecting IPv6 and blank input.</summary>
    public static bool TryParseIpv4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !IPAddress.TryParse(value.Trim(), out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }

    public static bool IsUdpPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts a TCP connect to prove the host is reachable via normal IP routing.
    /// </summary>
    public static async Task<bool> IsTcpPortOpenAsync(IPAddress address, int port, int timeoutMs, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(address, port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ICMP echo check used to avoid handing out an IP that another host already answers on.
    /// </summary>
    public static async Task<bool> IsHostRespondingAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsUsableNetworkInterface(NetworkInterface networkInterface, bool includeVirtualInterfaces)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
            return false;
        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return false;

        return includeVirtualInterfaces || !IsLikelyVirtualInterface(networkInterface);
    }

    public static bool IsLikelyVirtualInterface(NetworkInterface networkInterface)
    {
        var name = networkInterface.Name + " " + networkInterface.Description;

        return name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WSL", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    public static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromUInt32(uint value)
    {
        return new IPAddress([
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);
    }
}
