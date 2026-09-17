using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

internal interface IPrintSpoolerApi
{
    IPrintSpoolerReadSession OpenRead(PrintQueueIdentity queue);

    PrintQueueResumeResult Resume(PrintQueueJobReference reference);
}

internal interface IPrintSpoolerReadSession : IDisposable
{
    IPrintSpoolerChangeSubscription Subscribe();

    PrintQueueSnapshot Snapshot(DateTimeOffset observedAt);

    PrintQueueJobSnapshot? ReadJob(uint jobId);
}

internal interface IPrintSpoolerChangeSubscription : IDisposable
{
    ValueTask<PrintQueueChangeNotification> WaitAsync(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);
}

[Flags]
internal enum WindowsPrinterAccess : uint
{
    Administer = 0x00000004,
    Use = 0x00000008,
}

internal enum WindowsSpoolerWaitOutcome
{
    Signaled,
    TimedOut,
}

internal enum WindowsSpoolerResumeOutcome
{
    Succeeded,
    JobMissing,
}

internal sealed record WindowsSpoolerJob(
    uint JobId,
    string DocumentName,
    DateTimeOffset SubmittedAt,
    uint RawStatus,
    string? StatusText,
    string? OwnerName,
    string? MachineName,
    uint TotalPages,
    uint PagesPrinted);

internal sealed record WindowsSpoolerQueueSnapshot(
    uint RawPrinterStatus,
    IReadOnlyList<WindowsSpoolerJob> Jobs);

internal sealed record WindowsSpoolerNotification(uint RawChanges, bool Discarded);

internal interface IWindowsPrinterHandle : IDisposable;

internal interface IWindowsPrinterChangeHandle : IDisposable;

internal interface IWindowsPrintSpoolerNative
{
    IWindowsPrinterHandle OpenPrinter(string printerName, WindowsPrinterAccess desiredAccess);

    IWindowsPrinterChangeHandle Subscribe(IWindowsPrinterHandle printerHandle);

    WindowsSpoolerQueueSnapshot Snapshot(IWindowsPrinterHandle printerHandle);

    WindowsSpoolerJob? GetJob(IWindowsPrinterHandle printerHandle, uint jobId);

    WindowsSpoolerWaitOutcome WaitForChange(
        IWindowsPrinterChangeHandle changeHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    WindowsSpoolerNotification NextNotification(
        IWindowsPrinterChangeHandle changeHandle,
        bool refresh);

    WindowsSpoolerResumeOutcome Resume(IWindowsPrinterHandle printerHandle, uint jobId);
}