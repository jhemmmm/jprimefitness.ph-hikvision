namespace HikVision.Config;

public class HikVisionConfig
{
    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string ForwardUrl { get; set; }
    public required string ForwardToken { get; set; }
    public bool Debug { get; set; }
}
