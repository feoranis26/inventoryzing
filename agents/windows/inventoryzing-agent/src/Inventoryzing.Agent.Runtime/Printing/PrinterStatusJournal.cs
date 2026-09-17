using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record PrinterStatusObservationRecord(
    long Sequence,
    PrinterStatusObservation Observation,
    DateTimeOffset ReceivedAt);

public interface IPrinterStatusJournal
{
    ValueTask<PrinterStatusObservationRecord> AppendPrinterStatusAsync(
        PrinterStatusObservation observation,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PrinterStatusObservationRecord>> ReadPrinterStatusesAsync(
        string deviceId,
        CancellationToken cancellationToken);
}

public sealed class DurablePrinterStatusObserver(
    IPrinterStatusJournal journal,
    TimeProvider? timeProvider = null)
{
    private readonly IPrinterStatusJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<PrinterStatusObservationRecord> ObserveAsync(
        IPrinterStatusProbe probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var observation = await probe.ObserveStatusAsync(cancellationToken).ConfigureAwait(false);
        return await journal.AppendPrinterStatusAsync(
            observation,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }
}