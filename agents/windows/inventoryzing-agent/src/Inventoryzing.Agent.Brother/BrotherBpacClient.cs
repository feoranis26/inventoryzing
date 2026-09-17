using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Brother;

public enum BrotherCutMode
{
    DriverDefault,
    AutoCut,
    NoCut,
}

public sealed record BrotherMediaSelection
{
    private BrotherMediaSelection(string? name, int? id)
    {
        Name = name;
        Id = id;
    }

    public string? Name { get; }
    public int? Id { get; }

    public static BrotherMediaSelection ByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new BrotherMediaSelection(name, null);
    }

    public static BrotherMediaSelection ById(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        return new BrotherMediaSelection(null, id);
    }

    internal bool Matches(string? loadedName, int? loadedId) =>
        Name is not null
            ? string.Equals(Name, loadedName, StringComparison.OrdinalIgnoreCase)
            : Id == loadedId;

    public override string ToString() => Name is not null ? $"name '{Name}'" : $"ID {Id}";
}

public sealed record BrotherBpacPrinterStatus(
    bool IsSupported,
    bool IsOnline,
    bool IsMediaSupported,
    string? LoadedMediaName,
    int? LoadedMediaId,
    int ErrorCode,
    string? ErrorMessage);

public sealed record BrotherBpacPrinterAvailability(
    bool IsSupported,
    bool IsOnline,
    int ErrorCode,
    string? ErrorMessage);

public interface IBrotherBpacPrintEventClient
{
    IPrinterMonitorSubscription ArmPrintedEvents(
        TimeProvider? timeProvider = null,
        Func<Guid>? createId = null);
}

public sealed class BrotherBpacPrinterDiscovery
{
    private readonly IReadOnlyList<string> supportedMediaNames;
    private readonly IReadOnlyList<int> supportedMediaIds;

    public BrotherBpacPrinterDiscovery(
        string name,
        bool isOnline,
        bool mediaDetailsAvailable,
        IEnumerable<string> supportedMediaNames,
        IEnumerable<int> supportedMediaIds,
        string? loadedMediaName,
        int? loadedMediaId,
        int errorCode,
        string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(supportedMediaNames);
        ArgumentNullException.ThrowIfNull(supportedMediaIds);

        Name = name;
        IsOnline = isOnline;
        MediaDetailsAvailable = mediaDetailsAvailable;
        this.supportedMediaNames = Array.AsReadOnly(supportedMediaNames.ToArray());
        this.supportedMediaIds = Array.AsReadOnly(supportedMediaIds.ToArray());
        LoadedMediaName = loadedMediaName;
        LoadedMediaId = loadedMediaId;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public string Name { get; }
    public bool IsOnline { get; }
    public bool MediaDetailsAvailable { get; }
    public IReadOnlyList<string> SupportedMediaNames => supportedMediaNames;
    public IReadOnlyList<int> SupportedMediaIds => supportedMediaIds;
    public string? LoadedMediaName { get; }
    public int? LoadedMediaId { get; }
    public int ErrorCode { get; }
    public string? ErrorMessage { get; }
}

public interface IBrotherBpacClient : IDisposable
{
    int DocumentErrorCode { get; }
    int PrinterErrorCode { get; }
    string? PrinterErrorMessage { get; }

    IReadOnlyList<BrotherBpacPrinterDiscovery> DiscoverPrinters();

    BrotherBpacPrinterAvailability GetPrinterAvailability(string printerName);
    BrotherBpacPrinterStatus GetSelectedPrinterStatus(BrotherMediaSelection media);

    bool Open(string templatePath);
    bool SetPrinter(string printerName, bool fitPage);
    bool SetMedia(BrotherMediaSelection media, bool fitPage);
    bool SetObjectText(string objectName, string value);
    bool SetObjectImage(string objectName, string imagePath);
    bool StartPrint(string documentName, int options);
    bool PrintOut(int copies, int options);
    bool EndPrint();
    bool Close();
}