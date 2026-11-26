using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ziwa.Rendering.Svg;

public static partial class SvgSanitizer
{
    private const int MaxXmlChars = 5 * 1024 * 1024;

    private static readonly HashSet<string> BlockedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script",
        "foreignObject",
        "iframe",
        "object",
        "embed",
        "audio",
        "video",
        "canvas",
        "applet",
        "meta",
        "link",
    };

    public static string Sanitize(string rawSvgXml)
    {
        if (string.IsNullOrWhiteSpace(rawSvgXml))
        {
            throw new InvalidDataException("SVG content cannot be empty.");
        }

        if (rawSvgXml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SVG documents declaring XML entities are rejected.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlChars,
        };

        using var stringReader = new StringReader(rawSvgXml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        XDocument doc = XDocument.Load(xmlReader, LoadOptions.None);

        if (doc.Root is null || !doc.Root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Root XML element must be <svg>.");
        }

        foreach (XProcessingInstruction instruction in doc.DescendantNodes().OfType<XProcessingInstruction>().ToArray())
        {
            instruction.Remove();
        }

        List<XElement> elementsToRemove = doc.Descendants()
            .Where(el => BlockedElements.Contains(el.Name.LocalName) ||
                (el.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase) && HasUnsafeCss(el.Value)))
            .ToList();
        foreach (XElement blocked in elementsToRemove)
        {
            blocked.Remove();
        }

        foreach (XElement element in doc.Descendants().Prepend(doc.Root))
        {
            List<XAttribute> attrsToRemove = element.Attributes()
                .Where(IsDangerousAttribute)
                .ToList();
            foreach (XAttribute attr in attrsToRemove)
            {
                attr.Remove();
            }
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static bool IsDangerousAttribute(XAttribute attr)
    {
        string localName = attr.Name.LocalName;
        string val = attr.Value.Trim();

        if (HasUnsafeCss(val)) { return true; }
        if (localName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (localName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
            localName.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            bool isInternalRef = val.StartsWith('#');
            bool isSafeRasterDataUri =
                val.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
                val.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase);

            return !isInternalRef && !isSafeRasterDataUri;
        }

        return false;
    }

    private static bool HasUnsafeCss(string value)
    {
        if (value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("expression(", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("@import", StringComparison.OrdinalIgnoreCase) || value.Contains('\\'))
        {
            return true;
        }

        foreach (Match match in UrlReferenceRegex().Matches(value))
        {
            string target = match.Groups["target"].Value;
            if (!target.StartsWith('#') &&
                !target.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("url\\s*\\(\\s*['\"]?(?<target>[^'\"\\s)]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlReferenceRegex();
}
