using Inventoryzing.Agent.Core;
using System.Xml;

namespace Inventoryzing.Agent.Tests;

public sealed class ZebraCoreScannerXmlTests
{
    [Fact]
    public void Parses_only_snapi_scanners_with_stable_serial_identity()
    {
        const string xml = """
            <scanners>
              <scanner type="SNAPI">
                <scannerID>7</scannerID>
                <modelnumber>DS2208-SR00007ZZWW</modelnumber>
                <serialnumber> ABC123 </serialnumber>
                <GUID>AABBCCDD</GUID>
              </scanner>
              <scanner type="USBHIDKB">
                <scannerID>8</scannerID>
                <modelnumber>DS2208-SR00007ZZWW</modelnumber>
                <serialnumber>KEYBOARD1</serialnumber>
                <GUID>EEFF0011</GUID>
              </scanner>
            </scanners>
            """;

        var source = Assert.Single(ZebraCoreScannerXml.ParseScanners(xml));

        Assert.Equal("zebra:serial:ABC123", source.StableId);
        Assert.Equal("7", source.RuntimeId);
        Assert.Equal("DS2208-SR00007ZZWW", source.Model);
        Assert.Equal("ABC123", source.SerialNumber);
        Assert.Equal(ScanSourceType.ZebraSnapi, source.Type);
    }

    [Fact]
    public void Uses_guid_when_scanner_serial_is_blank()
    {
        const string xml = """
            <scanners>
              <scanner type="SNAPI">
                <scannerID>12</scannerID>
                <modelnumber>DS2208</modelnumber>
                <serialnumber>   </serialnumber>
                <GUID>AABBCCDD</GUID>
              </scanner>
            </scanners>
            """;

        var source = Assert.Single(ZebraCoreScannerXml.ParseScanners(xml));

        Assert.Equal("zebra:guid:AABBCCDD", source.StableId);
        Assert.Null(source.SerialNumber);
    }

    [Fact]
    public void Parses_barcode_bytes_symbology_and_source()
    {
        const string xml = """
            <outArgs>
              <scannerID>7</scannerID>
              <arg-xml>
                <scandata>
                  <modelnumber>DS2208</modelnumber>
                  <serialnumber>ABC123</serialnumber>
                  <GUID>AABBCCDD</GUID>
                  <datatype>28</datatype>
                  <datalabel>0x49 30 0x30 30 30 34 32</datalabel>
                  <rawdata>0x49 0x30 0x30 0x30 0x30 0x34 0x32</rawdata>
                </scandata>
              </arg-xml>
            </outArgs>
            """;

        var frame = ZebraCoreScannerXml.ParseBarcode(xml);

        Assert.Equal("I000042", ZebraCoreScannerXml.DecodePayload(frame.PayloadBytes));
        Assert.Equal([0x49, 0x30, 0x30, 0x30, 0x30, 0x34, 0x32], frame.PayloadBytes);
        Assert.Equal(28, frame.DataType);
        Assert.Equal("QR_CODE", frame.Symbology);
        Assert.Equal("zebra:serial:ABC123", frame.Source.StableId);
        Assert.Equal("7", frame.Source.RuntimeId);
    }

    [Fact]
    public void Rejects_external_entities()
    {
        const string xml = """
            <!DOCTYPE scanners [<!ENTITY probe SYSTEM "file:///C:/Windows/win.ini">]>
            <scanners>
              <scanner type="SNAPI">
                <scannerID>7</scannerID>
                <serialnumber>&probe;</serialnumber>
              </scanner>
            </scanners>
            """;

        Assert.Throws<XmlException>(() => ZebraCoreScannerXml.ParseScanners(xml));
    }

    [Theory]
    [InlineData("<outArgs><scannerID>7</scannerID><datatype>28</datatype></outArgs>")]
    [InlineData("<outArgs><scannerID>7</scannerID><serialnumber>ABC123</serialnumber><datatype>28</datatype><datalabel>0xGG</datalabel></outArgs>")]
    [InlineData("<outArgs><scannerID>7</scannerID><datatype>QR</datatype><datalabel>0x31</datalabel></outArgs>")]
    public void Rejects_malformed_barcode_xml(string xml)
    {
        Assert.Throws<FormatException>(() => ZebraCoreScannerXml.ParseBarcode(xml));
    }
}