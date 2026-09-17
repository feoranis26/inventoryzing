using System.Xml.Linq;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class ZebraCoreScannerComClientTests
{
    [Fact]
    public void Uses_installed_sdk_signatures_and_forwards_callbacks()
    {
        var interop = new FakeCoreScannerInterop();
        using var client = new ZebraCoreScannerComClient(() => interop);
        var barcodes = new List<ZebraCoreScannerEventArgs>();
        var pnpChanges = new List<ZebraCoreScannerEventArgs>();
        client.BarcodeReceived += (_, eventArgs) => barcodes.Add(eventArgs);
        client.PnpChanged += (_, eventArgs) => pnpChanges.Add(eventArgs);

        client.Open([ZebraScannerProbe.SnapiScannerType]);
        var scannersXml = client.GetScanners();
        client.RegisterForEvents([ZebraScannerProbe.BarcodeEventId, ZebraScannerProbe.PnpEventId]);
        client.ExecuteAction(1, 43);
        interop.EmitBarcode(1, "<barcode />");
        interop.EmitPnp(0, "<pnp />");

        Assert.Equal(0, interop.OpenAppHandle);
        Assert.Equal([ZebraScannerProbe.SnapiScannerType], interop.OpenScannerTypes);
        Assert.Equal((short)1, interop.OpenScannerTypeCount);
        Assert.Equal(FakeCoreScannerInterop.ScannerXml, scannersXml);
        Assert.Equal(ZebraCoreScannerComClient.MaximumDevices, interop.ScannerIdBufferLength);
        Assert.Equal(ZebraCoreScannerComClient.SetActionOpcode, interop.LastOpcode);
        Assert.Equal("1", XDocument.Parse(interop.LastInputXml!).Descendants("scannerID").Single().Value);
        Assert.Equal("43", XDocument.Parse(interop.LastInputXml!).Descendants("arg-int").Single().Value);
        Assert.Equal("<barcode />", Assert.Single(barcodes).Xml);
        Assert.Equal("<pnp />", Assert.Single(pnpChanges).Xml);

        client.Close();
        interop.EmitBarcode(1, "<late />");
        Assert.Equal(0, interop.CloseAppHandle);
        Assert.Single(barcodes);
    }

    [Fact]
    public void Nonzero_sdk_status_is_reported_with_operation()
    {
        var interop = new FakeCoreScannerInterop { OpenStatus = 112 };
        using var client = new ZebraCoreScannerComClient(() => interop);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            client.Open([ZebraScannerProbe.SnapiScannerType]));

        Assert.Contains("Open", exception.Message, StringComparison.Ordinal);
        Assert.Contains("112", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Construct_and_dispose_do_not_resolve_or_activate_interop()
    {
        using var client = new ZebraCoreScannerComClient("missing-interop.dll");

        client.Dispose();
    }

    public delegate void CoreScannerEventHandler(short eventType, ref string xml);

    public sealed class FakeCoreScannerInterop
    {
        public const string ScannerXml = "<scanners />";

        public event CoreScannerEventHandler? BarcodeEvent;

        public event CoreScannerEventHandler? PNPEvent;

        public int OpenStatus { get; init; }

        public int OpenAppHandle { get; private set; } = -1;

        public short[] OpenScannerTypes { get; private set; } = [];

        public short OpenScannerTypeCount { get; private set; }

        public int ScannerIdBufferLength { get; private set; }

        public int LastOpcode { get; private set; }

        public string? LastInputXml { get; private set; }

        public int CloseAppHandle { get; private set; } = -1;

        public void Open(int appHandle, short[] scannerTypes, short lengthOfTypes, ref int status)
        {
            OpenAppHandle = appHandle;
            OpenScannerTypes = scannerTypes;
            OpenScannerTypeCount = lengthOfTypes;
            status = OpenStatus;
        }

        public void GetScanners(ref short numberOfScanners, int[] scannerIds, ref string outXml, ref int status)
        {
            ScannerIdBufferLength = scannerIds.Length;
            numberOfScanners = 0;
            outXml = ScannerXml;
            status = 0;
        }

        public void ExecCommand(int opcode, ref string inXml, ref string outXml, ref int status)
        {
            LastOpcode = opcode;
            LastInputXml = inXml;
            outXml = string.Empty;
            status = 0;
        }

        public void Close(int appHandle, ref int status)
        {
            CloseAppHandle = appHandle;
            status = 0;
        }

        public void EmitBarcode(short eventType, string xml) => BarcodeEvent?.Invoke(eventType, ref xml);

        public void EmitPnp(short eventType, string xml) => PNPEvent?.Invoke(eventType, ref xml);
    }
}
