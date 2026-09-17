using Microsoft.Extensions.Hosting;

namespace Inventoryzing.Agent.Runtime;

internal sealed class AgentRuntimeLifecycleService(AgentRuntimeHealth health) : BackgroundService
{
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        health.SetState(AgentRuntimeState.Stopping);
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        health.SetState(AgentRuntimeState.Running);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            health.SetState(AgentRuntimeState.Stopped);
        }
    }
}