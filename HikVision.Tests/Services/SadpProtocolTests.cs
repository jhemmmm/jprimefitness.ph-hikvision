using HikVision.Services;

namespace HikVision.Tests.Services;

public class SadpProtocolTests
{
    private const string ProbeMatchFixture =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <ProbeMatch>
          <Uuid>13A888A9-F1B1-4020-AE9F-05607682D23B</Uuid>
          <Types>inquiry</Types>
          <DeviceType>60002</DeviceType>
          <DeviceDescription>DS-K1A802AEF</DeviceDescription>
          <DeviceSN>DS-K1A802AEF20240318V010401ENFX7718760</DeviceSN>
          <CommandPort>8000</CommandPort>
          <HttpPort>80</HttpPort>
          <MAC>a4-d5-c2-4b-74-b1</MAC>
          <IPv4Address>192.168.1.3</IPv4Address>
          <IPv4SubnetMask>255.255.255.0</IPv4SubnetMask>
          <IPv4Gateway>92.168.1.1</IPv4Gateway>
          <IPv6Address>::</IPv6Address>
          <IPv6Gateway>::</IPv6Gateway>
          <IPv6MaskLen>0</IPv6MaskLen>
          <DHCP>false</DHCP>
          <AnalogChannelNum>0</AnalogChannelNum>
          <DigitalChannelNum>0</DigitalChannelNum>
          <SoftwareVersion>V1.4.1build 240318</SoftwareVersion>
          <DSPVersion>V1.0, build 240318</DSPVersion>
          <BootTime>2026-09-18 15:15:16</BootTime>
          <Encrypt>true</Encrypt>
          <ResetAbility>false</ResetAbility>
          <DiskNumber>0</DiskNumber>
          <Activated>true</Activated>
          <PasswordResetAbility>true</PasswordResetAbility>
          <Salt>0123456789abcdef0123456789abcdef</Salt>
          <EnhancedSDKPort>0</EnhancedSDKPort>
          <SDKOverTLSPort>8443</SDKOverTLSPort>
        </ProbeMatch>
        """;

    [Fact]
    public void ParseProbeMatch_parses_all_fields()
    {
        var device = SadpProtocol.ParseProbeMatch(ProbeMatchFixture, "192.168.1.3", "192.168.250.102");

        Assert.NotNull(device);
        Assert.Equal("192.168.1.3", device.IpAddress);
        Assert.Equal("DS-K1A802AEF20240318V010401ENFX7718760", device.SerialNumber);
        Assert.Equal("DS-K1A802AEF", device.DeviceType);
        Assert.Equal("DS-K1A802AEF", device.DeviceDescription);
        Assert.Equal("60002", device.DeviceTypeCode);
        Assert.Equal("a4-d5-c2-4b-74-b1", device.MacAddress);
        Assert.Equal("255.255.255.0", device.SubnetMask);
        Assert.Equal("92.168.1.1", device.Gateway);
        Assert.Equal(8000, device.CommandPort);
        Assert.Equal(80, device.HttpPort);
        Assert.False(device.Dhcp);
        Assert.Equal("V1.4.1build 240318", device.SoftwareVersion);
        Assert.Equal("V1.0, build 240318", device.DspVersion);
        Assert.Equal("2026-09-18 15:15:16", device.BootTime);
        Assert.True(device.Activated);
        Assert.Equal("true", device.Encrypt);
        Assert.Equal("0123456789abcdef0123456789abcdef", device.Salt);
        Assert.Equal("::", device.IPv6Address);
        Assert.Equal("::", device.IPv6Gateway);
        Assert.Equal(0, device.IPv6MaskLen);
        Assert.Equal(0, device.EnhancedSdkPort);
        Assert.Equal(8443, device.SdkOverTlsPort);
        Assert.True(device.PasswordResetAbility);
        Assert.Equal("13A888A9-F1B1-4020-AE9F-05607682D23B", device.Uuid);
        Assert.Equal("192.168.250.102", device.ReceivedOnLocalIp);
        Assert.Equal(ProbeMatchFixture, device.RawXml);
    }

    [Fact]
    public void ParseProbeMatch_returns_null_for_our_own_probe_echo()
    {
        var probe = SadpProtocol.BuildInquiryXml("13A888A9-F1B1-4020-AE9F-05607682D23B");

        Assert.Null(SadpProtocol.ParseProbeMatch(probe, "192.168.250.102"));
    }

    [Fact]
    public void ParseProbeMatch_falls_back_to_remote_ip_and_normalizes_mac()
    {
        const string xml = "<ProbeMatch><Types>inquiry</Types><DeviceSN>SN-1</DeviceSN><MAC>A4:D5:C2:4B:74:B1</MAC></ProbeMatch>";

        var device = SadpProtocol.ParseProbeMatch(xml, "10.0.0.7");

        Assert.NotNull(device);
        Assert.Equal("10.0.0.7", device.IpAddress);
        Assert.Equal("a4-d5-c2-4b-74-b1", device.MacAddress);
        Assert.Null(device.SubnetMask);
    }

    [Fact]
    public void ParseProbeMatch_ignores_update_replies()
    {
        const string xml = "<ProbeMatch><Uuid>X</Uuid><Types>update</Types><MAC>a4-d5-c2-4b-74-b1</MAC><Result>success</Result></ProbeMatch>";

        Assert.Null(SadpProtocol.ParseProbeMatch(xml, "192.168.1.3"));
    }

    [Fact]
    public void ParseProbeMatch_returns_null_for_garbage()
    {
        Assert.Null(SadpProtocol.ParseProbeMatch("not xml at all", "192.168.1.3"));
        Assert.Null(SadpProtocol.ParseProbeMatch("", "192.168.1.3"));
    }

    [Fact]
    public void BuildUpdateXml_matches_sadp_layout()
    {
        var update = new SadpNetworkUpdate(
            "A4:D5:C2:4B:74:B1",
            "192.168.250.103",
            "255.255.255.0",
            "192.168.250.1");

        var xml = SadpProtocol.BuildUpdateXml("13A888A9-F1B1-4020-AE9F-05607682D23B", update, "Secret1");

        Assert.Equal(
            """<?xml version="1.0" encoding="utf-8"?><Probe><Uuid>13A888A9-F1B1-4020-AE9F-05607682D23B</Uuid><Types>update</Types><PWErrorParse>true</PWErrorParse><MAC>a4-d5-c2-4b-74-b1</MAC><Password>Secret1</Password><IPv4Address>192.168.250.103</IPv4Address><CommandPort>8000</CommandPort><HttpPort>80</HttpPort><IPv4SubnetMask>255.255.255.0</IPv4SubnetMask><IPv4Gateway>192.168.250.1</IPv4Gateway><IPv6Address>::</IPv6Address><IPv6Gateway>::</IPv6Gateway><IPv6MaskLen>0</IPv6MaskLen><DHCP>false</DHCP><SDKOverTLSPort>8443</SDKOverTLSPort></Probe>""",
            xml);
    }

    [Fact]
    public void BuildUpdateXml_escapes_password_and_honours_ports_and_dhcp()
    {
        var update = new SadpNetworkUpdate("a4d5c24b74b1", "10.1.1.5", "255.255.0.0", "10.1.0.1", CommandPort: 8100, HttpPort: 8080, Dhcp: true);

        var xml = SadpProtocol.BuildUpdateXml("U", update, "a<b&c\"d");

        Assert.Contains("<Password>a&lt;b&amp;c&quot;d</Password>", xml);
        Assert.Contains("<CommandPort>8100</CommandPort>", xml);
        Assert.Contains("<HttpPort>8080</HttpPort>", xml);
        Assert.Contains("<DHCP>true</DHCP>", xml);
        Assert.Contains("<MAC>a4-d5-c2-4b-74-b1</MAC>", xml);
    }

    [Fact]
    public void BuildUpdateXml_rejects_invalid_mac()
    {
        var update = new SadpNetworkUpdate("not-a-mac", "10.1.1.5", "255.255.255.0", "10.1.1.1");

        Assert.Throws<ArgumentException>(() => SadpProtocol.BuildUpdateXml("U", update, "pw"));
    }

    [Fact]
    public void ParseUpdateReply_success()
    {
        const string xml = "<ProbeMatch><Uuid>abc-1</Uuid><Types>update</Types><MAC>a4-d5-c2-4b-74-b1</MAC><Result>success</Result></ProbeMatch>";

        var reply = SadpProtocol.ParseUpdateReply(xml, "ABC-1");

        Assert.NotNull(reply);
        Assert.True(reply.IsSuccess);
        Assert.Equal("success", reply.Result);
        Assert.Null(reply.ErrorCode);
        Assert.Equal("a4-d5-c2-4b-74-b1", reply.Mac);
    }

    [Fact]
    public void ParseUpdateReply_failed_with_error_code()
    {
        const string xml = "<ProbeMatch><Uuid>abc-1</Uuid><Types>update</Types><Result>failed</Result><ErrorCode>2022</ErrorCode></ProbeMatch>";

        var reply = SadpProtocol.ParseUpdateReply(xml, "abc-1");

        Assert.NotNull(reply);
        Assert.False(reply.IsSuccess);
        Assert.Equal("failed", reply.Result);
        Assert.Equal(2022, reply.ErrorCode);
    }

    [Fact]
    public void ParseUpdateReply_infers_result_from_error_code_when_result_missing()
    {
        Assert.True(SadpProtocol.ParseUpdateReply("<ProbeMatch><Uuid>u</Uuid><Types>update</Types></ProbeMatch>", "u")!.IsSuccess);
        Assert.False(SadpProtocol.ParseUpdateReply("<ProbeMatch><Uuid>u</Uuid><Types>update</Types><ErrorCode>7</ErrorCode></ProbeMatch>", "u")!.IsSuccess);
    }

    [Fact]
    public void ParseUpdateReply_ignores_uuid_mismatch_and_inquiry_replies()
    {
        Assert.Null(SadpProtocol.ParseUpdateReply(
            "<ProbeMatch><Uuid>other</Uuid><Types>update</Types><Result>success</Result></ProbeMatch>", "abc-1"));
        Assert.Null(SadpProtocol.ParseUpdateReply(ProbeMatchFixture, "13A888A9-F1B1-4020-AE9F-05607682D23B"));
        Assert.Null(SadpProtocol.ParseUpdateReply(SadpProtocol.BuildInquiryXml("abc-1"), "abc-1"));
    }

    [Theory]
    [InlineData("a4-d5-c2-4b-74-b1", "a4-d5-c2-4b-74-b1")]
    [InlineData("A4:D5:C2:4B:74:B1", "a4-d5-c2-4b-74-b1")]
    [InlineData("A4D5C24B74B1", "a4-d5-c2-4b-74-b1")]
    [InlineData(" a4.d5.c2.4b.74.b1 ", "a4-d5-c2-4b-74-b1")]
    [InlineData("a4-d5-c2-4b-74", null)]
    [InlineData("zz-zz-zz-zz-zz-zz", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeMac_produces_dash_separated_lowercase(string? input, string? expected)
    {
        Assert.Equal(expected, SadpProtocol.NormalizeMac(input));
    }
}
