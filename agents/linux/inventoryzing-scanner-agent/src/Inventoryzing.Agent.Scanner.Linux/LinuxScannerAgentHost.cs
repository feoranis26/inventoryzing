using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime;
using Inventoryzing.Agent.Scanner.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventoryzing.Agent.Scanner.Linux;

public static class LinuxScannerAgentHost
{
    private const string DataDirectoryName = "Scanner";

    public static IHost Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(LinuxScannerAgentHost).Assembly.GetName().Name,
            Args = args,
        });
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff 'UTC' ";
            options.UseUtcTimestamp = true;
        });
        builder.Services.AddInventoryzingAgentRuntime(builder.Configuration, DataDirectoryName);
        builder.Services
            .AddOptions<ScannerAgentOptions>()
            .Bind(builder.Configuration.GetSection(ScannerAgentOptions.SectionName))
            .Validate(ValidScannerOptions, $"{ScannerAgentOptions.SectionName} is incomplete or invalid.")
            .ValidateOnStart();
        builder.Services
            .AddOptions<LinuxCoreScannerOptions>()
            .Bind(builder.Configuration.GetSection(LinuxCoreScannerOptions.SectionName))
            .Validate(options => Path.IsPathFullyQualified(options.BridgePath) &&
                options.TopologyTimeout > TimeSpan.Zero && options.TopologyTimeout <= TimeSpan.FromSeconds(10),
                $"{LinuxCoreScannerOptions.SectionName} is incomplete or invalid.")
            .ValidateOnStart();

        var configured = builder.Configuration.GetSection(ScannerAgentOptions.SectionName)
            .Get<ScannerAgentOptions>();
        if (configured?.Enabled == true)
        {
            AddLiveScannerWorker(builder.Services);
        }
        return builder.Build();
    }

    private static void AddLiveScannerWorker(IServiceCollection services)
    {
        services.AddSingleton<LinuxCoreScannerProbe>();
        services.AddSingleton<IScannerProbe>(provider => provider.GetRequiredService<LinuxCoreScannerProbe>());
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ScannerAgentOptions>>().Value;
            var token = File.ReadAllText(options.CredentialFile).Trim();
            var client = new HttpClient { BaseAddress = EnsureTrailingSlash(options.CoordinatorUri!) };
            return new ScanDeliveryClient(client, token, options.TerminalId, options.RequestTimeout,
                provider.GetRequiredService<ILogger<ScanDeliveryClient>>());
        });
        services.AddHostedService<LiveScannerWorker>();
    }

    private static bool ValidScannerOptions(ScannerAgentOptions options) =>
        !options.Enabled ||
        (options.CoordinatorUri is { IsAbsoluteUri: true } &&
         options.CoordinatorUri.Scheme is "http" or "https" &&
         Path.IsPathFullyQualified(options.CredentialFile) &&
         options.TerminalId != Guid.Empty &&
         options.RequestTimeout >= TimeSpan.FromMilliseconds(100) &&
         options.RequestTimeout <= TimeSpan.FromSeconds(10) &&
         options.FeedbackFlashDuration >= TimeSpan.Zero &&
         options.FeedbackFlashDuration <= TimeSpan.FromSeconds(5));

    private static Uri EnsureTrailingSlash(Uri value) =>
        new(value.AbsoluteUri.EndsWith('/') ? value.AbsoluteUri : $"{value.AbsoluteUri}/");
}
