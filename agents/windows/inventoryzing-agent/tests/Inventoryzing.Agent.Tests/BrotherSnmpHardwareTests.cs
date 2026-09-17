using Inventoryzing.Agent.Brother;

namespace Inventoryzing.Agent.Tests;

public sealed class BrotherSnmpHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Reads_the_documented_status_object_when_a_host_is_configured()
    {
        var host = Environment.GetEnvironmentVariable("INVENTORYZING_BROTHER_SNMP_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var status = await new BrotherSnmpStatusClient(host).GetStatusAsync(timeout.Token);

        Assert.Equal((byte)'A', status.ModelCode);
        Assert.True(status.MatchesDieCutMedia(29, 90));
        Assert.Equal(BrotherRasterState.Ready, status.State);
    }
}
