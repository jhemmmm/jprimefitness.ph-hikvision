using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Serilog;

namespace HikVision.Services;

/// <summary>
/// Discovers Hikvision devices on the local network using the SADP UDP broadcast protocol (port 37020).
/// Hikvision devices respond to the same source port (37020), so the socket must be bound to that port.
/// </summary>
public static class HikVisionDeviceDiscovery
{
    private const int SadpPort = 37020;
    private const int DefaultTimeoutMs = 5000;

    public record DiscoveredDevice(string IpAddress, string? SerialNumber, string? DeviceType, string? MacAddress);

    /// <summary>
    /// Broadcasts an SADP inquiry on all local network interfaces and returns all responding Hikvision devices.
    /// </summary>
    public static async Task<List<DiscoveredDevice>> DiscoverDevicesAsync(int timeoutMs = DefaultTimeoutMs)
    {
        var devices = new List<DiscoveredDevice>();
        var localIps = GetLocalIpAddresses();
        var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var probeXml = $"""<?xml version="1.0" encoding="utf-8"?><Probe><Uuid>{uuid}</Uuid><Types>inquiry</Types></Probe>""";
        var probeBytes = Encoding.UTF8.GetBytes(probeXml);

        // Collect all subnet broadcast addresses from active IPv4 interfaces
        var broadcastAddresses = GetSubnetBroadcastAddresses();
        if (broadcastAddresses.Count == 0)
        {
            Log.Warning("SADP: No active IPv4 network interfaces found for broadcast");
            return devices;
        }

        // Bind to port 37020 — Hikvision devices unicast their reply back to the sender's source port
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, SadpPort));
        }
        catch (SocketException ex)
        {
            Log.Warning(ex,
                "SADP: Could not bind to port {Port} (another process may be using it). " +
                "Falling back to ephemeral port — responses may not be received.", SadpPort);
            socket?.Dispose();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        using (socket)
        {
            // Send broadcast on every subnet
            foreach (var broadcast in broadcastAddresses)
            {
                try
                {
                    socket.SendTo(probeBytes, new IPEndPoint(broadcast, SadpPort));
                    Log.Information("SADP: Probe sent to {Broadcast}:{Port}", broadcast, SadpPort);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "SADP: Failed to send probe to {Broadcast}", broadcast);
                }
            }

            // Also try the global broadcast as a fallback
            try { socket.SendTo(probeBytes, new IPEndPoint(IPAddress.Broadcast, SadpPort)); }
            catch { /* best effort */ }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = new byte[4096];
            socket.ReceiveTimeout = 500;

            Log.Information("SADP: Listening for device responses (timeout: {TimeoutMs}ms)...", timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    var received = socket.ReceiveFrom(buffer, ref remoteEp);
                    var responseXml = Encoding.UTF8.GetString(buffer, 0, received);
                    var remoteIp = ((IPEndPoint)remoteEp).Address.ToString();

                    Log.Debug("SADP: Raw response from {IP}: {Xml}", remoteIp, responseXml);

                    var device = ParseSadpResponse(responseXml, remoteIp);
                    if (device is not null
                        && !localIps.Contains(device.IpAddress)
                        && !devices.Any(d => d.IpAddress == device.IpAddress))
                    {
                        Log.Information("SADP: Found device — IP: {IP}, Serial: {Serial}, Type: {Type}",
                            device.IpAddress, device.SerialNumber, device.DeviceType);
                        devices.Add(device);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    // Normal — keep looping until deadline
                }
                catch (SocketException ex)
                {
                    Log.Debug(ex, "SADP: Socket error while receiving");
                    break;
                }
            }
        }

        return devices;
    }

    private static List<IPAddress> GetSubnetBroadcastAddresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);

                addresses.Add(new IPAddress(broadcast));
                Log.Debug("SADP: Will broadcast to {Broadcast} (interface: {Name})",
                    new IPAddress(broadcast), ni.Name);
            }
        }

        return addresses;
    }

    private static DiscoveredDevice? ParseSadpResponse(string xml, string remoteIp)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // Must be a proper SADP ProbeMatch response — reject anything without this element
            var probeMatch = doc.Descendants(ns + "ProbeMatch").FirstOrDefault()
                          ?? doc.Descendants("ProbeMatch").FirstOrDefault();

            if (probeMatch is null)
            {
                Log.Debug("SADP: Ignoring non-ProbeMatch response from {IP}: {Xml}", remoteIp, xml);
                return null;
            }

            string? GetValue(params string[] names)
            {
                foreach (var name in names)
                {
                    var val = probeMatch.Element(ns + name)?.Value?.Trim()
                           ?? probeMatch.Element(name)?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
                return null;
            }

            // Hikvision uses <DeviceSN> in SADP responses; <SerialNumber> is a fallback alias
            var serial = GetValue("DeviceSN", "SerialNumber");
            var deviceType = GetValue("DeviceType", "DeviceDescription");

            // Without at least one identifying field this is not a Hikvision device
            if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(deviceType))
            {
                Log.Debug("SADP: Ignoring ProbeMatch with no serial/type from {IP}", remoteIp);
                return null;
            }

            var ipAddress = GetValue("IPv4Address") ?? remoteIp;
            var mac = GetValue("MAC");

            return new DiscoveredDevice(ipAddress, serial, deviceType, mac);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SADP: Failed to parse response XML from {IP}", remoteIp);
            return null;
        }
    }

    /// <summary>Returns all IPv4 addresses assigned to the local machine.</summary>
    private static HashSet<string> GetLocalIpAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToHashSet();
}
