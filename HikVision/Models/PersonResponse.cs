namespace HikVision.Models;

public class PersonResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public string? EmployeeNo { get; set; }
    public string? Name { get; set; }
    public string? RawResponse { get; set; }
    public Dictionary<string, object?>? UserInfo { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
