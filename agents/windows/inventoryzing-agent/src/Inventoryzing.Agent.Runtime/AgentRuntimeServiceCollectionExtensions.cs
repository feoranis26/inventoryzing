using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;

namespace Inventoryzing.Agent.Runtime;

public static class AgentRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryzingAgentRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        string agentDirectoryName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectoryName);

        services
            .AddOptions<AgentRuntimeOptions>()
            .Configure(options =>
                options.DataDirectory = AgentRuntimeDefaults.GetDataDirectory(agentDirectoryName))
            .Bind(configuration.GetSection(AgentRuntimeOptions.SectionName))
            .Validate(
                options =>
                    !string.IsNullOrWhiteSpace(options.DataDirectory) &&
                    Path.IsPathFullyQualified(options.DataDirectory),
                $"{AgentRuntimeOptions.SectionName}:DataDirectory must be a fully qualified path.")
            .Validate(
                options =>
                    options.ShutdownTimeout >= AgentRuntimeDefaults.MinimumShutdownTimeout &&
                    options.ShutdownTimeout <= AgentRuntimeDefaults.MaximumShutdownTimeout,
                $"{AgentRuntimeOptions.SectionName}:ShutdownTimeout must be between " +
                $"{AgentRuntimeDefaults.MinimumShutdownTimeout} and " +
                $"{AgentRuntimeDefaults.MaximumShutdownTimeout}.")
            .ValidateOnStart();
        services
            .AddOptions<HostOptions>()
            .Configure<IOptions<AgentRuntimeOptions>>((hostOptions, runtimeOptions) =>
                hostOptions.ShutdownTimeout = runtimeOptions.Value.ShutdownTimeout);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<AgentRuntimeHealth>();
        services.AddHostedService<AgentRuntimeLifecycleService>();
        return services;
    }

    public static IServiceCollection AddInventoryzingPrinterRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<SqlitePrintAttemptJournal>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
            return new SqlitePrintAttemptJournal(
                Path.Combine(options.DataDirectory, "print-journal.db"));
        });
        services.TryAddSingleton<IPrintAttemptJournal>(serviceProvider =>
            serviceProvider.GetRequiredService<SqlitePrintAttemptJournal>());
        services.TryAddSingleton<IPrinterStatusJournal>(serviceProvider =>
            serviceProvider.GetRequiredService<SqlitePrintAttemptJournal>());
        services.TryAddSingleton<IPrintArtifactCache>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
            return new FilePrintArtifactCache(Path.Combine(options.DataDirectory, "artifacts"));
        });
        services.TryAddSingleton<IPrintQueueMonitor, WindowsPrintQueueMonitor>();
        services.TryAddSingleton<DurablePrintQueueObserver>();
        services.TryAddSingleton<DurablePrinterMonitorObserver>();
        services.TryAddSingleton<DurablePrinterStatusObserver>();
        services.TryAddSingleton<PrinterRuntimeMetrics>();
        services.TryAddSingleton<IPrintDispatchAuthority, LocalPrintDispatchAuthority>();
        services.TryAddSingleton<PrintDispatchCoordinator>();
        services.TryAddSingleton<MonitoredPrintExecutionCoordinator>();
        services.TryAddSingleton<PrintDispatchRecovery>();
        services.TryAddSingleton<PrintStartupReconciler>();
        services.AddHostedService<PrinterRecoveryHostedService>();
        return services;
    }
}
