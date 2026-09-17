using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record PrinterRuntimeMetricsSnapshot(
    long DispatchResults,
    long Submitted,
    long Rejected,
    long Unknown,
    long RecoveryReconnects);

public sealed class PrinterRuntimeMetrics
{
    private long dispatchResults;
    private long submitted;
    private long rejected;
    private long unknown;
    private long recoveryReconnects;

    public PrinterRuntimeMetricsSnapshot Current => new(
        Volatile.Read(ref dispatchResults),
        Volatile.Read(ref submitted),
        Volatile.Read(ref rejected),
        Volatile.Read(ref unknown),
        Volatile.Read(ref recoveryReconnects));

    internal void RecordDispatchResult(PrintSubmissionStatus status)
    {
        Interlocked.Increment(ref dispatchResults);
        _ = status switch
        {
            PrintSubmissionStatus.Submitted => Interlocked.Increment(ref submitted),
            PrintSubmissionStatus.Rejected => Interlocked.Increment(ref rejected),
            PrintSubmissionStatus.Unknown => Interlocked.Increment(ref unknown),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
    }

    internal void RecordRecoveryReconnect() => Interlocked.Increment(ref recoveryReconnects);
}
