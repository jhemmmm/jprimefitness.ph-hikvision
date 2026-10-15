using System.Globalization;
using System.Xml.Linq;

namespace HikVision.Services;

/// <summary>
/// Pure transforms for the device's ISAPI <c>/ISAPI/System/time</c> document. Like the network document,
/// the device's own document is edited and sent back so fields such as <c>timeZone</c> survive the PUT.
/// </summary>
internal static class IsapiTimeXml
{
    /// <summary>The device's current clock, or null when the document has no parseable <c>localTime</c>.</summary>
    public static DateTimeOffset? ReadLocalTime(string xml)
    {
        var value = LocalTimeElement(XDocument.Parse(xml).Root)?.Value.Trim();

        // ponytail: a localTime without an offset is read in the PC's zone; fine while the PC and terminal share Asia/Manila.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Sets the clock manually to <paramref name="now"/>, written in the device's current UTC offset.</summary>
    public static string ToManual(string currentXml, DateTimeOffset now)
    {
        var doc = XDocument.Parse(currentXml);
        var root = doc.Root ?? throw new FormatException("ISAPI time document has no root element.");
        var ns = root.Name.Namespace;
        var offset = ReadLocalTime(currentXml)?.Offset ?? now.Offset;

        SetChild(root, ns, "timeMode", "manual");
        SetChild(root, ns, "localTime", now.ToOffset(offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement? LocalTimeElement(XElement? root) =>
        root?.Element(root.Name.Namespace + "localTime") ?? root?.Element("localTime");

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
}
