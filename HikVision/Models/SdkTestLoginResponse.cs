namespace HikVision.Models;

public class SdkTestLoginResponse
{
    public string Status { get; set; } = "unknown"; // success | failed
    public string Message { get; set; } = string.Empty;
    public int SdkErrorCode { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
