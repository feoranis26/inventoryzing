using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inventoryzing.Agent.Brother;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inventoryzing.Agent.Printer.Host;

/// <summary>
/// The coordinator keeps this claim only in memory. Its raster is never written to disk.
/// </summary>
public sealed record PrintRequestClaim(
    Guid Id,
    string ProfileId,
    string MediaType,
    [property: JsonPropertyName("width_mm")]
    decimal WidthMillimeters,
    [property: JsonPropertyName("height_mm")]
    decimal HeightMillimeters,
    [property: JsonPropertyName("media_kind")]
    string MediaKind,
    [property: JsonPropertyName("feed_margin_dots")]
    int FeedMarginDots,
    int RasterLineBytes,
    int RasterLines,
    string RasterBase64)
{
    public byte[] GetRaster()
    {
        try
        {
            return Convert.FromBase64String(RasterBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Coordinator returned malformed raster data.", exception);
        }
    }
}

public sealed class PrintRequestClient(
    HttpClient httpClient, string bearerToken,
    ILogger<PrintRequestClient>? logger = null, TimeSpan? requestTimeout = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async ValueTask<PrintRequestClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/agent/print-requests/claim",
            null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var claim = await response.Content.ReadFromJsonAsync<PrintRequestClaim>(
            JsonOptions, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("Coordinator returned an empty print request claim.");
        Validate(claim);
        return claim;
    }

    public async ValueTask ReportMediaAsync(
        int widthMillimeters,
        int heightMillimeters,
        int mediaType,
        string state,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/agent/printers/default/media",
            new
            {
                WidthMm = widthMillimeters,
                HeightMm = heightMillimeters,
                MediaType = mediaType,
                State = state,
            }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>false when a force reset deliberately forgot this request.</returns>
    public async ValueTask<bool> ReportAsync(
        Guid requestId,
        string state,
        string? detail,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation("Label {RequestId}: sending {State} report to coordinator", requestId, state);
        using var response = await SendAsync(HttpMethod.Post,
            $"api/agent/print-requests/{requestId:D}/report", new { state, detail },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = requestTimeout ?? TimeSpan.FromSeconds(5);
        deadline.CancelAfter(limit);
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Buffer the body inside the deadline too: ResponseHeadersRead ends
            // HttpClient's timeout at the headers, leaving claim bodies unbounded.
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent &&
                !string.Equals(path, "api/agent/printers/default/media", StringComparison.Ordinal))
            {
                logger?.LogInformation("Coordinator {Path}: HTTP {Status} in {ElapsedMs} ms",
                    path, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Coordinator request {path} exceeded {limit.TotalSeconds:0.###} seconds.");
        }
    }

    private static void Validate(PrintRequestClaim claim)
    {
        var continuous = string.Equals(claim.MediaKind, "continuous", StringComparison.Ordinal);
        var dieCut = string.Equals(claim.MediaKind, "die_cut", StringComparison.Ordinal);
        var integralWidth = claim.WidthMillimeters == decimal.Truncate(claim.WidthMillimeters) &&
            claim.WidthMillimeters is > 0 and <= byte.MaxValue;
        var expectedContinuousLines = decimal.ToInt32(decimal.Round(
            claim.HeightMillimeters * 300m / 25.4m, MidpointRounding.ToEven)) -
            2 * claim.FeedMarginDots;
        var validLength = continuous
            ? claim.HeightMillimeters is >= 12.7m and <= 1000m &&
              claim.FeedMarginDots is >= 35 and <= 1500 &&
              claim.RasterLines == expectedContinuousLines && claim.RasterLines > 0
            : dieCut && claim.HeightMillimeters == decimal.Truncate(claim.HeightMillimeters) &&
              claim.HeightMillimeters is > 0 and <= byte.MaxValue && claim.RasterLines > 0 &&
              claim.FeedMarginDots == 0;
        if (claim.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(claim.ProfileId) || claim.ProfileId.Length > 200 ||
            !string.Equals(claim.MediaType, "application/vnd.inventoryzing.brother-raster",
                StringComparison.Ordinal) ||
            !integralWidth || !validLength ||
            claim.RasterLineBytes != BrotherRasterProtocol.BytesPerRasterLine ||
            string.IsNullOrWhiteSpace(claim.RasterBase64))
        {
            throw new InvalidDataException("Coordinator returned an unsupported print request claim.");
        }

        if (claim.GetRaster().Length != checked(claim.RasterLineBytes * claim.RasterLines))
        {
            throw new InvalidDataException("Coordinator raster length does not match its claim.");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(string.IsNullOrWhiteSpace(detail)
                ? $"Coordinator returned HTTP {(int)response.StatusCode}."
                : detail, null, response.StatusCode);
        }
    }
}

public sealed class PrinterMediaWorker(
    PrintRequestClient client,
    string host,
    int statusPort,
    ILogger<PrinterMediaWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var statusClient = new BrotherSnmpStatusClient(host, statusPort);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(ProbeTimeout);
                var status = await statusClient.GetStatusAsync(deadline.Token).ConfigureAwait(false);
                await client.ReportMediaAsync(
                    status.MediaWidthMillimeters,
                    status.MediaLengthMillimeters,
                    status.MediaType,
                    status.State.ToString(),
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Unable to refresh installed printer media.");
                try
                {
                    await client.ReportMediaAsync(0, 0, 0, "Unavailable", stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (Exception reportException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug(reportException, "Unable to report unavailable printer media.");
                }
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

public sealed class RasterPrintWorker(
    PrintRequestClient client,
    string host,
    int port,
    int statusPort,
    TimeSpan idlePollInterval,
    TimeSpan statusPollInterval,
    TimeSpan completionTimeout,
    ILogger<RasterPrintWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PrintRequestClaim? claim;
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
                logger.LogWarning(exception, "Unable to claim a print request; retrying.");
                await Task.Delay(idlePollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (claim is null)
            {
                await Task.Delay(idlePollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await ProcessAsync(claim, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(PrintRequestClaim claim, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        var stage = "printer preflight";
        var dispatchStarted = false;
        logger.LogInformation("Label {RequestId}: claim received; starting {Stage}", claim.Id, stage);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(completionTimeout);
            var token = timeout.Token;
            var statusClient = new BrotherSnmpStatusClient(host, statusPort);
            var preflight = await statusClient.GetStatusAsync(token).ConfigureAwait(false);
            logger.LogInformation("Label {RequestId}: preflight returned in {ElapsedMs} ms; media {Width} x {Length}, state {State}",
                claim.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                preflight.MediaWidthMillimeters, preflight.MediaLengthMillimeters, preflight.State);
            var continuous = string.Equals(claim.MediaKind, "continuous", StringComparison.Ordinal);
            var mediaLength = continuous ? (byte)0 : checked((byte)claim.HeightMillimeters);
            if (!preflight.MatchesMedia(continuous,
                checked((byte)claim.WidthMillimeters), mediaLength))
            {
                var required = continuous
                    ? $"{claim.WidthMillimeters} mm continuous"
                    : $"{claim.WidthMillimeters} × {claim.HeightMillimeters} mm die-cut";
                var installed = preflight.MediaType == BrotherRasterProtocol.ContinuousMediaType
                    ? $"{preflight.MediaWidthMillimeters} mm continuous"
                    : $"{preflight.MediaWidthMillimeters} × {preflight.MediaLengthMillimeters} mm die-cut";
                throw new InvalidOperationException(
                    $"Install a {required} label roll. The printer currently reports {installed}.");
            }
            if (preflight.State != BrotherRasterState.Ready)
            {
                throw new InvalidOperationException(
                    "The printer needs attention. Check the cover, label roll, and cutter.");
            }
            var page = BrotherRasterProtocol.BuildMonochromePage(
                continuous, (int)claim.WidthMillimeters, mediaLength, claim.FeedMarginDots,
                claim.RasterLines, claim.GetRaster());
            stage = "sending raster";
            dispatchStarted = true;
            await using (var transport = new BrotherRasterTcpTransport(host, port))
            {
                await transport.ConnectAsync(token).ConfigureAwait(false);
                await transport.WriteAsync(page, token).ConfigureAwait(false);
            }
            logger.LogInformation("Label {RequestId}: raster sent in {ElapsedMs} ms; monitoring printer status",
                claim.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            stage = "waiting for printer completion";
            var sawPrinting = false;
            while (true)
            {
                await Task.Delay(statusPollInterval, token).ConfigureAwait(false);
                var status = await statusClient.GetStatusAsync(token).ConfigureAwait(false);
                if (status.State == BrotherRasterState.Blocked)
                {
                    throw new InvalidOperationException(
                        "Printing stopped because the printer needs attention. Check the cover, label roll, and cutter.");
                }
                sawPrinting |= status.State == BrotherRasterState.Printing;
                if (sawPrinting && status.State == BrotherRasterState.Ready)
                {
                    await ReportAsync(claim.Id, "Completed", "The label printed successfully.", stoppingToken)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Restarting the agent deliberately forgets the unpersisted request.
        }
        catch (Exception exception)
        {
            var detail = UserDetail(exception, stage);
            // Deliver the outcome before writing the full exception to the console.
            await ReportAsync(claim.Id, dispatchStarted && exception is OperationCanceledException ? "Unknown" : "Rejected", detail, stoppingToken)
                .ConfigureAwait(false);
            logger.LogWarning(exception, "Label {RequestId} failed during {Stage}; elapsed {ElapsedMs} ms",
                claim.Id, stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static string UserDetail(Exception exception, string stage)
    {
        if (exception is InvalidOperationException)
        {
            return exception.Message;
        }

        return stage switch
        {
            "printer preflight" => "Failed to reach printer.",
            "sending raster" => "Failed to send the label to the printer.",
            _ => "The printer did not confirm whether the label finished printing.",
        };
    }

    private async Task ReportAsync(
        Guid requestId,
        string state,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await client.ReportAsync(requestId, state, detail, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Label request {RequestId} was force-reset.", requestId);
            }
            else
            {
                logger.LogInformation("Label request {RequestId} reported {State}: {Detail}",
                    requestId, state, detail);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Unable to report label request {RequestId}.", requestId);
        }
    }
}
