namespace HikVision.Models;

public class HealthResponse
{
    public string Status { get; set; } = "healthy";
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    public string Version { get; set; } = "1.0.0-helper";
    public bool DeviceReady { get; set; }
    public string? DeviceIp { get; set; }
    public string? DeviceMac { get; set; }
    public string? DeviceSerial { get; set; }
    public string? DeviceType { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? CallbackUrl { get; set; }
}
