using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed class CoordinatorReportWorker(
    IPrintAttemptJournal journal,
    PrintAttemptReporter reporter,
    TimeSpan pollInterval,
    ILogger<CoordinatorReportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var attempts = await journal.ReadPendingReportAttemptIdsAsync(stoppingToken)
                .ConfigureAwait(false);
            foreach (var attemptId in attempts)
            {
                try
                {
                    _ = await reporter.ReportAsync(attemptId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Unable to report durable print attempt {AttemptId}; retrying.", attemptId);
                }
            }
            await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
