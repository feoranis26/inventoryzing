using System.Net;
using System.Text;
using Inventoryzing.Agent.Printer.Host;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintRequestClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_covers_stalled_response_body_for_claim_and_report(bool report)
    {
        using var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StalledContent(),
        })) { BaseAddress = new Uri("http://coordinator/") };
        var client = new PrintRequestClient(http, "token", requestTimeout: TimeSpan.FromMilliseconds(100));
        var operation = report
            ? Assert.ThrowsAsync<TimeoutException>(async () =>
                await client.ReportAsync(Guid.NewGuid(), "Rejected", "preflight failed", CancellationToken.None))
            : Assert.ThrowsAsync<TimeoutException>(async () => await client.ClaimAsync(CancellationToken.None));
        await operation.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class StalledContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [Theory]
    [InlineData(29, 90, 991, "die_cut", 0)]
    [InlineData(62, 29, 271, "die_cut", 0)]
    [InlineData(62, 100, 1109, "die_cut", 0)]
    [InlineData(17, 54, 566, "die_cut", 0)]
    [InlineData(63, 29, 271, "die_cut", 0)]
    [InlineData(62, 50, 521, "continuous", 35)]
    [InlineData(12, 50, 521, "continuous", 35)]
    public async Task Claim_and_report_use_the_print_request_raster_contract(
        int width, int height, int lines, string mediaKind, int feedMargin)
    {
        var requestId = Guid.NewGuid();
        var raster = new byte[90 * lines];
        raster[0] = 0x80;
        var handler = new Handler((_, index) => index switch
        {
            0 => Json(HttpStatusCode.OK, $$"""
                {"id":"{{requestId}}","profile_id":"brother-ql820nwb-{{width}}x{{height}}-mono-300",
                 "media_type":"application/vnd.inventoryzing.brother-raster",
                 "width_mm":{{width}},"height_mm":{{height}},"media_kind":"{{mediaKind}}",
                 "feed_margin_dots":{{feedMargin}},"raster_line_bytes":90,"raster_lines":{{lines}},
                 "raster_base64":"{{Convert.ToBase64String(raster)}}"}
                """),
            1 => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new InvalidOperationException(),
        });
        var client = new PrintRequestClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://coordinator/") }, "secret-token");

        var claim = await client.ClaimAsync(CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(width, claim.WidthMillimeters);
        Assert.Equal(height, claim.HeightMillimeters);
        Assert.Equal(lines, claim.RasterLines);
        Assert.Equal(raster, claim.GetRaster());
        Assert.True(await client.ReportAsync(requestId, "Completed", "done", CancellationToken.None));
        Assert.Equal("/api/agent/print-requests/claim", handler.Requests[0].Path);
        Assert.Equal($"/api/agent/print-requests/{requestId:D}/report", handler.Requests[1].Path);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer secret-token", request.Authorization));
        Assert.Contains("\"state\":\"Completed\"", handler.Requests[1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_reset_makes_a_late_report_a_noop()
    {
        var client = new PrintRequestClient(new HttpClient(new Handler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            BaseAddress = new Uri("http://coordinator/"),
        }, "secret-token");

        Assert.False(await client.ReportAsync(Guid.NewGuid(), "Rejected", "reset",
            CancellationToken.None));
    }

    [Fact]
    public async Task Media_report_uses_observed_roll_dimensions()
    {
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new PrintRequestClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://coordinator/") },
            "secret-token");

        await client.ReportMediaAsync(29, 90, 0x0b, "Ready", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/agent/printers/default/media", request.Path);
        Assert.Contains("\"width_mm\":29", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"height_mm\":90", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"media_type\":11", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Ready\"", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 29, 271, "die_cut", 0, false)]
    [InlineData(62, 50, 520, "continuous", 35, false)]
    [InlineData(62, 50, 591, "continuous", 0, false)]
    [InlineData(62, 29, 271, "unknown", 0, false)]
    [InlineData(62, 29, 271, "die_cut", 0, true)]
    public async Task Rejects_inconsistent_or_unsupported_media_claims(
        int width, int height, int lines, string mediaKind, int feedMargin, bool truncated)
    {
        var raster = new byte[90 * lines - (truncated ? 1 : 0)];
        using var http = new HttpClient(new Handler((_, _) => Json(HttpStatusCode.OK, $$"""
            {"id":"{{Guid.NewGuid()}}","profile_id":"coordinator-owned-profile",
             "media_type":"application/vnd.inventoryzing.brother-raster",
             "width_mm":{{width}},"height_mm":{{height}},"media_kind":"{{mediaKind}}",
             "feed_margin_dots":{{feedMargin}},"raster_line_bytes":90,"raster_lines":{{lines}},
             "raster_base64":"{{Convert.ToBase64String(raster)}}"}
            """))) { BaseAddress = new Uri("http://coordinator/") };
        var client = new PrintRequestClient(http, "token");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await client.ClaimAsync(CancellationToken.None));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
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
            Requests.Add(new CapturedRequest(
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty :
                    await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request, Requests.Count - 1);
        }
    }

    private sealed record CapturedRequest(string Path, string? Authorization, string Body);
}
