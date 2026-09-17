namespace Inventoryzing.Agent.Core;

public enum PrinterMonitorCompletionCapability
{
    Unknown,
    Unsupported,
    PageCompletion,
}

public enum PrinterMonitorEventKind
{
    PagePrinted,
    Offline,
    Paused,
    Deleted,
    Error,
    PrinterNotFound,
    Unknown,
}

public sealed record PrinterMonitorObservation(
    Guid ObservationId,
    PrinterMonitorEventKind Kind,
    int RawStatus,
    string? Value,
    DateTimeOffset ObservedAt);

public interface IPrinterMonitorSubscription : IDisposable
{
    ValueTask<PrinterMonitorObservation> WaitAsync(CancellationToken cancellationToken);
}

public enum PrinterProfileReadiness
{
    Ready,
    PrinterUnsupported,
    Offline,
    PrinterError,
    MediaUnsupported,
    MediaMismatch,
    ProbeError,
}

public sealed record PrinterProfileStatus(
    string ProfileId,
    PrinterProfileReadiness Readiness,
    string ExpectedMedia,
    string? LoadedMediaName,
    int? LoadedMediaId,
    int? RawProviderStatus,
    string? Detail);

public sealed class PrinterStatusObservation
{
    private readonly IReadOnlyList<PrinterProfileStatus> profiles;

    public PrinterStatusObservation(
        Guid observationId,
        string deviceId,
        string printerName,
        bool isSupported,
        bool isOnline,
        string? loadedMediaName,
        int? loadedMediaId,
        int? rawProviderStatus,
        string? errorDetail,
        PrinterMonitorCompletionCapability completionCapability,
        IEnumerable<PrinterProfileStatus> profiles,
        DateTimeOffset observedAt)
    {
        if (observationId == Guid.Empty)
        {
            throw new ArgumentException("Observation ID cannot be empty.", nameof(observationId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (!Enum.IsDefined(completionCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(completionCapability));
        }
        ArgumentNullException.ThrowIfNull(profiles);
        var snapshot = profiles.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("At least one profile status is required.", nameof(profiles));
        }

        ObservationId = observationId;
        DeviceId = deviceId;
        PrinterName = printerName;
        IsSupported = isSupported;
        IsOnline = isOnline;
        LoadedMediaName = loadedMediaName;
        LoadedMediaId = loadedMediaId;
        RawProviderStatus = rawProviderStatus;
        ErrorDetail = errorDetail;
        CompletionCapability = completionCapability;
        this.profiles = Array.AsReadOnly(snapshot);
        ObservedAt = observedAt.ToUniversalTime();
    }

    public Guid ObservationId { get; }
    public string DeviceId { get; }
    public string PrinterName { get; }
    public bool IsSupported { get; }
    public bool IsOnline { get; }
    public string? LoadedMediaName { get; }
    public int? LoadedMediaId { get; }
    public int? RawProviderStatus { get; }
    public string? ErrorDetail { get; }
    public PrinterMonitorCompletionCapability CompletionCapability { get; }
    public IReadOnlyList<PrinterProfileStatus> Profiles => profiles;
    public DateTimeOffset ObservedAt { get; }
}

public interface IPrinterStatusProbe
{
    ValueTask<PrinterStatusObservation> ObserveStatusAsync(CancellationToken cancellationToken);
}

public interface IMonitoredPrinterSession :
    IPrinterProbe,
    IPrinterStatusProbe,
    IAsyncDisposable
{
    // A session, including its callback subscription, belongs to exactly one attempt.
    Guid AttemptId { get; }

    PrinterMonitorCompletionCapability CompletionCapability { get; }

    ValueTask<PrinterMonitorObservation> WaitForMonitorEventAsync(
        CancellationToken cancellationToken);
}
