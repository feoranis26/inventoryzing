using Inventoryzing.Agent.Scanner.Host;

using var host = ScannerAgentHost.Build(args);
await host.RunAsync();