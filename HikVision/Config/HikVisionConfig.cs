namespace HikVision.Config;

public class HikVisionConfig
{
    /// <summary>
    /// IP address of the Hikvision device. If not set, auto-discovered via SADP broadcast.
    /// </summary>
    public string? DeviceIp { get; set; }
    public int HttpPort { get; set; } = 80;
    public int SdkPort { get; set; } = 8000;
    public required string Username { get; set; }
    public required string Password { get; set; }
    /// <summary>
    /// Serial number of the device. If not set, fetched automatically from the device on startup.
    /// </summary>
    public string? DeviceSerial { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string BindUrl { get; set; } = "http://127.0.0.1:5077";
    public string CallbackPath { get; set; } = "/attendance/callback";
    public string? CallbackToken { get; set; }
    public int CallbackHostId { get; set; } = 2;
    public string? ForwardUrl { get; set; }
    public string? ForwardToken { get; set; }
    public bool IncludeDebugInResponses { get; set; }
    public bool EnableVerboseSdkTracing { get; set; } = true;
}
