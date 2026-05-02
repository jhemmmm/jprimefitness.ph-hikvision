namespace HikVision.Models;

public class AttendanceSearchRequest
{
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public int Position { get; set; }
    public int MaxResults { get; set; } = 10;
    public string? EmployeeNo { get; set; }
    public int Major { get; set; } = 5;
    public int Minor { get; set; }
    public string EventAttribute { get; set; } = "attendance";
}
