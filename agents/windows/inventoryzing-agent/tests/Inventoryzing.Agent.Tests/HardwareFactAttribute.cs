namespace Inventoryzing.Agent.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public const string ScenarioEnvironmentVariable = "INVENTORYZING_HARDWARE_TEST";

    public HardwareFactAttribute(string skipReason)
    {
        Skip = skipReason;
    }

    public HardwareFactAttribute(string scenario, string skipReason)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(ScenarioEnvironmentVariable),
            scenario,
            StringComparison.Ordinal))
        {
            Skip = $"{skipReason} Set {ScenarioEnvironmentVariable}={scenario} to authorize only this scenario.";
        }
    }
}