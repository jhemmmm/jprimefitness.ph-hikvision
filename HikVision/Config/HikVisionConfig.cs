namespace HikVision.Config;

public class HikVisionConfig
{
    public required string DeviceIp { get; set; }
    public int HttpPort { get; set; } = 80;
    public int SdkPort { get; set; } = 8000;
    public required string Username { get; set; }
    public required string Password { get; set; }
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
