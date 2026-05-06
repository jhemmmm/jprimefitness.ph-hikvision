using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HikVision.Config;
using Serilog;

namespace HikVision.Services;

/// <summary>
/// Discovers Hikvision devices on the local network using SADP UDP broadcast on port 37020.
/// </summary>
public static class HikVisionDeviceDiscovery
{
    private const int SadpPort = 37020;
    private const int DefaultTimeoutMs = 5000;

    public record DiscoveredDevice(string IpAddress, string? SerialNumber, string? DeviceType, string? MacAddress);

    private sealed record BroadcastTarget(IPAddress Address, string InterfaceName);

    public static async Task<List<DiscoveredDevice>> DiscoverDevicesAsync(
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        var devices = new ConcurrentDictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var localIps = GetLocalIpAddresses();
        var broadcastTargets = GetSubnetBroadcastAddresses();

        if (broadcastTargets.Count == 0)
        {
            Log.Warning("SADP: No active IPv4 network interfaces found for broadcast");
            return [];
        }

        var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var probeXml = $"""<?xml version="1.0" encoding="utf-8"?><Probe><Uuid>{uuid}</Uuid><Types>inquiry</Types></Probe>""";
        var probeBytes = Encoding.UTF8.GetBytes(probeXml);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        if (IsSadpPortInUse())
        {
            Log.Warning(
                "SADP: UDP port {Port} is already in use on this machine. If Hikvision SADP Tool is open, it can receive device replies before the helper does.",
                SadpPort);
        }

        using var socket = CreateSadpSocket();

        foreach (var target in broadcastTargets)
        {
            try
            {
                await socket.SendToAsync(
                    probeBytes,
                    SocketFlags.None,
                    new IPEndPoint(target.Address, SadpPort),
                    timeoutCts.Token);

                Log.Information(
                    "SADP: Probe sent to {Broadcast}:{Port} via {Interface}",
                    target.Address,
                    SadpPort,
                    target.InterfaceName);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SADP: Failed to send probe to {Broadcast}", target.Address);
            }
        }

        try
        {
            await socket.SendToAsync(
                probeBytes,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Broadcast, SadpPort),
                timeoutCts.Token);
        }
        catch
        {
            // Best-effort fallback; interface-specific broadcasts above are the primary path.
        }

        var buffer = new byte[8192];
        Log.Information("SADP: Listening for device responses for {TimeoutMs}ms", timeoutMs);

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                var result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    remoteEp,
                    timeoutCts.Token);

                var remoteIp = ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
                var responseXml = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                var device = ParseSadpResponse(responseXml, remoteIp);

                if (device is null || localIps.Contains(device.IpAddress))
                {
                    continue;
                }

                devices.TryAdd(device.IpAddress, device);
                Log.Information(
                    "SADP: Found device - IP: {IP}, Serial: {Serial}, Type: {Type}",
                    device.IpAddress,
                    device.SerialNumber,
                    device.DeviceType);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Debug(ex, "SADP: Socket error while receiving");
                break;
            }
        }

        return devices.Values
            .OrderBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<List<DiscoveredDevice>> DiscoverDevicesByIsapiAsync(
        string username,
        string password,
        int timeoutMs = 3500,
        CancellationToken cancellationToken = default)
    {
        var neighborCandidates = GetNeighborCacheCandidates(includeVirtualInterfaces: false);
        var devices = await ProbeIsapiCandidatesAsync(
            username,
            password,
            neighborCandidates,
            timeoutMs: 5000,
            maxConcurrency: 8,
            scopeName: "neighbor cache",
            cancellationToken);

        if (devices.Count > 0)
        {
            return devices;
        }

        var physicalCandidates = GetSubnetScanCandidates(includeVirtualInterfaces: false);
        devices = await ProbeIsapiCandidatesAsync(
            username,
            password,
            physicalCandidates.Where(candidate => !neighborCandidates.Contains(candidate)).ToList(),
            timeoutMs,
            maxConcurrency: 16,
            scopeName: "physical subnet",
            cancellationToken);

        if (devices.Count > 0)
        {
            return devices;
        }

        var allCandidates = GetSubnetScanCandidates(includeVirtualInterfaces: true)
            .Where(candidate => !neighborCandidates.Contains(candidate))
            .Where(candidate => !physicalCandidates.Contains(candidate))
            .ToList();

        return await ProbeIsapiCandidatesAsync(
            username,
            password,
            allCandidates,
            timeoutMs: 2000,
            maxConcurrency: 32,
            scopeName: "all remaining subnets",
            cancellationToken);
    }

    public static async Task<DiscoveredDevice> DiscoverRequiredSingleDeviceAsync(
        string? username = null,
        string? password = null,
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

        if (lastDevices.Count == 0 &&
            !string.IsNullOrWhiteSpace(username) &&
            !string.IsNullOrWhiteSpace(password))
        {
            Log.Warning("SADP: No devices replied. Trying ISAPI subnet discovery with the configured credentials.");
            lastDevices = await DiscoverDevicesByIsapiAsync(username, password, cancellationToken: cancellationToken);
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
                "No Hikvision device was discovered by SADP or ISAPI subnet scan. Confirm the helper is on the same LAN as the device, the configured username/password are correct, and the device HTTP port is reachable.");
        }

        var found = string.Join(
            "; ",
            devices.Select(device => $"{device.IpAddress} serial={device.SerialNumber ?? "unknown"} type={device.DeviceType ?? "unknown"}"));

        throw new InvalidOperationException(
            "Multiple Hikvision devices were discovered, so the helper will not choose one automatically. Found: " + found);
    }

    private static Socket CreateSadpSocket()
    {
        Socket? socket = null;

        try
        {
            socket = NewBroadcastSocket();
            socket.Bind(new IPEndPoint(IPAddress.Any, SadpPort));
            return socket;
        }
        catch (SocketException ex)
        {
            Log.Warning(
                ex,
                "SADP: Could not bind to port {Port}. Falling back to an ephemeral UDP port; some devices may not reply.",
                SadpPort);
            socket?.Dispose();

            socket = NewBroadcastSocket();
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            return socket;
        }
    }

    private static Socket NewBroadcastSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        return socket;
    }

    private static async Task<List<DiscoveredDevice>> ProbeIsapiCandidatesAsync(
        string username,
        string password,
        IReadOnlyCollection<IPAddress> candidates,
        int timeoutMs,
        int maxConcurrency,
        string scopeName,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        Log.Information(
            "ISAPI discovery: probing {Count} {Scope} candidate IPs with timeout {TimeoutMs}ms",
            candidates.Count,
            scopeName,
            timeoutMs);

        var devices = new ConcurrentDictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        using var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 1500))
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = candidates.Select(async ip =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var device = await ProbeIsapiDeviceAsync(httpClient, ip, cancellationToken);
                if (device is not null)
                {
                    devices.TryAdd(device.IpAddress, device);
                    Log.Information(
                        "ISAPI discovery: found device - IP: {IP}, Serial: {Serial}, Type: {Type}",
                        device.IpAddress,
                        device.SerialNumber,
                        device.DeviceType);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Most candidate IPs will not be Hikvision devices.
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return devices.Values
            .OrderBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<DiscoveredDevice?> ProbeIsapiDeviceAsync(
        HttpClient httpClient,
        IPAddress ipAddress,
        CancellationToken cancellationToken)
    {
        var url = $"http://{ipAddress}:{HikVisionDefaults.HttpPort}/ISAPI/System/deviceInfo?format=json";
        using var response = await httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var (serial, deviceType) = ParseDeviceInfo(body);

        if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(deviceType))
        {
            return null;
        }

        return new DiscoveredDevice(ipAddress.ToString(), serial, deviceType, MacAddress: null);
    }

    private static List<IPAddress> GetSubnetScanCandidates(bool includeVirtualInterfaces)
    {
        var localIps = GetLocalIpAddresses();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsableNetworkInterface(ni, includeVirtualInterfaces))
            {
                continue;
            }

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork ||
                    addr.IPv4Mask is null ||
                    addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                var prefixLength = GetPrefixLength(addr.IPv4Mask);
                var scanPrefixLength = Math.Max(prefixLength, 24);

                foreach (var candidate in EnumerateSubnetHosts(addr.Address, scanPrefixLength))
                {
                    var value = candidate.ToString();
                    if (!localIps.Contains(value))
                    {
                        candidates.Add(value);
                    }
                }
            }
        }

        return candidates
            .Select(IPAddress.Parse)
            .OrderBy(address => address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<IPAddress> GetNeighborCacheCandidates(bool includeVirtualInterfaces)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "arp.exe",
                Arguments = "-a",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            if (process.ExitCode != 0)
            {
                return [];
            }

            var localIps = GetLocalIpAddresses();
            var localSubnets = GetLocalSubnetMemberships(includeVirtualInterfaces);
            var candidates = new List<IPAddress>();

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 ||
                    !IPAddress.TryParse(parts[0], out var ipAddress) ||
                    !string.Equals(parts[^1], "dynamic", StringComparison.OrdinalIgnoreCase) ||
                    localIps.Contains(ipAddress.ToString()) ||
                    !IsInAnyLocalSubnet(ipAddress, localSubnets))
                {
                    continue;
                }

                candidates.Add(ipAddress);
            }

            return candidates
                .Distinct()
                .OrderBy(address => address.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ISAPI discovery: failed to read Windows ARP neighbor cache");
            return [];
        }
    }

    private static List<(uint Network, uint Mask)> GetLocalSubnetMemberships(bool includeVirtualInterfaces)
    {
        var subnets = new List<(uint Network, uint Mask)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsableNetworkInterface(ni, includeVirtualInterfaces))
            {
                continue;
            }

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask is null)
                {
                    continue;
                }

                var mask = ToUInt32(addr.IPv4Mask);
                subnets.Add((ToUInt32(addr.Address) & mask, mask));
            }
        }

        return subnets;
    }

    private static bool IsInAnyLocalSubnet(IPAddress address, IReadOnlyCollection<(uint Network, uint Mask)> subnets)
    {
        var value = ToUInt32(address);
        return subnets.Any(subnet => (value & subnet.Mask) == subnet.Network);
    }

    private static List<BroadcastTarget> GetSubnetBroadcastAddresses()
    {
        var addresses = new List<BroadcastTarget>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsableNetworkInterface(ni, includeVirtualInterfaces: true))
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask is null)
                    continue;

                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];

                for (var i = 0; i < 4; i++)
                {
                    broadcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                }

                addresses.Add(new BroadcastTarget(new IPAddress(broadcast), ni.Name));
            }
        }

        return addresses
            .GroupBy(target => target.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsUsableNetworkInterface(NetworkInterface networkInterface, bool includeVirtualInterfaces)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
            return false;
        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return false;

        return includeVirtualInterfaces || !IsLikelyVirtualInterface(networkInterface);
    }

    private static bool IsLikelyVirtualInterface(NetworkInterface networkInterface)
    {
        var name = networkInterface.Name + " " + networkInterface.Description;

        return name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WSL", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<IPAddress> EnumerateSubnetHosts(IPAddress address, int prefixLength)
    {
        var ip = ToUInt32(address);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var network = ip & mask;
        var broadcast = network | ~mask;

        for (var current = network + 1; current < broadcast; current++)
        {
            yield return FromUInt32(current);
        }
    }

    private static int GetPrefixLength(IPAddress subnetMask)
    {
        var bytes = subnetMask.GetAddressBytes();
        var prefixLength = 0;

        foreach (var b in bytes)
        {
            var value = b;
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) == 0)
                {
                    return prefixLength;
                }

                prefixLength++;
            }
        }

        return prefixLength;
    }

    private static DiscoveredDevice? ParseSadpResponse(string xml, string remoteIp)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var probeMatch = doc.Descendants(ns + "ProbeMatch").FirstOrDefault()
                          ?? doc.Descendants("ProbeMatch").FirstOrDefault();

            if (probeMatch is null)
            {
                return null;
            }

            string? GetValue(params string[] names)
            {
                foreach (var name in names)
                {
                    var value = probeMatch.Element(ns + name)?.Value?.Trim()
                             ?? probeMatch.Element(name)?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                return null;
            }

            var serial = GetValue("DeviceSN", "SerialNumber");
            var deviceType = GetValue("DeviceType", "DeviceDescription");

            if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(deviceType))
            {
                return null;
            }

            return new DiscoveredDevice(
                GetValue("IPv4Address") ?? remoteIp,
                serial,
                deviceType,
                GetValue("MAC"));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SADP: Failed to parse response XML from {IP}", remoteIp);
            return null;
        }
    }

    private static (string? SerialNumber, string? DeviceType) ParseDeviceInfo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var info = root.TryGetProperty("DeviceInfo", out var deviceInfo) ? deviceInfo : root;

            return (
                ReadJsonString(info, "serialNumber"),
                ReadJsonString(info, "deviceType") ?? ReadJsonString(info, "model"));
        }
        catch (JsonException)
        {
            return ParseDeviceInfoXml(body);
        }
    }

    private static (string? SerialNumber, string? DeviceType) ParseDeviceInfoXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            string? ReadXmlString(string name) =>
                doc.Descendants(ns + name).FirstOrDefault()?.Value?.Trim()
                ?? doc.Descendants(name).FirstOrDefault()?.Value?.Trim();

            return (ReadXmlString("serialNumber"), ReadXmlString("deviceType") ?? ReadXmlString("model"));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.ToString()
            : null;
    }

    private static bool IsSadpPortInUse()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Any(endpoint => endpoint.Port == SadpPort);
        }
        catch
        {
            return false;
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress([
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);
    }

    private static HashSet<string> GetLocalIpAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
