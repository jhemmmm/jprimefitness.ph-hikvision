using HikVision.Services;

namespace HikVision.Models;

public class SadpDevicesResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int Count { get; set; }
    public int TimeoutMs { get; set; }
    /// <summary>True when another process (usually the Hikvision SADP Tool) already listens on UDP 37020.</summary>
    public bool SadpPortInUseByOtherProcess { get; set; }
    public List<HikVisionDeviceDiscovery.DiscoveredDevice> Devices { get; set; } = [];
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
