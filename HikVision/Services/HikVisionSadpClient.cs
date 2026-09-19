using Serilog;

namespace HikVision.Services;

/// <summary>
/// SADP "Modify Network Parameters": sends an update request to the multicast group / broadcast so it reaches the
/// device even when its current IP is on another subnet, then confirms by re-discovering it at the new address.
/// </summary>
public static class HikVisionSadpClient
{
    public sealed record SadpUpdateResult(
        bool Success,
        string Result,
        int? ErrorCode,
        string Mac,
        string NewIp,
        string? RawResponse,
        HikVisionDeviceDiscovery.DiscoveredDevice? Confirmed)
    {
        /// <summary>The device answered but refused the plaintext password (encrypted-password firmware).</summary>
        public bool IsPasswordRejected => Result.Contains("Password", StringComparison.OrdinalIgnoreCase);

        /// <summary>The device temporarily locked SADP writes after repeated failed attempts.</summary>
        public bool IsLocked => Result.Contains("lock", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Human-readable, actionable guidance shown when an automatic SADP network change did not take effect. The user
    /// runs the official SADP tool once; after that the helper's discovery finds the device on the local subnet.
    /// </summary>
    public static string BuildManualFixGuidance(SadpUpdateResult result, SadpNetworkUpdate target) =>
        BuildManualFixGuidance(ReasonFor(result), result.Mac, target);

    /// <summary>Overload for callers that know the reason up front (e.g. an Encrypt=true device never worth probing).</summary>
    public static string BuildManualFixGuidance(string reason, string mac, SadpNetworkUpdate target) =>
        $"Could not change the device's network settings automatically. {reason}\r\n" +
        $"Fix it manually: open the Hikvision SADP tool, select MAC {mac}, and under \"Modify Network Parameters\" set " +
        $"IP Address {target.IpAddress}, Subnet Mask {target.SubnetMask}, Gateway {target.Gateway} (HTTP Port {target.HttpPort}, Port {target.CommandPort}), " +
        "enter the admin password, and click Modify. If the SADP tool also reports the device locked, wait for the lock to clear or reboot the device, then try again. " +
        "Restart the helper once the device is on the same subnet as this PC.";

    public const string EncryptedPasswordReason =
        "This firmware requires the encrypted password format that only the official Hikvision SADP tool sends; " +
        "the helper does not send a plain-text SADP password to Encrypt=true devices.";

    private static string ReasonFor(SadpUpdateResult result) => result switch
    {
        { IsLocked: true } =>
            "The device has temporarily locked configuration changes after repeated failed password attempts. " +
            "Wait a few minutes (or power-cycle the device) for the lock to clear.",
        { IsPasswordRejected: true } =>
            "This firmware requires the encrypted password format that only the official Hikvision SADP tool sends; " +
            "the helper's plain-text SADP password was rejected.",
        { Result: "no-reply" } =>
            "The device did not answer the SADP network change. Check that the admin password is correct and that no other SADP tool is open.",
        { Result: "send-failed" } =>
            "The SADP request could not be sent on any network interface.",
        _ => $"The device rejected the change (result={result.Result}, errorCode={result.ErrorCode?.ToString() ?? "none"})."
    };

    /// <summary>
    /// Sends the update once (to the multicast group on every interface plus subnet broadcasts) and waits up to
    /// <paramref name="timeoutMs"/> for the device's reply, matched on the request UUID.
    /// </summary>
    public static async Task<SadpUpdateResult> ModifyNetworkAsync(
        SadpNetworkUpdate update,
        string password,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        var mac = SadpProtocol.NormalizeMac(update.Mac)
            ?? throw new ArgumentException($"'{update.Mac}' is not a valid MAC address.", nameof(update));

        var uuid = SadpProtocol.NewUuid();
        var payload = SadpProtocol.ToBytes(SadpProtocol.BuildUpdateXml(uuid, update, password));

        Log.Information(
            "SADP: Sending network update to {Mac}: ip={IP} mask={Mask} gw={Gateway} cmdPort={CommandPort} httpPort={HttpPort} dhcp={Dhcp}",
            mac,
            update.IpAddress,
            update.SubnetMask,
            update.Gateway,
            update.CommandPort,
            update.HttpPort,
            update.Dhcp);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        using var socket = SadpSocket.Open();

        // SendToAllAsync already delivers the payload to the group on every interface plus each broadcast, so one call
        // covers UDP loss. Sending again would add failed-password attempts against lock-sensitive firmware for no gain;
        // authoritative confirmation is done by re-discovery in ModifyNetworkAndConfirmAsync.
        var sent = await socket.SendToAllAsync(payload, timeoutCts.Token);
        if (sent == 0)
        {
            Log.Error("SADP: Network update could not be sent on any interface");
            return new SadpUpdateResult(false, "send-failed", null, mac, update.IpAddress, null, null);
        }

        var buffer = new byte[16384];

        while (true)
        {
            var datagram = await socket.ReceiveAsync(buffer, timeoutCts.Token);
            if (datagram is null)
            {
                break;
            }

            var reply = SadpProtocol.ParseUpdateReply(datagram.Xml, uuid);
            if (reply is null)
            {
                continue;
            }

            Log.Debug("SADP: Update reply from {RemoteIp}: {Xml}", datagram.RemoteIp, datagram.Xml);

            if (reply.IsSuccess)
            {
                Log.Information("SADP: Device {Mac} accepted the network update (result={Result})", mac, reply.Result);
            }
            else
            {
                Log.Warning(
                    "SADP: Device {Mac} rejected the network update: result={Result}, errorCode={ErrorCode}",
                    mac,
                    reply.Result,
                    reply.ErrorCode);
            }

            return new SadpUpdateResult(reply.IsSuccess, reply.Result, reply.ErrorCode, mac, update.IpAddress, reply.RawXml, null);
        }

        Log.Warning("SADP: No reply to the network update from {Mac} within {TimeoutMs}ms", mac, timeoutMs);
        return new SadpUpdateResult(false, "no-reply", null, mac, update.IpAddress, null, null);
    }

    /// <summary>
    /// Sends the update, waits for the device to apply it, then re-discovers. Success when the device reported
    /// success or when re-discovery shows the MAC at the requested IP (the device's unicast reply is often
    /// unroutable when it still sits on another subnet, so re-discovery is the authoritative check).
    /// </summary>
    public static async Task<SadpUpdateResult> ModifyNetworkAndConfirmAsync(
        SadpNetworkUpdate update,
        string password,
        int replyTimeoutMs = 5000,
        int settleDelayMs = 4000,
        int rediscoverAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        var result = await ModifyNetworkAsync(update, password, replyTimeoutMs, cancellationToken);

        // Re-discovery only helps two cases: an accepted change (confirm it landed), or no reply (the device may be
        // off-subnet and its unicast reply unroutable). An explicit rejection (PasswordError, locked, failed,
        // send-failed) is definitive — re-discovering would just waste the settle delay and extra socket passes.
        if (!result.Success && result.Result != "no-reply")
        {
            return result;
        }

        await Task.Delay(settleDelayMs, cancellationToken);

        HikVisionDeviceDiscovery.DiscoveredDevice? confirmed = null;
        for (var attempt = 1; attempt <= rediscoverAttempts && confirmed is null; attempt++)
        {
            var devices = await HikVisionDeviceDiscovery.DiscoverDevicesAsync(cancellationToken: cancellationToken);
            confirmed = devices.FirstOrDefault(d =>
                string.Equals(SadpProtocol.NormalizeMac(d.MacAddress), result.Mac, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.IpAddress, update.IpAddress, StringComparison.OrdinalIgnoreCase));

            if (confirmed is null && attempt < rediscoverAttempts)
            {
                var stillAtOld = devices.FirstOrDefault(d =>
                    string.Equals(SadpProtocol.NormalizeMac(d.MacAddress), result.Mac, StringComparison.OrdinalIgnoreCase));
                Log.Information(
                    "SADP: Device {Mac} not yet seen at {NewIp} (attempt {Attempt}/{Attempts}; currently {CurrentIp}); waiting...",
                    result.Mac,
                    update.IpAddress,
                    attempt,
                    rediscoverAttempts,
                    stillAtOld?.IpAddress ?? "not visible");
                await Task.Delay(2000, cancellationToken);
            }
        }

        if (confirmed is not null)
        {
            Log.Information("SADP: Confirmed device {Mac} now at {NewIp}", result.Mac, confirmed.IpAddress);
            // Keep Result as the raw protocol token; Confirmed carries the "seen at the new IP" fact separately.
            return result with { Success = true, Confirmed = confirmed };
        }

        Log.Warning("SADP: Could not confirm device {Mac} at {NewIp} after the update", result.Mac, update.IpAddress);
        return result;
    }
}
