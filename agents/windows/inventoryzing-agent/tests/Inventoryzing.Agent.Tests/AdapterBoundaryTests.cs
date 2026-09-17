using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Printer.Host;
using Inventoryzing.Agent.Runtime;
using Inventoryzing.Agent.Scanner.Host;

namespace Inventoryzing.Agent.Tests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void Vendor_implementations_live_in_separate_adapter_assemblies()
    {
        var coreAssembly = typeof(IPrinterProbe).Assembly;
        var brotherAssembly = typeof(BrotherPrinterProbe).Assembly;
        var zebraAssembly = typeof(ZebraScannerProbe).Assembly;

        Assert.Equal("Inventoryzing.Agent.Core", coreAssembly.GetName().Name);
        Assert.Equal("Inventoryzing.Agent.Brother", brotherAssembly.GetName().Name);
        Assert.Equal("Inventoryzing.Agent.Zebra", zebraAssembly.GetName().Name);
        Assert.NotSame(coreAssembly, brotherAssembly);
        Assert.NotSame(coreAssembly, zebraAssembly);
        Assert.NotSame(brotherAssembly, zebraAssembly);
    }

    [Fact]
    public void Core_does_not_reference_vendor_adapter_assemblies()
    {
        var references = typeof(IPrinterProbe).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is "Inventoryzing.Agent.Brother" or "Inventoryzing.Agent.Zebra");
    }

    [Fact]
    public void Runtime_and_printer_host_follow_dependency_direction()
    {
        var runtimeReferences = ReferenceNames(typeof(AgentRuntimeDefaults));
        var printerReferences = ReferenceNames(typeof(PrinterAgentHost));
        var scannerReferences = ReferenceNames(typeof(ScannerAgentHost));

        Assert.DoesNotContain("Inventoryzing.Agent.Brother", runtimeReferences);
        Assert.DoesNotContain("Inventoryzing.Agent.Zebra", runtimeReferences);
        Assert.Contains("Inventoryzing.Agent.Runtime", printerReferences);
        Assert.Contains("Inventoryzing.Agent.Brother", printerReferences);
        Assert.DoesNotContain("Inventoryzing.Agent.Zebra", printerReferences);
        Assert.Contains("Inventoryzing.Agent.Runtime", scannerReferences);
        Assert.Contains("Inventoryzing.Agent.Zebra", scannerReferences);
        Assert.DoesNotContain("Inventoryzing.Agent.Brother", scannerReferences);
    }

    private static string?[] ReferenceNames(Type type) =>
        [.. type.Assembly.GetReferencedAssemblies().Select(reference => reference.Name)];
}