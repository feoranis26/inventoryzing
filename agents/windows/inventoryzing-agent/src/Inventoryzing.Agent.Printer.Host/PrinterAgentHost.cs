using Inventoryzing.Agent.Brother;
using Inventoryzing.Agent.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventoryzing.Agent.Printer.Host;

public static class PrinterAgentHost
{
    public const string ServiceName = "Inventoryzing Printer Agent";

    internal const string DataDirectoryName = "Printer";

    internal static Type ProviderType => typeof(BrotherRasterTcpTransport);

    public static IHost Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = typeof(PrinterAgentHost).Assembly.GetName().Name,
                Args = args,
            });
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff 'UTC' ";
            options.UseUtcTimestamp = true;
        });
        builder.Services.AddInventoryzingAgentRuntime(builder.Configuration, DataDirectoryName);
        builder.Services
            .AddOptions<PrinterAgentOptions>()
            .Bind(builder.Configuration.GetSection(PrinterAgentOptions.SectionName))
            .Validate(ValidPrinterOptions,
                $"{PrinterAgentOptions.SectionName} is incomplete or invalid.")
            .ValidateOnStart();
        var configured = builder.Configuration.GetSection(PrinterAgentOptions.SectionName)
            .Get<PrinterAgentOptions>();
        if (configured?.Enabled == true)
        {
            AddRasterPrintWorker(builder.Services);
        }
        return builder.Build();
    }

    private static void AddRasterPrintWorker(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PrinterAgentOptions>>().Value;
            var token = File.ReadAllText(options.CredentialFile).Trim();
            var httpClient = new HttpClient { BaseAddress = EnsureTrailingSlash(options.CoordinatorUri!) };
            return new PrintRequestClient(httpClient, token,
                provider.GetRequiredService<ILogger<PrintRequestClient>>());
        });
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PrinterAgentOptions>>().Value;
            return new RasterPrintWorker(
                provider.GetRequiredService<PrintRequestClient>(),
                options.RasterHost,
                options.RasterPort,
                options.StatusPort,
                options.PollInterval,
                options.StatusPollInterval,
                options.CompletionTimeout,
                provider.GetRequiredService<ILogger<RasterPrintWorker>>());
        });
        services.AddHostedService(provider =>
            provider.GetRequiredService<RasterPrintWorker>());
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PrinterAgentOptions>>().Value;
            return new PrinterMediaWorker(
                provider.GetRequiredService<PrintRequestClient>(),
                options.RasterHost,
                options.StatusPort,
                provider.GetRequiredService<ILogger<PrinterMediaWorker>>());
        });
        services.AddHostedService(provider =>
            provider.GetRequiredService<PrinterMediaWorker>());
    }

    private static bool ValidPrinterOptions(PrinterAgentOptions options)
    {
        if (!options.Enabled)
        {
            return true;
        }
        return options.CoordinatorUri is { IsAbsoluteUri: true } &&
            options.CoordinatorUri.Scheme is "http" or "https" &&
            Path.IsPathFullyQualified(options.CredentialFile) &&
            !string.IsNullOrWhiteSpace(options.PrinterId) &&
            !string.IsNullOrWhiteSpace(options.RasterHost) &&
            options.RasterPort is > 0 and <= ushort.MaxValue &&
            options.StatusPort is > 0 and <= ushort.MaxValue &&
            options.PollInterval >= TimeSpan.FromMilliseconds(250) &&
            options.StatusPollInterval >= TimeSpan.FromMilliseconds(50) &&
            options.CompletionTimeout >= TimeSpan.FromSeconds(1);
    }

    private static Uri EnsureTrailingSlash(Uri value)
    {
        var text = value.AbsoluteUri;
        return new Uri(text.EndsWith('/') ? text : $"{text}/");
    }
}
