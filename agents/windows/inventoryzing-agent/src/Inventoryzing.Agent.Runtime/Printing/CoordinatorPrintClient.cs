using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record CoordinatorPrintClaim(
    Guid AttemptId,
    Guid LeaseId,
    DateTimeOffset LeaseExpiresAt,
    Guid ArtifactId,
    string ArtifactSha256,
    string MediaType,
    [property: JsonPropertyName("width_mm")]
    decimal WidthMillimeters,
    [property: JsonPropertyName("height_mm")]
    decimal HeightMillimeters,
    int PixelWidth,
    int PixelHeight,
    int DpiX,
    int DpiY,
    OutputPalette Palette,
    string ProfileId,
    int Copies,
    string DocumentName);

public sealed class CoordinatorPrintClient : IPrintDispatchAuthority, IPrintAttemptReportSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient httpClient;
    private readonly string agentId;
    private readonly string bearerToken;
    private readonly Lock sync = new();
    private readonly Dictionary<Guid, CoordinatorPrintClaim> claims = [];

    public CoordinatorPrintClient(HttpClient httpClient, string agentId, string bearerToken)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (httpClient.BaseAddress is null || !httpClient.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("Coordinator HTTP client requires an absolute base address.",
                nameof(httpClient));
        }
        this.agentId = agentId;
        this.bearerToken = bearerToken;
    }

    public async ValueTask<CoordinatorPrintClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/agent/print-attempts/claim",
            new { agent_id = agentId }, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var claim = await response.Content.ReadFromJsonAsync<CoordinatorPrintClaim>(
            JsonOptions, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("Coordinator returned an empty print claim.");
        ValidateClaim(claim);
        lock (sync)
        {
            claims[claim.AttemptId] = claim;
        }
        return claim;
    }

    private static void ValidateClaim(CoordinatorPrintClaim claim)
    {
        if (claim.AttemptId == Guid.Empty || claim.LeaseId == Guid.Empty ||
            claim.ArtifactId == Guid.Empty ||
            string.IsNullOrWhiteSpace(claim.ArtifactSha256) ||
            string.IsNullOrWhiteSpace(claim.MediaType) ||
            claim.WidthMillimeters <= 0 || claim.HeightMillimeters <= 0 ||
            claim.PixelWidth <= 0 || claim.PixelHeight <= 0 ||
            claim.DpiX <= 0 || claim.DpiY <= 0 ||
            string.IsNullOrWhiteSpace(claim.ProfileId) || claim.Copies <= 0 ||
            string.IsNullOrWhiteSpace(claim.DocumentName))
        {
            throw new InvalidDataException(
                "Coordinator returned an incomplete or invalid print claim.");
        }
    }

    public async ValueTask<byte[]> DownloadArtifactAsync(
        CoordinatorPrintClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using var response = await SendAsync(HttpMethod.Get,
            $"api/agent/print-artifacts/{claim.ArtifactId:D}", null, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrintDispatchAuthorization> AuthorizeAsync(
        PrintDispatchIntentRecord intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var claim = FindClaim(intent.AttemptId);
        using var response = await SendAsync(HttpMethod.Post,
            $"api/agent/print-attempts/{intent.AttemptId:D}/dispatch-started",
            new { lease_id = claim.LeaseId }, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden or
            HttpStatusCode.Unauthorized)
        {
            return new PrintDispatchAuthorization(Guid.NewGuid(),
                PrintDispatchAuthorizationStatus.Rejected,
                await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var authorization = await response.Content.ReadFromJsonAsync<AuthorizationResponse>(
            JsonOptions, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("Coordinator returned an empty dispatch acknowledgement.");
        return new PrintDispatchAuthorization(
            authorization.AcknowledgementId,
            authorization.Status,
            authorization.Detail);
    }

    public async ValueTask ReportAsync(
        PrintAttemptReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var claim = TryFindClaim(report.Attempt.AttemptId);
        var payload = new
        {
            lease_id = claim?.LeaseId,
            state = report.Attempt.Status.State,
            completion_evidence = report.Attempt.Status.CompletionEvidence,
            observations = report.Observations.Select(item => new
            {
                observation_id = item.Observation.ObservationId,
                source = item.Observation.Source,
                code = item.Observation.Code,
                detail = item.Observation.Detail,
                raw_job_status = item.Observation.RawJobStatus,
                raw_printer_status = item.Observation.RawPrinterStatus,
                raw_provider_status = item.Observation.RawProviderStatus,
                observed_at = item.Observation.ObservedAt,
            }),
        };
        using var response = await SendAsync(HttpMethod.Post,
            $"api/agent/print-attempts/{report.Attempt.AttemptId:D}/report",
            payload, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private CoordinatorPrintClaim FindClaim(Guid attemptId)
    {
        lock (sync)
        {
            return claims.TryGetValue(attemptId, out var claim)
                ? claim
                : throw new InvalidOperationException(
                    $"No active coordinator claim exists for print attempt '{attemptId}'.");
        }
    }

    private CoordinatorPrintClaim? TryFindClaim(Guid attemptId)
    {
        lock (sync)
        {
            return claims.GetValueOrDefault(attemptId);
        }
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
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false),
                null,
                response.StatusCode);
        }
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(content)
            ? $"Coordinator returned HTTP {(int)response.StatusCode}."
            : content;
    }

    private sealed record AuthorizationResponse(
        Guid AcknowledgementId,
        PrintDispatchAuthorizationStatus Status,
        string? Detail);
}
