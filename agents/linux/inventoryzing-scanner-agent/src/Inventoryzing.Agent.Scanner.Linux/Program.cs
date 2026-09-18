using Inventoryzing.Agent.Scanner.Linux;

using var host = LinuxScannerAgentHost.Build(args);
await host.RunAsync();
