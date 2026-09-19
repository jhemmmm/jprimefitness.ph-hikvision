using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Serilog;

namespace HikVision.Services;

/// <summary>
/// Pure SADP (Search Active Devices Protocol) message building and parsing. No sockets here.
/// </summary>
internal static partial class SadpProtocol
{
    public const string GroupAddress = "239.255.255.250";
    public const int Port = 37020;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public sealed record SadpUpdateReply(string Uuid, string? Mac, string Result, int? ErrorCode, string RawXml)
    {
        public bool IsSuccess => Result.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    public static string NewUuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    public static byte[] ToBytes(string xml) => Utf8NoBom.GetBytes(xml);

    public static string BuildInquiryXml(string uuid) =>
        $"""<?xml version="1.0" encoding="utf-8"?><Probe><Uuid>{uuid}</Uuid><Types>inquiry</Types></Probe>""";

    /// <summary>
    /// Builds the "Modify Network Parameters" request the official SADP tool sends. The device matches on MAC.
    /// </summary>
    public static string BuildUpdateXml(string uuid, SadpNetworkUpdate update, string password)
    {
        var mac = NormalizeMac(update.Mac) ?? throw new ArgumentException("A valid MAC address is required.", nameof(update));

        // <PWErrorParse>true</PWErrorParse> asks the device to return a parseable password result (success / PasswordError
        // / locked) rather than staying silent, matching the official SADP tool and other SADP clients.
        return
            $"""<?xml version="1.0" encoding="utf-8"?><Probe><Uuid>{uuid}</Uuid><Types>update</Types><PWErrorParse>true</PWErrorParse><MAC>{mac}</MAC><Password>{Escape(password)}</Password><IPv4Address>{update.IpAddress}</IPv4Address><CommandPort>{update.CommandPort}</CommandPort><HttpPort>{update.HttpPort}</HttpPort><IPv4SubnetMask>{update.SubnetMask}</IPv4SubnetMask><IPv4Gateway>{update.Gateway}</IPv4Gateway><IPv6Address>{update.IPv6Address}</IPv6Address><IPv6Gateway>{update.IPv6Gateway}</IPv6Gateway><IPv6MaskLen>{update.IPv6MaskLen}</IPv6MaskLen><DHCP>{(update.Dhcp ? "true" : "false")}</DHCP><SDKOverTLSPort>{update.SdkOverTlsPort}</SDKOverTLSPort></Probe>""";
    }

    public static HikVisionDeviceDiscovery.DiscoveredDevice? ParseProbeMatch(string xml, string remoteIp, string? receivedOnLocalIp = null)
    {
        var root = TryParseRoot(xml);
        if (root is null || root.Name.LocalName != "ProbeMatch")
        {
            return null;
        }

        var ns = root.Name.Namespace;
        string? Get(params string[] names)
        {
            foreach (var name in names)
            {
                var value = root.Element(ns + name)?.Value?.Trim()
                         ?? root.Element(name)?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        var types = Get("Types");
        if (types is not null && !types.Equals("inquiry", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var serial = Get("DeviceSN", "SerialNumber");
        var description = Get("DeviceDescription");
        var typeCode = Get("DeviceType");

        if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(typeCode))
        {
            return null;
        }

        return new HikVisionDeviceDiscovery.DiscoveredDevice(
            Get("IPv4Address") ?? remoteIp,
            serial,
            description ?? typeCode,
            NormalizeMac(Get("MAC")) ?? Get("MAC"),
            DeviceDescription: description,
            DeviceTypeCode: typeCode,
            SubnetMask: Get("IPv4SubnetMask"),
            Gateway: Get("IPv4Gateway"),
            CommandPort: ParseInt(Get("CommandPort")),
            HttpPort: ParseInt(Get("HttpPort")),
            Dhcp: ParseBool(Get("DHCP")),
            SoftwareVersion: Get("SoftwareVersion"),
            DspVersion: Get("DSPVersion"),
            BootTime: Get("BootTime"),
            Activated: ParseBool(Get("Activated")),
            Encrypt: Get("Encrypt"),
            Salt: Get("Salt"),
            IPv6Address: Get("IPv6Address"),
            IPv6Gateway: Get("IPv6Gateway"),
            IPv6MaskLen: ParseInt(Get("IPv6MaskLen")),
            EnhancedSdkPort: ParseInt(Get("EnhancedSDKPort")),
            SdkOverTlsPort: ParseInt(Get("SDKOverTLSPort")),
            PasswordResetAbility: ParseBool(Get("PasswordResetAbility")),
            Uuid: Get("Uuid"),
            ReceivedOnLocalIp: receivedOnLocalIp,
            RawXml: xml);
    }

    /// <summary>
    /// Parses the device's answer to an update request. Returns null unless it is a ProbeMatch of type "update"
    /// carrying the UUID we sent, so replies to other tools or other requests are ignored.
    /// </summary>
    public static SadpUpdateReply? ParseUpdateReply(string xml, string expectedUuid)
    {
        var root = TryParseRoot(xml);
        if (root is null || root.Name.LocalName != "ProbeMatch")
        {
            return null;
        }

        var ns = root.Name.Namespace;
        string? Get(string name) =>
            root.Element(ns + name)?.Value?.Trim() ?? root.Element(name)?.Value?.Trim();

        var uuid = Get("Uuid");
        if (uuid is null || !uuid.Equals(expectedUuid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var types = Get("Types");
        if (types is not null && !types.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var result = Get("Result");
        var errorCode = ParseInt(Get("ErrorCode"));

        if (string.IsNullOrWhiteSpace(result))
        {
            result = errorCode is null or 0 ? "success" : "failed";
        }

        return new SadpUpdateReply(uuid, NormalizeMac(Get("MAC")), result, errorCode, xml);
    }

    /// <summary>
    /// Normalizes a MAC to the dash-separated lower-case form SADP uses (a4-d5-c2-4b-74-b1). Null if not 6 hex octets.
    /// </summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }

        var hex = NonHexRegex().Replace(mac, string.Empty);
        if (hex.Length != 12)
        {
            return null;
        }

        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = hex.Substring(i * 2, 2).ToLowerInvariant();
        }

        return string.Join("-", parts);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static XElement? TryParseRoot(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(xml).Root;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SADP: Ignoring datagram that is not well-formed XML");
            return null;
        }
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? ParseBool(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            return false;
        }

        return null;
    }

    [GeneratedRegex("[^0-9A-Fa-f]")]
    private static partial Regex NonHexRegex();
}
