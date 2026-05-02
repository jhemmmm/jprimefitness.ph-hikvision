namespace HikVision.Models;

public class HealthResponse
{
    public string Status { get; set; } = "healthy";
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    public string Version { get; set; } = "1.0.0-helper";
}
