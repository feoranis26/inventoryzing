using System.Net;
using System.Text;
using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;

namespace Inventoryzing.Agent.Tests;

public sealed class CoordinatorPrintClientTests
{
    [Fact]
    public async Task Claim_download_and_authorize_use_bearer_and_snake_case_contract()
    {
        var attemptId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var acknowledgementId = Guid.NewGuid();
        var handler = new Handler((request, index) => index switch
        {
            0 => Json(HttpStatusCode.OK, $$"""
                {
                  "attempt_id":"{{attemptId}}", "lease_id":"{{leaseId}}",
                  "lease_expires_at":"2026-09-15T12:01:00Z", "artifact_id":"{{artifactId}}",
                  "artifact_sha256":"{{new string('a', 64)}}", "media_type":"image/png",
                  "width_mm":29, "height_mm":90, "pixel_width":343, "pixel_height":1063,
                  "dpi_x":300, "dpi_y":300, "palette":"Monochrome",
                  "profile_id":"brother-ql820nwb-29x90-mono-300", "copies":1,
                  "document_name":"inventoryzing-{{attemptId:N}}"
                }
                """),
            1 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            },
            2 => Json(HttpStatusCode.OK, $$"""
                {"acknowledgement_id":"{{acknowledgementId}}", "status":"Granted",
                 "detail":"acknowledged"}
                """),
            _ => throw new InvalidOperationException(),
        });
        var client = new CoordinatorPrintClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://coordinator/") },
            "test-agent",
            "secret-token");

        var claim = await client.ClaimAsync(CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(attemptId, claim.AttemptId);
        Assert.Equal(29m, claim.WidthMillimeters);
        Assert.Equal(90m, claim.HeightMillimeters);
        Assert.Equal([1, 2, 3],
            await client.DownloadArtifactAsync(claim, CancellationToken.None));
        var authorization = await client.AuthorizeAsync(
            new PrintDispatchIntentRecord(attemptId, "printer", new string('a', 64),
                new string('b', 64), DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(acknowledgementId, authorization.AcknowledgementId);
        Assert.All(handler.Requests, request =>
            Assert.Equal("Bearer secret-token", request.Authorization));
        Assert.Contains("\"agent_id\":\"test-agent\"", handler.Requests[0].Body,
            StringComparison.Ordinal);
        Assert.Contains($"\"lease_id\":\"{leaseId}\"", handler.Requests[2].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_after_restart_does_not_require_an_in_memory_claim()
    {
        var attemptId = Guid.NewGuid();
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, $$"""
            {"id":"{{attemptId}}", "state":"DriverAccepted", "version":3,
             "completion_evidence":null, "updated_at":"2026-09-15T12:01:00Z"}
            """));
        var client = new CoordinatorPrintClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://coordinator/") },
            "test-agent",
            "secret-token");
        var now = DateTimeOffset.UtcNow;
        var report = new PrintAttemptReport(
            new PrintAttemptRecord(
                attemptId,
                new PrintAttemptStatus(PrintAttemptState.DriverAccepted),
                3,
                now,
                now),
            null,
            null,
            null,
            []);

        await client.ReportAsync(report, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/api/agent/print-attempts/{attemptId:D}/report", request.Path);
        Assert.Contains("\"lease_id\":null", request.Body, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return respond(request, Requests.Count - 1);
        }
    }

    private sealed record CapturedRequest(string Path, string? Authorization, string Body);
}
