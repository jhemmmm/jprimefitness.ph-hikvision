namespace HikVision.Models;

public class AttendanceCallbackConfigResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public string? CallbackUrl { get; set; }
    public int? HostId { get; set; }
    public string? RawResponse { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
