using HikVision.Config;
using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

/// <summary>
/// Changes the current (reachable) device's network settings over ISAPI. Use this on a network where the device is
/// already reachable. For a device that arrives on a new network with an off-subnet static IP, give the PC a temporary
/// address in the device's subnet first (see the README redeploy note) so ISAPI can reach it.
/// </summary>
public static class DeviceNetworkHandler
{
    public static async Task<DeviceNetworkResponse> Handle(
        DeviceNetworkRequest request,
        HikVisionConfig config,
        HikVisionDeviceState deviceState,
        HikVisionIsapiClient isapiClient,
        HikVisionSdkClient sdkClient,
        CancellationToken cancellationToken)
    {
        var logger = Log.ForContext(typeof(DeviceNetworkHandler));
        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();

        if (mode != "dhcp" && mode != "static")
        {
            return Failed("mode must be \"dhcp\" or \"static\".", mode);
        }

        string? ip = null, mask = null, gw = null, dns1 = null, dns2 = null;
        if (mode == "static")
        {
            if (!LocalNetwork.TryParseIpv4(request.IpAddress, out var parsedIp))
            {
                return Failed("ipAddress must be a valid IPv4 address for static mode.", mode);
            }

            if (!LocalNetwork.TryParseIpv4(request.SubnetMask, out var parsedMask))
            {
                return Failed("subnetMask must be a valid IPv4 mask for static mode.", mode);
            }

            if (!LocalNetwork.TryParseIpv4(request.Gateway, out var parsedGateway))
            {
                return Failed("gateway must be a valid IPv4 address for static mode.", mode);
            }

            ip = parsedIp;
            mask = parsedMask;
            gw = parsedGateway;

            if (!string.IsNullOrWhiteSpace(request.PrimaryDns))
            {
                if (!LocalNetwork.TryParseIpv4(request.PrimaryDns, out var parsedDns1))
                {
                    return Failed("primaryDns must be a valid IPv4 address.", mode);
                }

                dns1 = parsedDns1;
            }

            if (!string.IsNullOrWhiteSpace(request.SecondaryDns))
            {
                if (!LocalNetwork.TryParseIpv4(request.SecondaryDns, out var parsedDns2))
                {
                    return Failed("secondaryDns must be a valid IPv4 address.", mode);
                }

                dns2 = parsedDns2;
            }
        }

        if (!deviceState.IsReady)
        {
            return Failed("The device is not ready yet (startup discovery has not completed).", mode);
        }

        try
        {
            var (status, message, httpStatusCode, rawResponse) =
                await isapiClient.SetNetworkAsync(mode, ip, mask, gw, dns1, dns2);

            var applied = status is "success" or "applied";
            if (applied)
            {
                // The device changed its own IP, so the helper must follow it. For static we know the new IP; for DHCP
                // we re-discover by MAC to learn the leased address. Without this the helper keeps talking to the old IP.
                var newIp = mode == "static"
                    ? ip
                    : await FindLeasedIpAsync(deviceState.DeviceMac, cancellationToken);

                if (!string.IsNullOrWhiteSpace(newIp))
                {
                    await DeviceRepointer.RepointAsync(newIp, deviceState, isapiClient, sdkClient, logger);
                    logger.Information("Device network set to {Mode}; helper repointed to {Ip}.", mode, newIp);
                }
                else
                {
                    logger.Warning("Device switched to DHCP but its new IP could not be found; re-discover with GET /sadp/devices and restart if needed.");
                }
            }

            return new DeviceNetworkResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                Mode = mode,
                RequestedIpAddress = ip,
                RawResponse = config.Debug ? rawResponse : null
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in device network handler");
            return Failed($"Unexpected error: {ex.Message}", mode);
        }
    }

    /// <summary>Re-discovers the device by MAC after a DHCP switch to learn its newly leased IP.</summary>
    private static async Task<string?> FindLeasedIpAsync(string? deviceMac, CancellationToken cancellationToken)
    {
        var mac = SadpProtocol.NormalizeMac(deviceMac);
        if (mac is null)
        {
            return null;
        }

        // Give the device a moment to release its old lease and request a new one.
        await Task.Delay(4000, cancellationToken);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var devices = await HikVisionDeviceDiscovery.DiscoverDevicesAsync(cancellationToken: cancellationToken);
            var match = devices.FirstOrDefault(d =>
                string.Equals(SadpProtocol.NormalizeMac(d.MacAddress), mac, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.IpAddress;
            }

            if (attempt < 3)
            {
                await Task.Delay(2000, cancellationToken);
            }
        }

        return null;
    }

    private static DeviceNetworkResponse Failed(string message, string? mode) => new()
    {
        Status = "failed",
        Message = message,
        Mode = mode
    };
}
