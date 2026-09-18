namespace Inventoryzing.Agent.Scanner.Linux;

public sealed class LinuxCoreScannerOptions
{
    public const string SectionName = "Inventoryzing:Scanner:LinuxCoreScanner";

    public string BridgePath { get; set; } = "/usr/local/bin/inventoryzing-zebra-bridge";

    public string? BridgeArguments { get; set; }

    public TimeSpan TopologyTimeout { get; set; } = TimeSpan.FromSeconds(1);
}
