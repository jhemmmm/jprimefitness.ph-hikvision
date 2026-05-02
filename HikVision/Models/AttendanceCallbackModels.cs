namespace HikVision.Models;

public class AttendanceCallbackReceiveResponse
{
    public string Status { get; set; } = "success";
    public string Message { get; set; } = "Attendance callback received";
    public long CallbackCount { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    public AttendanceCallbackSnapshot? Callback { get; set; }
}

public class AttendanceCallbackLastResponse
{
    public string Status { get; set; } = "empty";
    public string Message { get; set; } = "No attendance callback has been received yet.";
    public AttendanceCallbackSnapshot? Callback { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}

public class AttendanceCallbackSnapshot
{
    public long CallbackCount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? QueryString { get; set; }
    public string? ContentType { get; set; }
    public string? RemoteIpAddress { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public string? Body { get; set; }
    public string ReceivedAt { get; set; } = DateTime.UtcNow.ToString("O");
    public AttendanceForwardPayload? NormalizedEvent { get; set; }
    public AttendanceForwardResult? ForwardResult { get; set; }
}

public class AttendanceForwardPayload
{
    public string? DeviceIp { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceSerial { get; set; }
    public string? EmployeeNo { get; set; }
    public int? EventSerialNo { get; set; }
    public string EventType { get; set; } = "AccessControllerEvent";
    public int? EventSubType { get; set; }
    public int? MajorEventType { get; set; }
    public string? OccurredAt { get; set; }
    public int? CardReaderNo { get; set; }
    public int? DoorNo { get; set; }
    public string? UserType { get; set; }
    public string? AttendanceStatus { get; set; }
    public System.Text.Json.JsonElement Raw { get; set; }
}

public class AttendanceForwardResult
{
    public bool Attempted { get; set; }
    public string Status { get; set; } = "skipped";
    public string Message { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string? ResponseBody { get; set; }
}
