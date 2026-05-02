namespace HikVision.Models;

public class PersonUpsertRequest
{
    public required string EmployeeNo { get; set; }
    public required string Name { get; set; }
    public string UserType { get; set; } = "normal";
    public bool ValidityEnabled { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public bool CheckUser { get; set; } = true;
    public bool CloseDelayEnabled { get; set; }
    public int NumberOfFingerprints { get; set; }
}
