using HikVision.Config;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

/// <summary>
/// Repoints the helper at the current device after its IP changed (SADP update or ISAPI network change): updates
/// runtime state, drops the SDK session pinned to the old IP, and re-registers the attendance callback for the new
/// subnet. Shared by the SADP and ISAPI network handlers.
/// </summary>
internal static class DeviceRepointer
{
    public static async Task RepointAsync(
        string newIp,
        HikVisionDeviceState deviceState,
        HikVisionIsapiClient isapiClient,
        HikVisionSdkClient sdkClient,
        Serilog.ILogger logger)
    {
        deviceState.SetDeviceIp(newIp);

        // The SDK session (if any) is pinned to the old IP.
        sdkClient.Logout();

        // The helper's callback URL is chosen relative to the device subnet, so re-register it best-effort.
        var callbackUrl = AttendanceCallbackConfigHandler.ResolveCallbackUrl(deviceState);
        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            logger.Warning("Device moved to {NewIp} but no callback URL could be resolved for the new subnet", newIp);
            return;
        }

        try
        {
            var (status, message, httpStatusCode, _, _, _) =
                await isapiClient.ConfigureAttendanceCallbackAsync(callbackUrl, HikVisionDefaults.CallbackHostId);

            if (status == "success")
            {
                deviceState.SetCallbackUrl(callbackUrl);
                logger.Information("Re-registered attendance callback {CallbackUrl} after device moved to {NewIp}", callbackUrl, newIp);
            }
            else
            {
                logger.Warning(
                    "Could not re-register attendance callback after device moved to {NewIp}: status={Status}, http={HttpStatusCode}, message={Message}",
                    newIp,
                    status,
                    httpStatusCode,
                    message);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Callback re-registration failed after device moved to {NewIp}", newIp);
        }
    }
}
