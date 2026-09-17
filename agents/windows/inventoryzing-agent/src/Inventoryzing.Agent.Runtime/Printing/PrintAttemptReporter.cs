namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record PrintAttemptReport(
    PrintAttemptRecord Attempt,
    PrintDispatchIntentRecord? Intent,
    PrintDispatchResultRecord? DispatchResult,
    PrintQueueCorrelationRecord? QueueCorrelation,
    IReadOnlyList<PrintObservationRecord> Observations);

public interface IPrintAttemptReportSink
{
    // Implementations must treat attempt version and observation IDs as
    // idempotency keys. A lost response causes the same snapshot to be resent.
    ValueTask ReportAsync(PrintAttemptReport report, CancellationToken cancellationToken);
}

public sealed class PrintAttemptReporter(
    IPrintAttemptJournal journal,
    IPrintAttemptReportSink sink)
{
    private readonly IPrintAttemptJournal journal =
        journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IPrintAttemptReportSink sink =
        sink ?? throw new ArgumentNullException(nameof(sink));

    public async ValueTask<PrintAttemptReport> ReportAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt ID cannot be empty.", nameof(attemptId));
        }

        var attempt = await journal.FindAsync(attemptId, cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");
        var report = new PrintAttemptReport(
            attempt,
            await journal.FindDispatchIntentAsync(attemptId, cancellationToken).ConfigureAwait(false),
            await journal.FindDispatchResultAsync(attemptId, cancellationToken).ConfigureAwait(false),
            await journal.FindQueueCorrelationAsync(attemptId, cancellationToken).ConfigureAwait(false),
            await journal.ReadObservationsAsync(attemptId, cancellationToken).ConfigureAwait(false));
        await sink.ReportAsync(report, cancellationToken).ConfigureAwait(false);
        await journal.MarkReportAcknowledgedAsync(
            attemptId, attempt.Version, cancellationToken).ConfigureAwait(false);
        return report;
    }
}
