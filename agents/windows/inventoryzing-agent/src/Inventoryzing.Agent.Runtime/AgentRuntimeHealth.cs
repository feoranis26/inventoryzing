namespace Inventoryzing.Agent.Runtime;

public enum AgentRuntimeState
{
    Starting,
    Running,
    Stopping,
    Stopped,
}

public sealed record AgentRuntimeHealthSnapshot(
    AgentRuntimeState State,
    DateTimeOffset ChangedAt);

public sealed class AgentRuntimeHealth(TimeProvider clock)
{
    private readonly Lock sync = new();
    private readonly TimeProvider timeProvider = clock ?? throw new ArgumentNullException(nameof(clock));
    private AgentRuntimeHealthSnapshot current = new(AgentRuntimeState.Starting, clock.GetUtcNow());

    public AgentRuntimeHealthSnapshot Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    internal void SetState(AgentRuntimeState state)
    {
        lock (sync)
        {
            current = new AgentRuntimeHealthSnapshot(state, timeProvider.GetUtcNow());
        }
    }
}