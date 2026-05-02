namespace HikVision.Models;

public class FingerprintPromptResponse
{
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public int SdkErrorCode { get; set; }
    public string? EmployeeNo { get; set; }
    public int? FingerprintId { get; set; }
    public int? FingerprintDataSize { get; set; }
    public int? FingerprintQuality { get; set; }
    public string? FingerprintDataBase64 { get; set; }
    public bool SavedToDevice { get; set; }
    public string? SaveStatus { get; set; }
    public string? SaveMessage { get; set; }
    public int? SaveHttpStatusCode { get; set; }
    public string? SaveRawResponse { get; set; }
    public FingerprintDebugInfo? Debug { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}

public class FingerprintDebugInfo
{
    public int CallbackInvocationCount { get; set; }
    public uint? LastCallbackStatus { get; set; }
    public int? LastProgress { get; set; }
    public int? LastRemoteConfigState { get; set; }
    public List<string> Events { get; set; } = [];
}
