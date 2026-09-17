namespace Inventoryzing.Agent.Runtime;

public sealed class AgentRuntimeOptions
{
    public const string SectionName = "Inventoryzing:Agent";

    public string DataDirectory { get; set; } = string.Empty;

    public TimeSpan ShutdownTimeout { get; set; } = AgentRuntimeDefaults.ShutdownTimeout;
}