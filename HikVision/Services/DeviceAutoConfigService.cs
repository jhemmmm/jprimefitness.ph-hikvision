using HikVision.Config;
using Serilog;

namespace HikVision.Services;

/// <summary>
/// Learns the device IP and serial number from the first incoming attendance callback
/// when they could not be determined at startup (e.g. SADP discovery found nothing).
/// Once the IP is known the ISAPI client base address is updated and the callback
/// URL is re-registered on the device so future operations work immediately.
/// </summary>
public class DeviceAutoConfigService
{
    private readonly HikVisionConfig _config;
    private readonly HikVisionIsapiClient _isapiClient;
    private int _configured; // 0 = pending, 1 = done (interlocked)

    public DeviceAutoConfigService(HikVisionConfig config, HikVisionIsapiClient isapiClient)
    {
        _config = config;
        _isapiClient = isapiClient;
    }

    /// <summary>
    /// Call on every received callback. No-op once the device is already configured.
    /// Runs the heavy work on a background thread so it never delays the callback response.
    /// </summary>
    public void TryLearnFromCallback(string? deviceIpFromPayload, string? deviceNameFromPayload)
    {
        // Fast-path: already configured
        if (!string.IsNullOrWhiteSpace(_config.DeviceIp))
            return;

        // Only one goroutine should do the configuration work
        if (Interlocked.CompareExchange(ref _configured, 1, 0) != 0)
            return;

        var ip = deviceIpFromPayload;
        var serial = deviceNameFromPayload;

        if (string.IsNullOrWhiteSpace(ip))
        {
            Log.Warning("DeviceAutoConfig: Callback received but no ipAddress in payload — cannot auto-configure.");
            Interlocked.Exchange(ref _configured, 0); // allow retry on next callback
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Log.Information("DeviceAutoConfig: Learned device IP {IP} from first callback. Configuring...", ip);
                _config.DeviceIp = ip;

                // Prefer the real serial from ISAPI over the deviceName field
                var (success, fetchedSerial, deviceType, firmware) = await _isapiClient.GetDeviceInfoAsync();
                if (success && !string.IsNullOrWhiteSpace(fetchedSerial))
                {
                    _config.DeviceSerial = fetchedSerial;
                    Log.Information(
                        "DeviceAutoConfig: Serial={Serial}, Type={Type}, Firmware={Firmware}",
                        fetchedSerial, deviceType, firmware);
                }
                else if (!string.IsNullOrWhiteSpace(serial))
                {
                    _config.DeviceSerial = serial;
                    Log.Information("DeviceAutoConfig: Using deviceName as serial: {Serial}", serial);
                }
                else
                {
                    Log.Warning("DeviceAutoConfig: Could not determine device serial number.");
                }

                Log.Information(
                    "DeviceAutoConfig: Device configured — IP: {IP}, Serial: {Serial}",
                    _config.DeviceIp, _config.DeviceSerial);

                // Re-register the callback on the device now that we have its IP
                await ReRegisterCallbackAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DeviceAutoConfig: Failed to complete auto-configuration from callback");
                // Reset so next callback can retry
                _config.DeviceIp = null;
                Interlocked.Exchange(ref _configured, 0);
            }
        });
    }

    private async Task ReRegisterCallbackAsync()
    {
        try
        {
            var callbackUrl = Handlers.AttendanceCallbackConfigHandler.ResolveCallbackUrl(_config);

            if (string.IsNullOrWhiteSpace(callbackUrl))
            {
                Log.Warning("DeviceAutoConfig: Could not resolve callback URL for re-registration.");
                return;
            }

            Log.Information("DeviceAutoConfig: Re-registering attendance callback to {CallbackUrl}", callbackUrl);

            var (status, message, httpStatusCode, _, _, _) =
                await _isapiClient.ConfigureAttendanceCallbackAsync(callbackUrl, _config.CallbackHostId);

            Log.Information(
                "DeviceAutoConfig: Callback re-registration result: status={Status}, httpStatusCode={HttpStatusCode}, message={Message}",
                status, httpStatusCode, message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DeviceAutoConfig: Failed to re-register attendance callback");
        }
    }
}
