using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inventoryzing.Agent.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventoryzing.Agent.Scanner.Host;

public enum ScannerOperationOutcome
{
    Success,
    Failure,
    NoAction,
}

public sealed record ScanDeliveryResult(
    ScannerOperationOutcome Outcome,
    string Message,
    bool CommandCompleted = false);

public sealed class ScanDeliveryClient(
    HttpClient httpClient,
    string bearerToken,
    Guid terminalId,
    TimeSpan requestTimeout,
    ILogger<ScanDeliveryClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async ValueTask<ScanDeliveryResult> DeliverAsync(
        ScanEvent scan,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/agent/scans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = JsonContent.Create(new
        {
            scan.EventId,
            TerminalId = terminalId,
            scan.Payload,
            SourceId = scan.Source.StableId,
            RuntimeId = scan.Source.RuntimeId,
            SourceType = scan.Source.Type.ToString(),
            scan.Source.Model,
            scan.Source.SerialNumber,
            scan.ReceivedAt,
            scan.Symbology,
        }, options: JsonOptions);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        var started = Stopwatch.GetTimestamp();
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ScanDeliveryResult>(
            JsonOptions, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || result is null)
        {
            throw new HttpRequestException(
                result?.Message ?? $"Coordinator returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        logger.LogInformation(
            "Scan {EventId}: {Outcome} in {ElapsedMs:0.0} ms ({Message})",
            scan.EventId,
            result.Outcome,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            result.Message);
        return result;
    }
}

public sealed class LiveScannerWorker(
    IScannerProbe scanner,
    ScanDeliveryClient client,
    IOptions<ScannerAgentOptions> options,
    ILogger<LiveScannerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var scan in scanner.WatchAsync(stoppingToken).ConfigureAwait(false))
        {
            var outcome = ScannerOperationOutcome.Failure;
            var commandCompleted = false;
            try
            {
                var result = await client.DeliverAsync(scan, stoppingToken).ConfigureAwait(false);
                outcome = result.Outcome;
                commandCompleted = result.CommandCompleted;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Scan {EventId} was not delivered. Scan the item again.", scan.EventId);
            }

            try
            {
                logger.LogInformation("Applying {Outcome} feedback for scan source {SourceId} ({Model}, runtime {RuntimeId}); cradle LEDs resolve to the paired handheld.",
                    outcome, scan.Source.StableId, scan.Source.Model ?? "unknown model", scan.Source.RuntimeId);
                await scanner.ExecuteFeedbackAsync(
                    scan.Source, options.Value.FeedbackFor(outcome, commandCompleted), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Unable to execute feedback for scan {EventId}.", scan.EventId);
            }
        }
    }
}
