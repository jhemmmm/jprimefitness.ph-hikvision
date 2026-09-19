using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace HikVision.Services;

/// <summary>
/// One UDP socket bound to the SADP port that is joined to the SADP multicast group on every usable IPv4 interface,
/// mirroring what the official SADP tool does. Sends go to the group per interface plus subnet broadcasts.
/// </summary>
internal sealed class SadpSocket : IDisposable
{
    // SIO_UDP_CONNRESET: stop Windows from surfacing ICMP "port unreachable" (from broadcast targets) as
    // SocketException 10054 on the next receive, which would otherwise end the listen loop early.
    private const int SioUdpConnReset = -1744830452;

    private static readonly IPAddress Group = IPAddress.Parse(SadpProtocol.GroupAddress);

    private readonly Socket _socket;

    public sealed record SadpDatagram(string Xml, string RemoteIp, string? ReceivedOnLocalIp, string? DestinationIp);

    private SadpSocket(Socket socket, IReadOnlyList<LocalNetwork.LocalIpv4Interface> interfaces)
    {
        _socket = socket;
        Interfaces = interfaces;
    }

    public IReadOnlyList<LocalNetwork.LocalIpv4Interface> Interfaces { get; }

    public static SadpSocket Open(bool includeVirtualInterfaces = true)
    {
        var interfaces = LocalNetwork.GetIpv4Interfaces(includeVirtualInterfaces);

        if (LocalNetwork.IsUdpPortInUse(SadpProtocol.Port))
        {
            Log.Warning(
                "SADP: UDP port {Port} is already in use on this machine. If the Hikvision SADP Tool is open, it may receive device replies instead of the helper.",
                SadpProtocol.Port);
        }

        var socket = NewSocket();

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, SadpProtocol.Port));
        }
        catch (SocketException ex)
        {
            Log.Error(
                ex,
                "SADP: Could not bind UDP port {Port}. Falling back to an ephemeral port, which cannot receive replies sent to the multicast group. Close the Hikvision SADP Tool and restart the helper.",
                SadpProtocol.Port);
            socket.Dispose();
            socket = NewSocket();
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        foreach (var iface in interfaces.Where(i => i.SupportsMulticast))
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(Group, iface.Address));

                Log.Information(
                    "SADP: Joined {Group}:{Port} on {Interface} ({LocalIp})",
                    SadpProtocol.GroupAddress,
                    SadpProtocol.Port,
                    iface.Name,
                    iface.Address);
            }
            catch (SocketException ex)
            {
                Log.Debug(ex, "SADP: Could not join multicast group on {Interface} ({LocalIp})", iface.Name, iface.Address);
            }
        }

        return new SadpSocket(socket, interfaces);
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to the multicast group once per interface, to each subnet's directed
    /// broadcast address, and to 255.255.255.255. Returns the number of successful sends.
    /// </summary>
    public async Task<int> SendToAllAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var sent = 0;
        var groupEndpoint = new IPEndPoint(Group, SadpProtocol.Port);

        foreach (var iface in Interfaces.Where(i => i.SupportsMulticast))
        {
            try
            {
                // 4-byte in_addr of the local interface selects the outgoing interface for the multicast send.
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface.Address.GetAddressBytes());
                await _socket.SendToAsync(payload, SocketFlags.None, groupEndpoint, cancellationToken);
                sent++;
                Log.Debug("SADP: Sent to {Group}:{Port} via {Interface} ({LocalIp})", SadpProtocol.GroupAddress, SadpProtocol.Port, iface.Name, iface.Address);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return sent;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SADP: Multicast send failed on {Interface} ({LocalIp})", iface.Name, iface.Address);
            }
        }

        foreach (var broadcast in Interfaces.Select(i => i.Broadcast).DistinctBy(b => b.ToString()))
        {
            try
            {
                await _socket.SendToAsync(payload, SocketFlags.None, new IPEndPoint(broadcast, SadpProtocol.Port), cancellationToken);
                sent++;
                Log.Debug("SADP: Sent to broadcast {Broadcast}:{Port}", broadcast, SadpProtocol.Port);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return sent;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SADP: Broadcast send failed to {Broadcast}", broadcast);
            }
        }

        try
        {
            await _socket.SendToAsync(payload, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, SadpProtocol.Port), cancellationToken);
            sent++;
        }
        catch
        {
            // Best effort; group and directed broadcasts above are the primary paths.
        }

        return sent;
    }

    /// <summary>
    /// Receives the next datagram. Returns null once <paramref name="cancellationToken"/> fires or the socket is closed.
    /// </summary>
    public async Task<SadpDatagram?> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveMessageFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    cancellationToken);

                var remoteIp = ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
                var xml = Encoding.UTF8.GetString(buffer.Span[..result.ReceivedBytes]).TrimEnd('\0');

                var interfaceIndex = result.PacketInformation.Interface;
                var receivedOn = Interfaces.FirstOrDefault(i => i.Index == interfaceIndex)?.Address.ToString();
                var destination = result.PacketInformation.Address?.ToString();

                return new SadpDatagram(xml, remoteIp, receivedOn, destination);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ICMP unreachable from a broadcast target, or an oversized datagram: keep listening.
                Log.Debug(ex, "SADP: Transient receive error; continuing");
            }
            catch (SocketException ex)
            {
                Log.Debug(ex, "SADP: Socket error while receiving");
                return null;
            }
        }

        return null;
    }

    public void Dispose() => _socket.Dispose();

    private static Socket NewSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);

        try
        {
            // Windows applies IP_MULTICAST_LOOP on the receive path: this hides our own group sends from us.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
        }
        catch (SocketException)
        {
            // Not fatal; our own probes are filtered by root element anyway.
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
                // Handled by the ConnectionReset filter in ReceiveAsync.
            }
        }

        return socket;
    }
}
