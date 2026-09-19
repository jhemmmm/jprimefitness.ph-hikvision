namespace HikVision.Models;

public class DeviceNetworkResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public string? Mode { get; set; }
    public string? RequestedIpAddress { get; set; }
    public string? RawResponse { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
