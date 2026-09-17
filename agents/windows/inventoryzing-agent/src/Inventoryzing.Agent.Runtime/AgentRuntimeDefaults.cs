namespace Inventoryzing.Agent.Runtime;

public static class AgentRuntimeDefaults
{
    public static TimeSpan ShutdownTimeout { get; } = TimeSpan.FromSeconds(30);

    public static TimeSpan MinimumShutdownTimeout { get; } = TimeSpan.FromSeconds(5);

    public static TimeSpan MaximumShutdownTimeout { get; } = TimeSpan.FromMinutes(5);

    public static string GetDataDirectory(string agentDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectoryName);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Inventoryzing",
            agentDirectoryName);
    }
}