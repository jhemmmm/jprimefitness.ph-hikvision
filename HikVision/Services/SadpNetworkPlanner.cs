using System.Net;

namespace HikVision.Services;

/// <summary>
/// The network parameters sent to a device in a SADP "update" request (same fields as the official tool's
/// "Modify Network Parameters" panel).
/// </summary>
public sealed record SadpNetworkUpdate(
    string Mac,
    string IpAddress,
    string SubnetMask,
    string Gateway,
    int CommandPort = 8000,
    int HttpPort = 80,
    bool Dhcp = false,
    int SdkOverTlsPort = 8443,
    string IPv6Address = "::",
    string IPv6Gateway = "::",
    int IPv6MaskLen = 0);

/// <summary>
/// Pure decision logic for moving an off-subnet device into one of the helper's local subnets.
/// </summary>
public static class SadpNetworkPlanner
{
    /// <summary>
    /// Picks a free host address in the subnet of <paramref name="localIp"/>: first the addresses after the helper's
    /// own, then wrapping around from the start of the subnet. Skips the network, broadcast, the helper's own IP and
    /// anything in <paramref name="reserved"/>. Each remaining candidate is probed with <paramref name="isInUse"/>;
    /// at most <paramref name="maxAttempts"/> probes are made.
    /// </summary>
    public static IPAddress? PickCandidateIp(
        IPAddress localIp,
        IPAddress mask,
        IEnumerable<IPAddress> reserved,
        Func<IPAddress, bool> isInUse,
        int maxAttempts = 10)
    {
        var local = LocalNetwork.ToUInt32(localIp);
        var maskValue = LocalNetwork.ToUInt32(mask);
        var network = local & maskValue;
        var broadcast = network | ~maskValue;

        if (broadcast - network < 2)
        {
            return null;
        }

        var skip = new HashSet<uint> { network, broadcast, local };
        foreach (var address in reserved)
        {
            skip.Add(LocalNetwork.ToUInt32(address));
        }

        var probes = 0;
        foreach (var candidate in EnumerateCandidates(local, network, broadcast))
        {
            if (skip.Contains(candidate))
            {
                continue;
            }

            if (probes++ >= maxAttempts)
            {
                return null;
            }

            var ip = LocalNetwork.FromUInt32(candidate);
            if (!isInUse(ip))
            {
                return ip;
            }
        }

        return null;
    }

    /// <summary>
    /// Plans a SADP update that moves <paramref name="device"/> into the subnet of the interface that received its
    /// reply (or the first physical interface with a gateway). Keeps the device's existing ports, disables DHCP.
    /// </summary>
    internal static SadpNetworkUpdate? PlanOffSubnetFix(
        HikVisionDeviceDiscovery.DiscoveredDevice device,
        IReadOnlyList<LocalNetwork.LocalIpv4Interface> interfaces,
        Func<IPAddress, bool> isInUse,
        int maxAttempts = 10)
    {
        var mac = SadpProtocol.NormalizeMac(device.MacAddress);
        if (mac is null)
        {
            return null;
        }

        var iface = SelectInterface(device, interfaces);
        if (iface is null)
        {
            return null;
        }

        var network = LocalNetwork.ToUInt32(iface.Address) & LocalNetwork.ToUInt32(iface.Mask);
        var gateway = iface.Gateway ?? LocalNetwork.FromUInt32(network | 1);

        var reserved = interfaces.Select(i => i.Address).Append(gateway).ToList();
        var candidate = PickCandidateIp(iface.Address, iface.Mask, reserved, isInUse, maxAttempts);
        if (candidate is null)
        {
            return null;
        }

        return new SadpNetworkUpdate(
            mac,
            candidate.ToString(),
            iface.Mask.ToString(),
            gateway.ToString(),
            device.CommandPort ?? 8000,
            device.HttpPort ?? 80,
            Dhcp: false,
            SdkOverTlsPort: device.SdkOverTlsPort ?? 8443);
    }

    internal static LocalNetwork.LocalIpv4Interface? SelectInterface(
        HikVisionDeviceDiscovery.DiscoveredDevice device,
        IReadOnlyList<LocalNetwork.LocalIpv4Interface> interfaces)
    {
        if (!string.IsNullOrWhiteSpace(device.ReceivedOnLocalIp)
            && IPAddress.TryParse(device.ReceivedOnLocalIp, out var receivedOn))
        {
            var match = interfaces.FirstOrDefault(i => i.Address.Equals(receivedOn));
            if (match is not null)
            {
                return match;
            }
        }

        return interfaces.FirstOrDefault(i => !i.IsVirtual && i.Gateway is not null)
            ?? interfaces.FirstOrDefault(i => !i.IsVirtual)
            ?? interfaces.FirstOrDefault();
    }

    private static IEnumerable<uint> EnumerateCandidates(uint local, uint network, uint broadcast)
    {
        for (var current = local + 1; current < broadcast; current++)
        {
            yield return current;
        }

        for (var current = network + 1; current < local; current++)
        {
            yield return current;
        }
    }
}
