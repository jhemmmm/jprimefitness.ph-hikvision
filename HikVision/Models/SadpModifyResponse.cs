namespace HikVision.Models;

public class SadpModifyResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public string? Result { get; set; }
    public int? ErrorCode { get; set; }
    public string? Mac { get; set; }
    public string? NewIp { get; set; }
    public bool Confirmed { get; set; }
    public bool IsCurrentDevice { get; set; }
    public string? RawResponse { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
