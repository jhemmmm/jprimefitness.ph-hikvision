using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HikVision.Config;

namespace HikVision.Services;

public static class HelperPublicUrlResolver
{
    public static string? ResolveCallbackUrl(string? deviceIp)
    {
        return ResolveCallbackUrl(deviceIp, HikVisionDefaults.BindUrl, HikVisionDefaults.CallbackPath);
    }

    public static string? ResolveCallbackUrl(string? deviceIp, string bindUrl, string callbackPath)
    {
        if (!Uri.TryCreate(bindUrl, UriKind.Absolute, out var bindUri))
        {
            return null;
        }

        var host = ResolvePublicHost(bindUri.Host, deviceIp);

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalizedPath = NormalizePath(callbackPath);
        var port = bindUri.IsDefaultPort
            ? bindUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : bindUri.Port;

        return new UriBuilder(bindUri.Scheme, host, port, normalizedPath).Uri.ToString().TrimEnd('/');
    }

    private static string NormalizePath(string callbackPath)
    {
        var path = string.IsNullOrWhiteSpace(callbackPath) ? "/attendance/callback" : callbackPath.Trim();

        if (!path.StartsWith('/'))
        {
            path = "/"+path;
        }

        return path.Replace("//", "/");
    }

    private static string? ResolvePublicHost(string bindHost, string? deviceIp)
    {
        if (Uri.CheckHostName(bindHost) == UriHostNameType.IPv4 &&
            IPAddress.TryParse(bindHost, out var bindIp) &&
            !IPAddress.IsLoopback(bindIp) &&
            bindHost != IPAddress.Any.ToString())
        {
            return bindHost;
        }

        if (!string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase) &&
            bindHost != IPAddress.Any.ToString() &&
            bindHost != IPAddress.IPv6Any.ToString())
        {
            return bindHost;
        }

        return GetBestLocalIpv4(deviceIp);
    }

    private static string? GetBestLocalIpv4(string? deviceIp)
    {
        var candidates = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.ToString())
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(deviceIp, out var parsedDeviceIp))
        {
            var deviceOctets = parsedDeviceIp.GetAddressBytes();
            var sameSubnet = candidates.FirstOrDefault(candidate =>
            {
                if (!IPAddress.TryParse(candidate, out var parsedCandidate))
                {
                    return false;
                }

                var candidateOctets = parsedCandidate.GetAddressBytes();

                return candidateOctets[0] == deviceOctets[0]
                    && candidateOctets[1] == deviceOctets[1]
                    && candidateOctets[2] == deviceOctets[2];
            });

            if (!string.IsNullOrWhiteSpace(sameSubnet))
            {
                return sameSubnet;
            }
        }

        return candidates.FirstOrDefault(IsPrivateIpv4) ?? candidates.First();
    }

    private static bool IsPrivateIpv4(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var parsedIp))
        {
            return false;
        }

        var octets = parsedIp.GetAddressBytes();

        return octets[0] == 10
            || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            || (octets[0] == 192 && octets[1] == 168);
    }
}
