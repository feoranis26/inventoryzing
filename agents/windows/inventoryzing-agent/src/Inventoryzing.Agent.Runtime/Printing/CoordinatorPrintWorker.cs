using Inventoryzing.Agent.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inventoryzing.Agent.Runtime.Printing;

public interface IMonitoredPrinterSessionFactory
{
    ValueTask<IMonitoredPrinterSession> CreateAsync(
        CoordinatorPrintClaim claim,
        CancellationToken cancellationToken);
}

public sealed class CoordinatorPrintWorker(
    CoordinatorPrintClient client,
    IMonitoredPrinterSessionFactory sessionFactory,
    MonitoredPrintExecutionCoordinator executionCoordinator,
    DurablePrinterStatusObserver statusObserver,
    PrintAttemptReporter reporter,
    PrintQueueIdentity queue,
    string printerId,
    TimeSpan idlePollInterval,
    TimeSpan correlationTimeout,
    ILogger<CoordinatorPrintWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CoordinatorPrintClaim? claim;
            try
            {
                claim = await client.ClaimAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to claim a print attempt; retrying.");
                await Task.Delay(idlePollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (claim is null)
            {
                await Task.Delay(idlePollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ProcessAsync(claim, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Print attempt {AttemptId} processing failed.",
                    claim.AttemptId);
                await TryReportAsync(claim.AttemptId, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(
        CoordinatorPrintClaim claim,
        CancellationToken cancellationToken)
    {
        var content = await client.DownloadArtifactAsync(claim, cancellationToken)
            .ConfigureAwait(false);
        var artifact = new PrintArtifact(
            claim.MediaType,
            claim.WidthMillimeters,
            claim.HeightMillimeters,
            ArtifactColorSpace.Srgb,
            content);
        if (!string.Equals(artifact.Sha256, claim.ArtifactSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Coordinator artifact failed SHA-256 validation.");
        }
        var profile = new PrintProfile(
            claim.ProfileId,
            claim.Palette,
            claim.DpiX,
            claim.DpiY,
            claim.WidthMillimeters,
            claim.HeightMillimeters);
        var request = new PrintProbeRequest(
            claim.AttemptId,
            artifact,
            profile,
            claim.Copies);
        await using var session = await sessionFactory.CreateAsync(claim, cancellationToken)
            .ConfigureAwait(false);
        _ = await statusObserver.ObserveAsync(session, cancellationToken).ConfigureAwait(false);
        var result = await executionCoordinator.ExecuteAsync(
            printerId,
            queue,
            session,
            request,
            claim.DocumentName,
            correlationTimeout,
            expectedOwnerName: null,
            expectedMachineName: null,
            cancellationToken).ConfigureAwait(false);
        _ = await reporter.ReportAsync(claim.AttemptId, cancellationToken).ConfigureAwait(false);
        if (result.Submission.Status == PrintSubmissionStatus.Rejected)
        {
            logger.LogWarning("Print attempt {AttemptId} was rejected: {Detail}",
                claim.AttemptId, result.Submission.Detail);
        }
        else if (result.Submission.Status == PrintSubmissionStatus.Unknown)
        {
            logger.LogWarning("Print attempt {AttemptId} has an unknown dispatch result: {Detail}",
                claim.AttemptId, result.Submission.Detail);
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Print attempt {AttemptId} reached {State} with {Evidence} evidence.",
                claim.AttemptId,
                result.Attempt.Status.State,
                result.Attempt.Status.CompletionEvidence);
        }
    }

    private async Task TryReportAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await reporter.ReportAsync(attemptId, cancellationToken).ConfigureAwait(false);
        }
        catch (PrintAttemptNotFoundException)
        {
            // The failure occurred before any local durable attempt existed. The
            // coordinator lease can safely expire and be claimed again.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to report print attempt {AttemptId}.", attemptId);
        }
    }
}
