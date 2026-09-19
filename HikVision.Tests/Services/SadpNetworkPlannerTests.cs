using System.Net;
using HikVision.Services;

namespace HikVision.Tests.Services;

public class SadpNetworkPlannerTests
{
    private static readonly IPAddress Mask24 = IPAddress.Parse("255.255.255.0");

    private static LocalNetwork.LocalIpv4Interface Interface(
        string ip,
        string? gateway = "192.168.250.1",
        string name = "Ethernet",
        bool isVirtual = false,
        int index = 7)
    {
        var address = IPAddress.Parse(ip);
        var network = LocalNetwork.ToUInt32(address) & LocalNetwork.ToUInt32(Mask24);

        return new LocalNetwork.LocalIpv4Interface(
            name,
            index,
            address,
            Mask24,
            gateway is null ? null : IPAddress.Parse(gateway),
            LocalNetwork.FromUInt32(network | 0xFF),
            isVirtual,
            SupportsMulticast: true);
    }

    private static HikVisionDeviceDiscovery.DiscoveredDevice Device(string? receivedOn = "192.168.250.102", string? mac = "a4-d5-c2-4b-74-b1") =>
        new("192.168.1.3", "SN", "DS-K1A802AEF", mac,
            SubnetMask: "255.255.255.0",
            Gateway: "92.168.1.1",
            CommandPort: 8000,
            HttpPort: 80,
            SdkOverTlsPort: 8443,
            ReceivedOnLocalIp: receivedOn);

    [Fact]
    public void PickCandidateIp_prefers_the_address_after_the_helper()
    {
        var picked = SadpNetworkPlanner.PickCandidateIp(
            IPAddress.Parse("192.168.250.102"),
            Mask24,
            [IPAddress.Parse("192.168.250.1")],
            _ => false);

        Assert.Equal(IPAddress.Parse("192.168.250.103"), picked);
    }

    [Fact]
    public void PickCandidateIp_skips_in_use_and_reserved_addresses()
    {
        var inUse = new HashSet<string> { "192.168.250.103" };

        var picked = SadpNetworkPlanner.PickCandidateIp(
            IPAddress.Parse("192.168.250.102"),
            Mask24,
            [IPAddress.Parse("192.168.250.104")],
            ip => inUse.Contains(ip.ToString()));

        Assert.Equal(IPAddress.Parse("192.168.250.105"), picked);
    }

    [Fact]
    public void PickCandidateIp_wraps_around_at_broadcast()
    {
        var probed = new List<string>();

        var picked = SadpNetworkPlanner.PickCandidateIp(
            IPAddress.Parse("192.168.250.254"),
            Mask24,
            [IPAddress.Parse("192.168.250.1")],
            ip =>
            {
                probed.Add(ip.ToString());
                return false;
            });

        Assert.Equal(IPAddress.Parse("192.168.250.2"), picked);
        Assert.Equal(["192.168.250.2"], probed);
    }

    [Fact]
    public void PickCandidateIp_never_probes_network_broadcast_or_own_address()
    {
        var probed = new List<string>();

        SadpNetworkPlanner.PickCandidateIp(
            IPAddress.Parse("192.168.250.102"),
            Mask24,
            [],
            ip =>
            {
                probed.Add(ip.ToString());
                return true;
            },
            maxAttempts: 200);

        Assert.DoesNotContain("192.168.250.0", probed);
        Assert.DoesNotContain("192.168.250.255", probed);
        Assert.DoesNotContain("192.168.250.102", probed);
    }

    [Fact]
    public void PickCandidateIp_returns_null_after_max_attempts()
    {
        var probes = 0;

        var picked = SadpNetworkPlanner.PickCandidateIp(
            IPAddress.Parse("192.168.250.102"),
            Mask24,
            [],
            _ =>
            {
                probes++;
                return true;
            },
            maxAttempts: 3);

        Assert.Null(picked);
        Assert.Equal(3, probes);
    }

    [Fact]
    public void IsInAnyLocalSubnet_detects_membership()
    {
        var subnets = new[] { (Network: LocalNetwork.ToUInt32(IPAddress.Parse("192.168.250.0")), Mask: LocalNetwork.ToUInt32(Mask24)) };

        Assert.False(LocalNetwork.IsInAnyLocalSubnet(IPAddress.Parse("192.168.1.3"), subnets));
        Assert.True(LocalNetwork.IsInAnyLocalSubnet(IPAddress.Parse("192.168.250.103"), subnets));
    }

    [Fact]
    public void PlanOffSubnetFix_uses_receiving_interface_mask_gateway_and_device_ports()
    {
        var interfaces = new[]
        {
            Interface("10.0.0.5", gateway: "10.0.0.1", name: "Wi-Fi", index: 3),
            Interface("192.168.250.102", gateway: "192.168.250.1", name: "Ethernet", index: 7)
        };

        var plan = SadpNetworkPlanner.PlanOffSubnetFix(Device(), interfaces, _ => false);

        Assert.NotNull(plan);
        Assert.Equal("a4-d5-c2-4b-74-b1", plan.Mac);
        Assert.Equal("192.168.250.103", plan.IpAddress);
        Assert.Equal("255.255.255.0", plan.SubnetMask);
        Assert.Equal("192.168.250.1", plan.Gateway);
        Assert.Equal(8000, plan.CommandPort);
        Assert.Equal(80, plan.HttpPort);
        Assert.Equal(8443, plan.SdkOverTlsPort);
        Assert.False(plan.Dhcp);
    }

    [Fact]
    public void PlanOffSubnetFix_falls_back_to_first_physical_interface_and_network_plus_one_gateway()
    {
        var interfaces = new[]
        {
            Interface("172.16.5.20", gateway: null, name: "vEthernet (WSL)", isVirtual: true, index: 20),
            Interface("192.168.250.102", gateway: null, name: "Ethernet", index: 7)
        };

        var plan = SadpNetworkPlanner.PlanOffSubnetFix(Device(receivedOn: null), interfaces, _ => false);

        Assert.NotNull(plan);
        Assert.Equal("192.168.250.103", plan.IpAddress);
        Assert.Equal("192.168.250.1", plan.Gateway);
    }

    [Fact]
    public void PlanOffSubnetFix_returns_null_without_mac_or_interfaces()
    {
        Assert.Null(SadpNetworkPlanner.PlanOffSubnetFix(Device(mac: null), [Interface("192.168.250.102")], _ => false));
        Assert.Null(SadpNetworkPlanner.PlanOffSubnetFix(Device(), [], _ => false));
    }
}
