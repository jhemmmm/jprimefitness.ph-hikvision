using HikVision.Models;
using HikVision.Config;
using HikVision.Services;

namespace HikVision.Handlers;

public static class HealthHandler
{
    public static HealthResponse Handle(HikVisionConfig config, HikVisionDeviceState deviceState)
    {
        var snapshot = deviceState.Snapshot();

        return new HealthResponse
        {
            DeviceReady = snapshot.IsReady,
            DeviceIp = config.Debug ? snapshot.DeviceIp : null,
            DeviceSerial = config.Debug ? snapshot.DeviceSerial : null,
            DeviceType = config.Debug ? snapshot.DeviceType : null,
            FirmwareVersion = config.Debug ? snapshot.FirmwareVersion : null,
            CallbackUrl = config.Debug ? snapshot.CallbackUrl : null
        };
    }
}
