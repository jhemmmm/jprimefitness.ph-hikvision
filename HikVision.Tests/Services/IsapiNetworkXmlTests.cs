using System.Xml.Linq;
using HikVision.Services;

namespace HikVision.Tests.Services;

public class IsapiNetworkXmlTests
{
    // The exact document the DS-K1A802AEF returns from GET /ISAPI/System/Network/interfaces/1/ipAddress.
    private const string CurrentXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <IPAddress version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
        <ipVersion>v4</ipVersion>
        <addressingType>static</addressingType>
        <ipAddress>192.168.250.114</ipAddress>
        <subnetMask>255.255.255.0</subnetMask>
        <DefaultGateway>
        <ipAddress>192.168.250.1</ipAddress>
        </DefaultGateway>
        <PrimaryDNS>
        <ipAddress>8.8.8.8</ipAddress>
        </PrimaryDNS>
        <SecondaryDNS>
        <ipAddress>8.8.4.4</ipAddress>
        </SecondaryDNS>
        </IPAddress>
        """;

    private static readonly XNamespace Ns = "http://www.isapi.org/ver20/XMLSchema";

    private static XElement Root(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void ToDynamic_only_switches_addressing_type_and_keeps_every_field()
    {
        var root = Root(IsapiNetworkXml.ToDynamic(CurrentXml));

        Assert.Equal("dynamic", root.Element(Ns + "addressingType")!.Value);
        // All other fields preserved (device rejects a partial body with MessageParametersLack).
        Assert.Equal("192.168.250.114", root.Element(Ns + "ipAddress")!.Value);
        Assert.Equal("255.255.255.0", root.Element(Ns + "subnetMask")!.Value);
        Assert.Equal("192.168.250.1", root.Element(Ns + "DefaultGateway")!.Element(Ns + "ipAddress")!.Value);
        Assert.Equal("8.8.8.8", root.Element(Ns + "PrimaryDNS")!.Element(Ns + "ipAddress")!.Value);
    }

    [Fact]
    public void ToStatic_sets_address_mask_and_gateway()
    {
        var root = Root(IsapiNetworkXml.ToStatic(CurrentXml, "192.168.1.50", "255.255.255.0", "192.168.1.1"));

        Assert.Equal("static", root.Element(Ns + "addressingType")!.Value);
        Assert.Equal("192.168.1.50", root.Element(Ns + "ipAddress")!.Value);
        Assert.Equal("255.255.255.0", root.Element(Ns + "subnetMask")!.Value);
        Assert.Equal("192.168.1.1", root.Element(Ns + "DefaultGateway")!.Element(Ns + "ipAddress")!.Value);
        // Untouched DNS is preserved.
        Assert.Equal("8.8.8.8", root.Element(Ns + "PrimaryDNS")!.Element(Ns + "ipAddress")!.Value);
    }

    [Fact]
    public void ToStatic_updates_dns_when_supplied()
    {
        var root = Root(IsapiNetworkXml.ToStatic(CurrentXml, "10.0.0.5", "255.0.0.0", "10.0.0.1", "1.1.1.1", "1.0.0.1"));

        Assert.Equal("1.1.1.1", root.Element(Ns + "PrimaryDNS")!.Element(Ns + "ipAddress")!.Value);
        Assert.Equal("1.0.0.1", root.Element(Ns + "SecondaryDNS")!.Element(Ns + "ipAddress")!.Value);
    }

    [Fact]
    public void Output_keeps_the_isapi_namespace_and_declaration()
    {
        var xml = IsapiNetworkXml.ToDynamic(CurrentXml);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains("xmlns=\"http://www.isapi.org/ver20/XMLSchema\"", xml);
    }

    [Fact]
    public void Invalid_xml_throws()
    {
        Assert.ThrowsAny<System.Exception>(() => IsapiNetworkXml.ToDynamic("not xml"));
    }
}
