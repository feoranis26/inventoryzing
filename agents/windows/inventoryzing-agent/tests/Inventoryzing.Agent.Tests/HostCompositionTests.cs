using Inventoryzing.Agent.Brother;
using Inventoryzing.Agent.Printer.Host;
using Inventoryzing.Agent.Runtime;
using Inventoryzing.Agent.Scanner.Host;
using Inventoryzing.Agent.Zebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inventoryzing.Agent.Tests;

public sealed class HostCompositionTests
{
    [Fact]
    public void Printer_host_builds_without_activating_bpac()
    {
        using var host = PrinterAgentHost.Build([]);

        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        var hostOptions = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        var runtimeOptions = host.Services.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
        Assert.Equal("Inventoryzing.Agent.Printer.Host", environment.ApplicationName);
        Assert.Equal(AgentRuntimeDefaults.ShutdownTimeout, hostOptions.ShutdownTimeout);
        Assert.Equal(
            AgentRuntimeDefaults.GetDataDirectory(PrinterAgentHost.DataDirectoryName),
            runtimeOptions.DataDirectory);
        Assert.Equal(typeof(BrotherRasterTcpTransport), PrinterAgentHost.ProviderType);
    }

    [Fact]
    public void Scanner_host_builds_without_activating_core_scanner()
    {
        using var host = ScannerAgentHost.Build([]);

        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        var hostOptions = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        var runtimeOptions = host.Services.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
        Assert.Equal("Inventoryzing.Agent.Scanner.Host", environment.ApplicationName);
        Assert.Equal(AgentRuntimeDefaults.ShutdownTimeout, hostOptions.ShutdownTimeout);
        Assert.Equal(
            AgentRuntimeDefaults.GetDataDirectory(ScannerAgentHost.DataDirectoryName),
            runtimeOptions.DataDirectory);
        Assert.Equal(typeof(ZebraScannerProbe), ScannerAgentHost.ProviderType);
    }

    [Fact]
    public void Relative_data_directory_is_rejected_during_options_validation()
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            PrinterAgentHost.Build(["--Inventoryzing:Agent:DataDirectory=relative-data"]));

        Assert.Contains("fully qualified path", exception.Message, StringComparison.Ordinal);
    }
}
