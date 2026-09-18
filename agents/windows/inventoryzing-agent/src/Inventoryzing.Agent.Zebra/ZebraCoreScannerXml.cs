using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Zebra;

public sealed record ZebraBarcodeFrame(
    ScanSource Source,
    byte[] PayloadBytes,
    int DataType,
    string Symbology);

public static class ZebraCoreScannerXml
{
    private const int MaximumXmlCharacters = 1_048_576;

    private static readonly Dictionary<int, string> Symbologies = new()
    {
        [1] = "CODE_39",
        [2] = "CODABAR",
        [3] = "CODE_128",
        [4] = "D2OF5",
        [5] = "IATA",
        [6] = "I2OF5",
        [7] = "CODE_93",
        [8] = "UPCA",
        [9] = "UPCE0",
        [10] = "EAN8",
        [11] = "EAN13",
        [12] = "CODE_11",
        [13] = "CODE_49",
        [14] = "MSI",
        [15] = "EAN_128",
        [16] = "UPCE1",
        [17] = "PDF417",
        [18] = "CODE_16K",
        [19] = "CODE_39_FULL_ASCII",
        [20] = "UPCD",
        [21] = "TRIOPTIC",
        [22] = "BOOKLAND",
        [23] = "COUPON",
        [24] = "NW7",
        [25] = "ISBT_128",
        [26] = "MICRO_PDF",
        [27] = "DATAMATRIX",
        [28] = "QR_CODE",
        [29] = "MICRO_PDF_CCA",
        [30] = "POSTNET_US",
        [31] = "PLANET_CODE",
        [32] = "CODE_32",
        [33] = "ISBT_128_CON",
        [34] = "JAPAN_POSTAL",
        [35] = "AUS_POSTAL",
        [36] = "DUTCH_POSTAL",
        [37] = "MAXICODE",
        [38] = "CANADIAN_POSTAL",
        [39] = "UK_POSTAL",
        [40] = "MACRO_PDF",
        [44] = "MICRO_QR_CODE",
        [45] = "AZTEC",
        [48] = "GS1_DATABAR",
        [49] = "GS1_DATABAR_LIMITED",
        [50] = "GS1_DATABAR_EXPANDED",
        [55] = "SCANLET",
        [57] = "C25",
        [72] = "UPCA_2",
        [73] = "UPCE0_2",
        [74] = "EAN8_2",
        [75] = "EAN13_2",
        [80] = "UPCE1_2",
        [81] = "CCA_EAN128",
        [82] = "CCA_EAN13",
        [83] = "CCA_EAN8",
        [84] = "CCA_RSS_EXPANDED",
        [85] = "CCA_RSS_LIMITED",
        [86] = "CCA_RSS14",
        [87] = "CCA_UPCA",
        [88] = "CCA_UPCE",
        [89] = "CCC_EAN128",
        [90] = "TLC39",
        [97] = "CCB_EAN128",
        [98] = "CCB_EAN13",
        [99] = "CCB_EAN8",
        [100] = "CCB_RSS_EXPANDED",
        [101] = "CCB_RSS_LIMITED",
        [102] = "CCB_RSS14",
        [103] = "CCB_UPCA",
        [104] = "CCB_UPCE",
        [105] = "SIGNATURE_CAPTURE",
        [113] = "MATRIX2OF5",
        [114] = "CHINESE2OF5",
        [136] = "UPCA_5",
        [137] = "UPCE0_5",
        [138] = "EAN8_5",
        [139] = "EAN13_5",
        [144] = "UPCE1_5",
        [154] = "MACRO_MICRO_PDF",
    };

    public static IReadOnlyList<ScanSource> ParseScanners(string xml) =>
        [.. ParseDocument(xml)
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "scanner" &&
                string.Equals((string?)element.Attribute("type"), "SNAPI", StringComparison.OrdinalIgnoreCase))
            .Select(ParseSource)];

    public static ZebraBarcodeFrame ParseBarcode(string xml)
    {
        var document = ParseDocument(xml);
        var root = document.Root ?? throw new FormatException("CoreScanner XML has no root element.");
        var dataTypeText = RequiredValue(root, "datatype");
        if (!int.TryParse(dataTypeText, NumberStyles.None, CultureInfo.InvariantCulture, out var dataType))
        {
            throw new FormatException($"CoreScanner barcode datatype '{dataTypeText}' is not an integer.");
        }

        var payloadBytes = ParseHexBytes(RequiredValue(root, "datalabel"));
        var symbology = Symbologies.TryGetValue(dataType, out var knownName)
            ? knownName
            : $"ZEBRA_{dataType.ToString(CultureInfo.InvariantCulture)}";

        return new ZebraBarcodeFrame(ParseSource(root), payloadBytes, dataType, symbology);
    }

    public static ScanSource? FindHandheld(string xml, ScanSource cradle)
    {
        var parent = ParseDocument(xml).Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "scanner" &&
            element.Elements().Any(child => child.Name.LocalName == "scannerID" &&
                child.Value.Trim() == cradle.RuntimeId) &&
            element.Elements().Any(child => child.Name.LocalName == "serialnumber" &&
                child.Value.Trim() == cradle.SerialNumber));
        if (parent is null) return null;
        var children = parent.Elements().Where(element => element.Name.LocalName == "scanner")
            .Select(ParseSource).Where(source =>
                source.Model?.StartsWith("DS", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        // A cradle event cannot identify which handheld decoded when multiple are paired.
        return children.Length == 1 ? children[0] : null;
    }

    public static string DecodePayload(ReadOnlySpan<byte> payloadBytes) =>
        Encoding.Latin1.GetString(payloadBytes);

    private static ScanSource ParseSource(XContainer scope)
    {
        var runtimeId = RequiredValue(scope, "scannerID");
        var serialNumber = OptionalValue(scope, "serialnumber");
        var scannerGuid = OptionalValue(scope, "GUID");
        var model = OptionalValue(scope, "modelnumber");

        var stableId = serialNumber is not null
            ? $"zebra:serial:{serialNumber}"
            : scannerGuid is not null
                ? $"zebra:guid:{scannerGuid}"
                : throw new FormatException("CoreScanner scanner XML has neither a serial number nor a GUID.");

        return new ScanSource(stableId, runtimeId, ScanSourceType.ZebraSnapi, model, serialNumber);
    }

    private static byte[] ParseHexBytes(string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new FormatException("CoreScanner barcode data label is empty.");
        }

        var bytes = new byte[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? tokens[index][2..]
                : tokens[index];
            if (!byte.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[index]))
            {
                throw new FormatException($"CoreScanner barcode byte '{tokens[index]}' is not hexadecimal.");
            }
        }

        return bytes;
    }

    private static XDocument ParseDocument(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        using var textReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(textReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaximumXmlCharacters,
            XmlResolver = null,
        });
        return XDocument.Load(xmlReader, LoadOptions.None);
    }

    private static string RequiredValue(XContainer? scope, string localName) =>
        OptionalValue(scope, localName) ??
        throw new FormatException($"CoreScanner XML is missing '{localName}'.");

    private static string? OptionalValue(XContainer? scope, string localName)
    {
        var value = scope?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value
            .Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
