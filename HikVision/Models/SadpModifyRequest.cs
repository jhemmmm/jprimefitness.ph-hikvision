namespace HikVision.Models;

/// <summary>
/// Body for POST /sadp/modify. Mirrors the official SADP tool's "Modify Network Parameters" panel.
/// Mac defaults to the discovered device; Password defaults to the configured admin password.
/// </summary>
public class SadpModifyRequest
{
    public string? Mac { get; set; }
    public string? IpAddress { get; set; }
    public string? SubnetMask { get; set; }
    public string? Gateway { get; set; }
    public int? CommandPort { get; set; }
    public int? HttpPort { get; set; }
    public bool? Dhcp { get; set; }
    public string? Password { get; set; }
    public int? TimeoutMs { get; set; }
}
