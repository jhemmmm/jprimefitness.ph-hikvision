namespace HikVision.Models;

public class FingerprintDeleteResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public string? EmployeeNo { get; set; }
    public int? FingerprintId { get; set; }
    public string? DeleteStatus { get; set; }
    public string? RawResponse { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
