using System.Xml.Linq;
using HikVision.Services;

namespace HikVision.Tests.Services;

public class IsapiTimeXmlTests
{
    // Shape of GET /ISAPI/System/time on Hikvision access-control terminals.
    private const string CurrentXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <Time version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
        <timeMode>NTP</timeMode>
        <localTime>2026-10-15T08:02:10+08:00</localTime>
        <timeZone>CST-8:00:00</timeZone>
        </Time>
        """;

    private static readonly XNamespace Ns = "http://www.isapi.org/ver20/XMLSchema";

    [Fact]
    public void ReadLocalTime_parses_the_device_clock_with_its_offset()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 10, 15, 8, 2, 10, TimeSpan.FromHours(8)),
            IsapiTimeXml.ReadLocalTime(CurrentXml));
    }

    [Fact]
    public void ReadLocalTime_returns_null_without_a_local_time()
    {
        Assert.Null(IsapiTimeXml.ReadLocalTime("<Time><timeMode>manual</timeMode></Time>"));
    }

    [Fact]
    public void ToManual_sets_the_pc_time_in_the_device_offset_and_keeps_the_time_zone()
    {
        var pcTimeUtc = new DateTimeOffset(2026, 10, 15, 0, 0, 5, TimeSpan.Zero);

        var root = XDocument.Parse(IsapiTimeXml.ToManual(CurrentXml, pcTimeUtc)).Root!;

        Assert.Equal("manual", root.Element(Ns + "timeMode")!.Value);
        Assert.Equal("2026-10-15T08:00:05+08:00", root.Element(Ns + "localTime")!.Value);
        Assert.Equal("CST-8:00:00", root.Element(Ns + "timeZone")!.Value);
    }
}
