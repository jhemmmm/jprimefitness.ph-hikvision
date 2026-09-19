using HikVision.Config;
using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class SadpDevicesHandler
{
    private const int MinTimeoutMs = 1000;
    private const int MaxTimeoutMs = 30000;
    private const int DefaultTimeoutMs = 5000;

    public static async Task<SadpDevicesResponse> Handle(
        int? timeoutMs,
        HikVisionConfig config,
        CancellationToken cancellationToken)
    {
        var logger = Log.ForContext(typeof(SadpDevicesHandler));
        var timeout = Math.Clamp(timeoutMs ?? DefaultTimeoutMs, MinTimeoutMs, MaxTimeoutMs);

        try
        {
            var portConflict = LocalNetwork.IsUdpPortInUse(SadpProtocol.Port);
            var devices = await HikVisionDeviceDiscovery.DiscoverDevicesAsync(timeout, cancellationToken);

            if (!config.Debug)
            {
                devices = devices.Select(device => device with { RawXml = null, Salt = null }).ToList();
            }

            return new SadpDevicesResponse
            {
                Status = "success",
                Message = devices.Count == 0
                    ? "No Hikvision devices replied. Close the SADP Tool if it is open and check that inbound UDP 37020 is allowed."
                    : $"{devices.Count} device(s) found.",
                Count = devices.Count,
                TimeoutMs = timeout,
                SadpPortInUseByOtherProcess = portConflict,
                Devices = devices
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in SADP devices handler");

            return new SadpDevicesResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                TimeoutMs = timeout
            };
        }
    }
}
