using Serilog;

namespace HikVision.Services;

/// <summary>
/// Diagnostic fallback that can learn the device IP from a callback if startup discovery
/// was bypassed or the service is reused in tests. Normal startup discovery should make
/// this a no-op in production.
/// </summary>
public class DeviceAutoConfigService
{
    private readonly HikVisionDeviceState _deviceState;
    private readonly HikVisionIsapiClient _isapiClient;
    private int _configured; // 0 = pending, 1 = done

    public DeviceAutoConfigService(HikVisionDeviceState deviceState, HikVisionIsapiClient isapiClient)
    {
        _deviceState = deviceState;
        _isapiClient = isapiClient;
    }

    public void TryLearnFromCallback(string? deviceIpFromPayload, string? deviceNameFromPayload)
    {
        if (_deviceState.IsReady)
            return;

        if (Interlocked.CompareExchange(ref _configured, 1, 0) != 0)
            return;

        if (string.IsNullOrWhiteSpace(deviceIpFromPayload))
        {
            Log.Warning("DeviceAutoConfig: Callback received but no ipAddress was present in the payload.");
            Interlocked.Exchange(ref _configured, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Log.Information("DeviceAutoConfig: Learned device IP {IP} from callback. Configuring runtime state...", deviceIpFromPayload);
                _deviceState.SetDiscoveredDevice(deviceIpFromPayload, deviceNameFromPayload, deviceType: null);

                var (success, fetchedSerial, deviceType, firmware) = await _isapiClient.GetDeviceInfoAsync();
                if (success)
                {
                    _deviceState.SetDeviceInfo(fetchedSerial, deviceType, firmware);
                }

                await ReRegisterCallbackAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DeviceAutoConfig: Failed to complete fallback auto-configuration from callback");
                Interlocked.Exchange(ref _configured, 0);
            }
        });
    }

    private async Task ReRegisterCallbackAsync()
    {
        var callbackUrl = Handlers.AttendanceCallbackConfigHandler.ResolveCallbackUrl(_deviceState);
        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            Log.Warning("DeviceAutoConfig: Could not resolve callback URL for re-registration.");
            return;
        }

        Log.Information("DeviceAutoConfig: Re-registering attendance callback to {CallbackUrl}", callbackUrl);

        var (status, message, httpStatusCode, _, _, _) =
            await _isapiClient.ConfigureAttendanceCallbackAsync(callbackUrl, Config.HikVisionDefaults.CallbackHostId);

        if (status == "success")
        {
            _deviceState.SetCallbackUrl(callbackUrl);
        }

        Log.Information(
            "DeviceAutoConfig: Callback re-registration result: status={Status}, httpStatusCode={HttpStatusCode}, message={Message}",
            status, httpStatusCode, message);
    }
}
