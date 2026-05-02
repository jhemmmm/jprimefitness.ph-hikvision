namespace HikVision.Models;

public class AttendanceSearchResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public int NumOfMatches { get; set; }
    public int TotalMatches { get; set; }
    public List<AttendanceEventRecord> Events { get; set; } = [];
    public string? RawResponse { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}

public class AttendanceEventRecord
{
    public string? EmployeeNo { get; set; }
    public string? Name { get; set; }
    public string? Time { get; set; }
    public string? VerifyMode { get; set; }
    public string? AttendanceStatus { get; set; }
    public int? SerialNo { get; set; }
    public int? Major { get; set; }
    public int? Minor { get; set; }
    public string? RawPayload { get; set; }
    public string? DeviceSerial { get; set; }
    public string? SourceIp { get; set; }
    public bool ForwardedToLaravel { get; set; }
    public string? ForwardStatus { get; set; }
}
