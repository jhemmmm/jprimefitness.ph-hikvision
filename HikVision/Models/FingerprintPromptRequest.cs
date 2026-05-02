namespace HikVision.Models;

public class FingerprintPromptRequest
{
    public required string EmployeeNo { get; set; }
    public int FingerprintId { get; set; } = 1;
    public int? TimeoutSeconds { get; set; }
    public bool SaveToDevice { get; set; } = true;
    public bool? IncludeDebug { get; set; }
    public bool IncludeFingerprintData { get; set; } = true;
    public int[]? CardReaders { get; set; }
    public string? FingerprintData { get; set; }
}
