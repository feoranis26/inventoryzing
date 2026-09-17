using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Brother;

public static class BrotherBpacModelCapabilities
{
    public static PrinterMonitorCompletionCapability GetCompletionCapability(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return PrinterMonitorCompletionCapability.Unknown;
    }
}