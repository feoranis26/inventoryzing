using Inventoryzing.Agent.Printer.Host;
using Inventoryzing.Agent.Runtime;
using Inventoryzing.Agent.Scanner.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inventoryzing.Agent.Tests;

public sealed class HostLifecycleTests
{
    [Theory]
    [InlineData("printer")]
    [InlineData("scanner")]
    public async Task Host_start_and_stop_update_process_health_without_device_access(string hostType)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"inventoryzing-host-{hostType}-{Guid.NewGuid():N}");
        try
        {
            using var host = BuildHost(hostType, directoryPath);
            var health = host.Services.GetRequiredService<AgentRuntimeHealth>();
            Assert.Equal(AgentRuntimeState.Starting, health.Current.State);

            await host.StartAsync();

            Assert.Equal(AgentRuntimeState.Running, health.Current.State);

            await host.StopAsync();

            Assert.Equal(AgentRuntimeState.Stopped, health.Current.State);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static IHost BuildHost(string hostType, string directoryPath)
    {
        var args = new[] { $"--Inventoryzing:Agent:DataDirectory={directoryPath}" };
        return hostType switch
        {
            "printer" => PrinterAgentHost.Build(args),
            "scanner" => ScannerAgentHost.Build(args),
            _ => throw new ArgumentOutOfRangeException(nameof(hostType)),
        };
    }
}