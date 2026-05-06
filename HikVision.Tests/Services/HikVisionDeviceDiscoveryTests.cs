using HikVision.Services;

namespace HikVision.Tests.Services;

public class HikVisionDeviceDiscoveryTests
{
    [Fact]
    public void RequireSingleDevice_returns_the_only_discovered_device()
    {
        var device = new HikVisionDeviceDiscovery.DiscoveredDevice("192.168.1.50", "SERIAL-1", "terminal", "00:11");

        var selected = HikVisionDeviceDiscovery.RequireSingleDevice([device]);

        Assert.Same(device, selected);
    }

    [Fact]
    public void RequireSingleDevice_fails_when_no_device_is_found()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => HikVisionDeviceDiscovery.RequireSingleDevice([]));

        Assert.Contains("No Hikvision device", error.Message);
    }

    [Fact]
    public void RequireSingleDevice_fails_when_multiple_devices_are_found()
    {
        var devices = new[]
        {
            new HikVisionDeviceDiscovery.DiscoveredDevice("192.168.1.50", "SERIAL-1", "terminal", "00:11"),
            new HikVisionDeviceDiscovery.DiscoveredDevice("192.168.1.51", "SERIAL-2", "terminal", "00:22")
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => HikVisionDeviceDiscovery.RequireSingleDevice(devices));

        Assert.Contains("Multiple Hikvision devices", error.Message);
        Assert.Contains("192.168.1.50", error.Message);
        Assert.Contains("192.168.1.51", error.Message);
    }
}
