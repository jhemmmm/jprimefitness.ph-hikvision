namespace HikVision.Models;

/// <summary>
/// Body for POST /device/network. Changes the current device's IPv4 settings over ISAPI (no SADP tool needed).
/// Mode is "dhcp" or "static"; for "static", ipAddress/subnetMask/gateway are required.
/// </summary>
public class DeviceNetworkRequest
{
    public string? Mode { get; set; }
    public string? IpAddress { get; set; }
    public string? SubnetMask { get; set; }
    public string? Gateway { get; set; }
    public string? PrimaryDns { get; set; }
    public string? SecondaryDns { get; set; }
}
