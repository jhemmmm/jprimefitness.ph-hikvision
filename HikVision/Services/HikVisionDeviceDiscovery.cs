using Serilog;

namespace HikVision.Services;

/// <summary>
/// Discovers Hikvision devices with SADP: an inquiry probe sent to multicast group 239.255.255.250:37020 on every
/// IPv4 interface (plus subnet broadcasts), the same way the official SADP tool works. Devices on other IP subnets
/// still answer because SADP runs on the local Ethernet segment, not on IP routing.
/// </summary>
public static class HikVisionDeviceDiscovery
{
    private const int DefaultTimeoutMs = 5000;

    public record DiscoveredDevice(
        string IpAddress,
        string? SerialNumber,
        string? DeviceType,
        string? MacAddress,
        string? DeviceDescription = null,
        string? DeviceTypeCode = null,
        string? SubnetMask = null,
        string? Gateway = null,
        int? CommandPort = null,
        int? HttpPort = null,
        bool? Dhcp = null,
        string? SoftwareVersion = null,
        string? DspVersion = null,
        string? BootTime = null,
        bool? Activated = null,
        string? Encrypt = null,
        string? Salt = null,
        string? IPv6Address = null,
        string? IPv6Gateway = null,
        int? IPv6MaskLen = null,
        int? EnhancedSdkPort = null,
        int? SdkOverTlsPort = null,
        bool? PasswordResetAbility = null,
        string? Uuid = null,
        string? ReceivedOnLocalIp = null,
        string? RawXml = null);

    public static async Task<List<DiscoveredDevice>> DiscoverDevicesAsync(
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        using var socket = SadpSocket.Open();

        if (socket.Interfaces.Count == 0)
        {
            Log.Warning("SADP: No active IPv4 network interfaces found");
            return [];
        }

        // Our own send interfaces — used to drop the multicast echoes of our own probe.
        var localIps = socket.Interfaces
            .Select(i => i.Address.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uuid = SadpProtocol.NewUuid();
        var probe = SadpProtocol.ToBytes(SadpProtocol.BuildInquiryXml(uuid));
        var sent = await socket.SendToAllAsync(probe, timeoutCts.Token);

        Log.Information(
            "SADP: Probe sent to {Group}:{Port} on {InterfaceCount} interface(s) ({Sends} sends). Listening for {TimeoutMs}ms...",
            SadpProtocol.GroupAddress,
            SadpProtocol.Port,
            socket.Interfaces.Count,
            sent,
            timeoutMs);

        var buffer = new byte[16384];

        while (!timeoutCts.IsCancellationRequested)
        {
            var datagram = await socket.ReceiveAsync(buffer, timeoutCts.Token);
            if (datagram is null)
            {
                break;
            }

            // ParseProbeMatch returns null for anything that is not an inquiry ProbeMatch (our own probe echoes,
            // update replies, other tools' traffic), so a single parse doubles as the filter.
            var device = SadpProtocol.ParseProbeMatch(datagram.Xml, datagram.RemoteIp, datagram.ReceivedOnLocalIp);
            if (device is null || localIps.Contains(device.IpAddress))
            {
                continue;
            }

            var key = SadpProtocol.NormalizeMac(device.MacAddress) ?? device.IpAddress;
            if (!devices.TryAdd(key, device))
            {
                continue;
            }

            Log.Information(
                "SADP: Found device - IP: {IP}/{Mask} gw {Gateway}, MAC: {Mac}, Serial: {Serial}, Type: {Type}, SW: {Software}, Encrypt: {Encrypt}, via {LocalIp} (to {Destination})",
                device.IpAddress,
                device.SubnetMask ?? "?",
                device.Gateway ?? "?",
                device.MacAddress ?? "?",
                device.SerialNumber ?? "?",
                device.DeviceType ?? "?",
                device.SoftwareVersion ?? "?",
                device.Encrypt ?? "?",
                datagram.ReceivedOnLocalIp ?? "?",
                datagram.DestinationIp ?? "?");
        }

        return devices.Values
            .OrderBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<DiscoveredDevice> DiscoverRequiredSingleDeviceAsync(
        int attempts = 1,
        int timeoutMs = DefaultTimeoutMs,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        List<DiscoveredDevice> lastDevices = [];
        var delay = retryDelay ?? TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            lastDevices = await DiscoverDevicesAsync(timeoutMs, cancellationToken);
            if (lastDevices.Count == 1)
            {
                return lastDevices[0];
            }

            if (lastDevices.Count > 1)
            {
                break;
            }

            if (attempt < attempts)
            {
                Log.Information("SADP: No device found on attempt {Attempt}/{Attempts}; retrying...", attempt, attempts);
                await Task.Delay(delay, cancellationToken);
            }
        }

        return RequireSingleDevice(lastDevices);
    }

    public static DiscoveredDevice RequireSingleDevice(IReadOnlyCollection<DiscoveredDevice> devices)
    {
        if (devices.Count == 1)
        {
            return devices.First();
        }

        if (devices.Count == 0)
        {
            throw new InvalidOperationException(
                "No Hikvision device was discovered by SADP. Confirm the helper is on the same LAN segment as the device, " +
                "close the Hikvision SADP Tool if it is open (it holds UDP port 37020), and allow inbound UDP 37020 through Windows Firewall.");
        }

        var found = string.Join(
            "; ",
            devices.Select(device => $"{device.IpAddress} mac={device.MacAddress ?? "unknown"} serial={device.SerialNumber ?? "unknown"} type={device.DeviceType ?? "unknown"}"));

        throw new InvalidOperationException(
            "Multiple Hikvision devices were discovered, so the helper will not choose one automatically. Found: " + found);
    }
}
