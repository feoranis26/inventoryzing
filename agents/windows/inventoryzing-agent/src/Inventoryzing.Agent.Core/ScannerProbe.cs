namespace Inventoryzing.Agent.Core;

public interface IScannerProbe : IAsyncDisposable
{
    ValueTask<IReadOnlyList<ScanSource>> EnumerateAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ScanEvent> WatchAsync(CancellationToken cancellationToken);

    ValueTask ExecuteFeedbackAsync(
        ScanSource source,
        ScannerFeedback feedback,
        CancellationToken cancellationToken);
}
