using Inventoryzing.Agent.Printer.Host;

using var host = PrinterAgentHost.Build(args);
await host.RunAsync();