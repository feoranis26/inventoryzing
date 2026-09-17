using Inventoryzing.Agent.Runtime;
using Inventoryzing.Agent.Zebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Scanner.Host;

public static class ScannerAgentHost
{
    public const string ServiceName = "Inventoryzing Scanner Agent";

    internal const string DataDirectoryName = "Scanner";

    internal static Type ProviderType => typeof(ZebraScannerProbe);

    public static IHost Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = typeof(ScannerAgentHost).Assembly.GetName().Name,
                Args = args,
            });
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
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
        services.AddSingleton<ZebraScannerProbe>();
        services.AddSingleton<IScannerProbe>(provider => provider.GetRequiredService<ZebraScannerProbe>());
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ScannerAgentOptions>>().Value;
            var token = File.ReadAllText(options.CredentialFile).Trim();
            var httpClient = new HttpClient { BaseAddress = EnsureTrailingSlash(options.CoordinatorUri!) };
            return new ScanDeliveryClient(httpClient, token, options.TerminalId, options.RequestTimeout,
                provider.GetRequiredService<ILogger<ScanDeliveryClient>>());
        });
        services.AddHostedService<LiveScannerWorker>();
    }

    private static bool ValidScannerOptions(ScannerAgentOptions options)
    {
        if (!options.Enabled)
        {
            return true;
        }
        return options.CoordinatorUri is { IsAbsoluteUri: true } &&
            options.CoordinatorUri.Scheme is "http" or "https" &&
            Path.IsPathFullyQualified(options.CredentialFile) &&
            options.TerminalId != Guid.Empty &&
            options.RequestTimeout >= TimeSpan.FromMilliseconds(100) &&
            options.RequestTimeout <= TimeSpan.FromSeconds(10) &&
            options.FeedbackFlashDuration >= TimeSpan.Zero &&
            options.FeedbackFlashDuration <= TimeSpan.FromSeconds(5);
    }

    private static Uri EnsureTrailingSlash(Uri value)
    {
        var text = value.AbsoluteUri;
        return new Uri(text.EndsWith('/') ? text : $"{text}/");
    }
}
