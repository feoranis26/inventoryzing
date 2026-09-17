namespace Inventoryzing.Agent.Zebra;

public sealed class ZebraCoreScannerEventArgs(short eventType, string xml) : EventArgs
{
    public short EventType { get; } = eventType;

    public string Xml { get; } = xml ?? throw new ArgumentNullException(nameof(xml));
}

public interface IZebraCoreScannerClient : IDisposable
{
    event EventHandler<ZebraCoreScannerEventArgs>? BarcodeReceived;

    event EventHandler<ZebraCoreScannerEventArgs>? PnpChanged;

    void Open(IReadOnlyCollection<short> scannerTypes);

    string GetScanners();

    string GetDeviceTopology();

    void RegisterForEvents(IReadOnlyCollection<short> eventIds);

    void ExecuteAction(int scannerId, int action);

    void Close();
}
