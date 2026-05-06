namespace HikVision.Services;

public sealed class HikVisionDeviceState
{
    private readonly object _sync = new();

    public string? DeviceIp { get; private set; }
    public string? DeviceSerial { get; private set; }
    public string? DeviceType { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public string? CallbackUrl { get; private set; }

    public bool IsReady => !string.IsNullOrWhiteSpace(DeviceIp);

    public void SetDiscoveredDevice(string deviceIp, string? serialNumber, string? deviceType)
    {
        if (string.IsNullOrWhiteSpace(deviceIp))
            throw new ArgumentException("Device IP is required.", nameof(deviceIp));

        lock (_sync)
        {
            DeviceIp = deviceIp.Trim();
            DeviceSerial = string.IsNullOrWhiteSpace(serialNumber) ? DeviceSerial : serialNumber.Trim();
            DeviceType = string.IsNullOrWhiteSpace(deviceType) ? DeviceType : deviceType.Trim();
        }
    }

    public void SetDeviceInfo(string? serialNumber, string? deviceType, string? firmwareVersion)
    {
        lock (_sync)
        {
            DeviceSerial = string.IsNullOrWhiteSpace(serialNumber) ? DeviceSerial : serialNumber.Trim();
            DeviceType = string.IsNullOrWhiteSpace(deviceType) ? DeviceType : deviceType.Trim();
            FirmwareVersion = string.IsNullOrWhiteSpace(firmwareVersion) ? FirmwareVersion : firmwareVersion.Trim();
        }
    }

    public void SetCallbackUrl(string callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new ArgumentException("Callback URL is required.", nameof(callbackUrl));

        lock (_sync)
        {
            CallbackUrl = callbackUrl.Trim();
        }
    }

    public string RequireDeviceIp()
    {
        var deviceIp = DeviceIp;
        if (string.IsNullOrWhiteSpace(deviceIp))
        {
            throw new InvalidOperationException(
                "The Hikvision device IP is not ready. Startup discovery must complete before device operations can run.");
        }

        return deviceIp;
    }

    public HikVisionDeviceSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new HikVisionDeviceSnapshot(
                IsReady,
                DeviceIp,
                DeviceSerial,
                DeviceType,
                FirmwareVersion,
                CallbackUrl);
        }
    }
}

public sealed record HikVisionDeviceSnapshot(
    bool IsReady,
    string? DeviceIp,
    string? DeviceSerial,
    string? DeviceType,
    string? FirmwareVersion,
    string? CallbackUrl);
