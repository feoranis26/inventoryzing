using Inventoryzing.Agent.Printer.Host;
using Inventoryzing.Agent.Brother;

// Explicit, read-only hardware check: no coordinator claim and no label data.
if (args.Length == 2 && args[0] == "--usb-probe")
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var session = new BrotherRasterSession(new BrotherRasterUsbTransport(args[1]));
    var status = await session.PreflightAsync(deadline.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(status));
    return;
}

using var host = PrinterAgentHost.Build(args);
await host.RunAsync();
