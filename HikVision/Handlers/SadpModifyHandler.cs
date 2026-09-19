using HikVision.Config;
using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class SadpModifyHandler
{
    private const int MinTimeoutMs = 1000;
    private const int MaxTimeoutMs = 30000;
    private const int DefaultTimeoutMs = 5000;

    public static async Task<SadpModifyResponse> Handle(
        SadpModifyRequest request,
        HikVisionConfig config,
        HikVisionDeviceState deviceState,
        HikVisionIsapiClient isapiClient,
        HikVisionSdkClient sdkClient,
        CancellationToken cancellationToken)
    {
        var logger = Log.ForContext(typeof(SadpModifyHandler));

        var currentMac = SadpProtocol.NormalizeMac(deviceState.DeviceMac);
        var mac = SadpProtocol.NormalizeMac(request.Mac) ?? currentMac;
        if (mac is null)
        {
            return Failed("mac is required (no MAC was given and the current device MAC is not known).");
        }

        if (!LocalNetwork.TryParseIpv4(request.IpAddress, out var ipAddress))
        {
            return Failed("ipAddress must be a valid IPv4 address.", mac);
        }

        if (!LocalNetwork.TryParseIpv4(request.SubnetMask, out var subnetMask))
        {
            return Failed("subnetMask must be a valid IPv4 mask.", mac);
        }

        if (!LocalNetwork.TryParseIpv4(request.Gateway, out var gateway))
        {
            return Failed("gateway must be a valid IPv4 address.", mac);
        }

        var update = new SadpNetworkUpdate(
            mac,
            ipAddress,
            subnetMask,
            gateway,
            request.CommandPort ?? HikVisionDefaults.SdkPort,
            request.HttpPort ?? HikVisionDefaults.HttpPort,
            request.Dhcp ?? false);

        var password = string.IsNullOrEmpty(request.Password) ? config.Password : request.Password;
        var timeout = Math.Clamp(request.TimeoutMs ?? DefaultTimeoutMs, MinTimeoutMs, MaxTimeoutMs);
        var isCurrentDevice = string.Equals(mac, currentMac, StringComparison.OrdinalIgnoreCase);

        // Encrypt=true firmware rejects a plain-text SADP password and locks itself after repeated attempts. Startup
        // already refuses to send in that case; guard the endpoint too so a modify call can't lock the device.
        if (isCurrentDevice && string.Equals(deviceState.DeviceEncrypt, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new SadpModifyResponse
            {
                Status = "failed",
                Message = HikVisionSadpClient.BuildManualFixGuidance(HikVisionSadpClient.EncryptedPasswordReason, mac, update),
                Result = "encrypted-password",
                Mac = mac,
                NewIp = update.IpAddress,
                IsCurrentDevice = isCurrentDevice
            };
        }

        try
        {
            logger.Information("Modifying network parameters of {Mac} via SADP (current device: {IsCurrent})", mac, isCurrentDevice);

            var result = await HikVisionSadpClient.ModifyNetworkAndConfirmAsync(
                update,
                password,
                replyTimeoutMs: timeout,
                cancellationToken: cancellationToken);

            if (result.Success && isCurrentDevice)
            {
                await DeviceRepointer.RepointAsync(result.NewIp, deviceState, isapiClient, sdkClient, logger);
            }

            return new SadpModifyResponse
            {
                Status = result.Success ? "success" : "failed",
                Message = result.Success
                    ? $"Device {mac} now uses {result.NewIp}/{update.SubnetMask} gateway {update.Gateway}."
                    : HikVisionSadpClient.BuildManualFixGuidance(result, update),
                Result = result.Result,
                ErrorCode = result.ErrorCode,
                Mac = mac,
                NewIp = result.NewIp,
                Confirmed = result.Confirmed is not null,
                IsCurrentDevice = isCurrentDevice,
                RawResponse = config.Debug ? result.RawResponse : null
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in SADP modify handler");
            return Failed($"Unexpected error: {ex.Message}", mac);
        }
    }

    private static SadpModifyResponse Failed(string message, string? mac = null) => new()
    {
        Status = "failed",
        Message = message,
        Mac = mac
    };
}
