using System.Xml.Linq;

namespace HikVision.Services;

/// <summary>
/// Pure transforms for the device's ISAPI <c>/ISAPI/System/Network/interfaces/1/ipAddress</c> document.
/// The device requires every field it already has to be present on a PUT (a minimal body returns
/// "MessageParametersLack"), so these helpers edit the device's current document rather than build a fresh one.
/// </summary>
internal static class IsapiNetworkXml
{
    /// <summary>Switches the interface to DHCP, keeping every other field the device requires.</summary>
    public static string ToDynamic(string currentXml)
    {
        var doc = XDocument.Parse(currentXml);
        var root = doc.Root ?? throw new FormatException("ISAPI ipAddress document has no root element.");
        var ns = root.Name.Namespace;

        SetChild(root, ns, "addressingType", "dynamic");

        return Serialize(doc);
    }

    /// <summary>Sets a static address, keeping the device's other fields (DNS, etc.) intact.</summary>
    public static string ToStatic(string currentXml, string ipAddress, string subnetMask, string gateway, string? primaryDns = null, string? secondaryDns = null)
    {
        var doc = XDocument.Parse(currentXml);
        var root = doc.Root ?? throw new FormatException("ISAPI ipAddress document has no root element.");
        var ns = root.Name.Namespace;

        SetChild(root, ns, "addressingType", "static");
        SetChild(root, ns, "ipAddress", ipAddress);
        SetChild(root, ns, "subnetMask", subnetMask);
        SetNestedIp(root, ns, "DefaultGateway", gateway);

        if (!string.IsNullOrWhiteSpace(primaryDns))
        {
            SetNestedIp(root, ns, "PrimaryDNS", primaryDns);
        }

        if (!string.IsNullOrWhiteSpace(secondaryDns))
        {
            SetNestedIp(root, ns, "SecondaryDNS", secondaryDns);
        }

        return Serialize(doc);
    }

    private static void SetChild(XElement parent, XNamespace ns, string name, string value)
    {
        var element = parent.Element(ns + name) ?? parent.Element(name);
        if (element is null)
        {
            element = new XElement(ns + name);
            parent.Add(element);
        }

        element.Value = value;
    }

    private static void SetNestedIp(XElement root, XNamespace ns, string containerName, string value)
    {
        var container = root.Element(ns + containerName) ?? root.Element(containerName);
        if (container is null)
        {
            container = new XElement(ns + containerName);
            root.Add(container);
        }

        SetChild(container, ns, "ipAddress", value);
    }

    private static string Serialize(XDocument doc)
    {
        // The device expects the XML declaration; XDocument.ToString() omits it.
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }
}
