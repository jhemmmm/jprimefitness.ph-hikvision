namespace HikVision.Models;

public class AcsAbilityResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int SdkErrorCode { get; set; }
    public string Source { get; set; } = "sdk";
    public string? AbilityXml { get; set; }
    public bool? SupportsCaptureFingerPrint { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
