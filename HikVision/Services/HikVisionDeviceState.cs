namespace HikVision.Services;

public sealed class HikVisionDeviceState
{
    private readonly object _sync = new();

    public string? DeviceIp { get; private set; }
    public string? DeviceMac { get; private set; }
    public string? DeviceSerial { get; private set; }
    public string? DeviceType { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public string? CallbackUrl { get; private set; }

    /// <summary>The device's SADP Encrypt flag ("true" when it requires the encrypted-password format).</summary>
    public string? DeviceEncrypt { get; private set; }

    public bool IsReady => !string.IsNullOrWhiteSpace(DeviceIp);

    public void SetDiscoveredDevice(string deviceIp, string? serialNumber, string? deviceType, string? macAddress = null, string? encrypt = null)
    {
        if (string.IsNullOrWhiteSpace(deviceIp))
            throw new ArgumentException("Device IP is required.", nameof(deviceIp));

        lock (_sync)
        {
            DeviceIp = deviceIp.Trim();
            DeviceMac = string.IsNullOrWhiteSpace(macAddress) ? DeviceMac : macAddress.Trim();
            DeviceSerial = string.IsNullOrWhiteSpace(serialNumber) ? DeviceSerial : serialNumber.Trim();
            DeviceType = string.IsNullOrWhiteSpace(deviceType) ? DeviceType : deviceType.Trim();
            DeviceEncrypt = string.IsNullOrWhiteSpace(encrypt) ? DeviceEncrypt : encrypt.Trim();
        }
    }

    /// <summary>Points the helper at a new device IP after a SADP network update; other device data is kept.</summary>
    public void SetDeviceIp(string deviceIp)
    {
        if (string.IsNullOrWhiteSpace(deviceIp))
            throw new ArgumentException("Device IP is required.", nameof(deviceIp));

        lock (_sync)
        {
            DeviceIp = deviceIp.Trim();
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
                DeviceMac,
                DeviceSerial,
                DeviceType,
                FirmwareVersion,
                CallbackUrl,
                DeviceEncrypt);
        }
    }
}

public sealed record HikVisionDeviceSnapshot(
    bool IsReady,
    string? DeviceIp,
    string? DeviceMac,
    string? DeviceSerial,
    string? DeviceType,
    string? FirmwareVersion,
    string? CallbackUrl,
    string? DeviceEncrypt);
