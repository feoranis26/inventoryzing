using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class BrotherBpacComClientTests
{
    [Fact]
    public void Construct_and_dispose_do_not_activate_bpac_com()
    {
        var activations = 0;
        using var client = new BrotherBpacComClient(_ =>
        {
            activations++;
            return new object();
        });

        client.Dispose();

        Assert.Equal(0, activations);
    }

    [Fact]
    public void Printer_availability_uses_parameterized_standalone_printer_calls()
    {
        var printer = new FakeAutomationPrinter
        {
            ErrorCode = 17,
            ErrorString = "Synthetic availability status",
        };
        var activations = new List<string>();
        using var client = new BrotherBpacComClient(progId =>
        {
            activations.Add(progId);
            return printer;
        });

        var availability = client.GetPrinterAvailability("Brother QL-820NWB");

        Assert.Equal([BrotherBpacComClient.PrinterProgId], activations);
        Assert.True(availability.IsSupported);
        Assert.True(availability.IsOnline);
        Assert.Equal(17, availability.ErrorCode);
        Assert.Equal("Synthetic availability status", availability.ErrorMessage);
        Assert.Equal("Brother QL-820NWB", printer.Name);
        Assert.Equal(
            [
                "IsPrinterSupported:Brother QL-820NWB",
                "IsPrinterOnline:Brother QL-820NWB",
            ],
            printer.Calls);
    }

    [Fact]
    public void Selected_printer_status_uses_the_open_documents_read_only_printer()
    {
        var printer = new FakeAutomationPrinter
        {
            LoadedMediaName = "62mm x 29mm",
            LoadedMediaId = 42,
        };
        var document = new FakeAutomationDocument(printer);
        var activations = new List<string>();
        using var client = new BrotherBpacComClient(progId =>
        {
            activations.Add(progId);
            return document;
        });

        Assert.True(client.Open(@"C:\labels\asset.lbx"));
        Assert.True(client.SetPrinter("Brother QL-820NWB", fitPage: false));
        var status = client.GetSelectedPrinterStatus(
            BrotherMediaSelection.ByName("62mm x 29mm"));

        Assert.Equal([BrotherBpacComClient.DocumentProgId], activations);
        Assert.True(status.IsSupported);
        Assert.True(status.IsOnline);
        Assert.True(status.IsMediaSupported);
        Assert.Equal("62mm x 29mm", status.LoadedMediaName);
        Assert.Equal(42, status.LoadedMediaId);
        Assert.Equal(
            [
                "IsPrinterSupported:Brother QL-820NWB",
                "IsPrinterOnline:Brother QL-820NWB",
                "IsMediaNameSupported:62mm x 29mm",
                "GetMediaName",
                "GetMediaId",
            ],
            printer.Calls);
    }

    [Fact]
    public void Discovery_snapshots_installed_printers_media_and_status()
    {
        var printer = new FakeAutomationPrinter
        {
            InstalledPrinters = ["Brother QL-820NWB", "Brother PT-P900W"],
            SupportedMediaNames = ["62mm", "62mm x 29mm"],
            SupportedMediaIds = [12, 42],
            LoadedMediaName = "62mm x 29mm",
            LoadedMediaId = 42,
        };
        var activations = new List<string>();
        using var client = new BrotherBpacComClient(progId =>
        {
            activations.Add(progId);
            return printer;
        });

        var discoveries = client.DiscoverPrinters();

        Assert.Equal([BrotherBpacComClient.PrinterProgId], activations);
        var discovery = Assert.Single(
            discoveries,
            item => item.Name == "Brother QL-820NWB");
        Assert.Equal("Brother QL-820NWB", discovery.Name);
        Assert.True(discovery.IsOnline);
        Assert.True(discovery.MediaDetailsAvailable);
        Assert.Equal(["62mm", "62mm x 29mm"], discovery.SupportedMediaNames);
        Assert.Equal([12, 42], discovery.SupportedMediaIds);
        Assert.Equal("62mm x 29mm", discovery.LoadedMediaName);
        Assert.Equal(42, discovery.LoadedMediaId);
        var unselected = Assert.Single(
            discoveries,
            item => item.Name == "Brother PT-P900W");
        Assert.True(unselected.IsOnline);
        Assert.False(unselected.MediaDetailsAvailable);
        Assert.Empty(unselected.SupportedMediaNames);
        Assert.Empty(unselected.SupportedMediaIds);
        Assert.Null(unselected.LoadedMediaName);
        Assert.Null(unselected.LoadedMediaId);
        Assert.Equal(
            [
                "GetInstalledPrinters",
                "IsPrinterOnline:Brother QL-820NWB",
                "GetSupportedMediaNames",
                "GetSupportedMediaIds",
                "GetMediaName",
                "GetMediaId",
                "IsPrinterOnline:Brother PT-P900W",
            ],
            printer.Calls);
    }

    [Fact]
    public void Document_bridge_maps_template_objects_media_printing_and_errors()
    {
        var documentPrinter = new FakeAutomationPrinter
        {
            ErrorCode = 73,
            ErrorString = "Synthetic printer error",
        };
        var textObject = new FakeAutomationObject();
        var imageObject = new FakeAutomationObject();
        var document = new FakeAutomationDocument(documentPrinter)
        {
            ErrorCode = 29,
        };
        document.Objects.Add("asset_name", textObject);
        document.Objects.Add("qr_image", imageObject);
        var activations = new List<string>();
        using var client = new BrotherBpacComClient(progId =>
        {
            activations.Add(progId);
            return progId switch
            {
                BrotherBpacComClient.DocumentProgId => document,
                BrotherBpacComClient.PrinterProgId => new FakeAutomationPrinter(),
                _ => throw new InvalidOperationException(progId),
            };
        });

        Assert.True(client.Open(@"C:\labels\asset.lbx"));
        Assert.True(client.SetPrinter("Brother QL-820NWB", fitPage: false));
        Assert.True(client.SetMedia(BrotherMediaSelection.ByName("62mm x 29mm"), fitPage: false));
        Assert.True(client.SetMedia(BrotherMediaSelection.ById(42), fitPage: false));
        Assert.True(client.SetObjectText("asset_name", "Bench meter"));
        Assert.True(client.SetObjectImage("qr_image", @"C:\temp\qr.png"));
        Assert.False(client.SetObjectText("missing", "value"));
        Assert.True(client.StartPrint("inventoryzing-test", 268435457));
        Assert.True(client.PrintOut(3, 0));
        Assert.True(client.EndPrint());
        Assert.Equal(29, client.DocumentErrorCode);
        Assert.Equal(73, client.PrinterErrorCode);
        Assert.Equal("Synthetic printer error", client.PrinterErrorMessage);
        Assert.True(client.Close());

        Assert.Equal([BrotherBpacComClient.DocumentProgId], activations);
        Assert.Equal("Bench meter", textObject.Text);
        Assert.Equal((0, @"C:\temp\qr.png", 4), imageObject.SetDataArguments);
        Assert.Equal(
            [
                @"Open:C:\labels\asset.lbx",
                "SetPrinter:Brother QL-820NWB:False",
                "SetMediaByName:62mm x 29mm:False",
                "SetMediaById:42:False",
                "GetObject:asset_name",
                "GetObject:qr_image",
                "GetObject:missing",
                "StartPrint:inventoryzing-test:268435457",
                "PrintOut:3:0",
                "EndPrint",
                "Close",
            ],
            document.Calls);
    }

    [Theory]
    [InlineData(0, PrinterMonitorEventKind.PagePrinted)]
    [InlineData(1, PrinterMonitorEventKind.Offline)]
    [InlineData(2, PrinterMonitorEventKind.Paused)]
    [InlineData(3, PrinterMonitorEventKind.Deleted)]
    [InlineData(4, PrinterMonitorEventKind.Error)]
    [InlineData(5, PrinterMonitorEventKind.PrinterNotFound)]
    [InlineData(73, PrinterMonitorEventKind.Unknown)]
    public async Task Printed_callback_preserves_raw_status_and_translates_event(
        int status,
        PrinterMonitorEventKind expectedKind)
    {
        var document = new FakeAutomationDocument(new FakeAutomationPrinter());
        using var client = new BrotherBpacComClient(_ => document);
        var observationId = Guid.NewGuid();
        using var subscription = client.ArmPrintedEvents(
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)),
            () => observationId);

        document.RaisePrinted(status, new object[] { 17, "raw" });
        var observation = await subscription.WaitAsync(CancellationToken.None);

        Assert.Equal(observationId, observation.ObservationId);
        Assert.Equal(expectedKind, observation.Kind);
        Assert.Equal(status, observation.RawStatus);
        Assert.Equal("17,raw", observation.Value);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero), observation.ObservedAt);
    }

    [Fact]
    public void Printed_callback_is_single_and_cleared_on_dispose()
    {
        var document = new FakeAutomationDocument(new FakeAutomationPrinter());
        using var client = new BrotherBpacComClient(_ => document);
        var subscription = client.ArmPrintedEvents();

        Assert.Throws<InvalidOperationException>(() => client.ArmPrintedEvents());
        subscription.Dispose();

        Assert.Null(document.PrintedCallback);
        Assert.Equal(["SetPrintedCallback:set", "SetPrintedCallback:clear"], document.Calls);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public sealed class FakeAutomationDocument(FakeAutomationPrinter printer)
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, FakeAutomationObject> Objects { get; } = [];
        public FakeAutomationPrinter Printer { get; } = printer;
        public int ErrorCode { get; init; }
        public object? PrintedCallback { get; private set; }

        public bool Open(string templatePath)
        {
            Calls.Add($"Open:{templatePath}");
            return true;
        }

        public bool SetPrinter(string printerName, bool fitPage)
        {
            Calls.Add($"SetPrinter:{printerName}:{fitPage}");
            return true;
        }

        public bool SetPrintedCallback(object? callback)
        {
            PrintedCallback = callback;
            Calls.Add(callback is null ? "SetPrintedCallback:clear" : "SetPrintedCallback:set");
            return true;
        }

        public void RaisePrinted(int status, object? value)
        {
            dynamic callback = PrintedCallback ??
                throw new InvalidOperationException("No callback is registered.");
            callback.PrintedEvent(status, value);
        }

        public bool SetMediaByName(string mediaName, bool fitPage)
        {
            Calls.Add($"SetMediaByName:{mediaName}:{fitPage}");
            return true;
        }

        public bool SetMediaById(int mediaId, bool fitPage)
        {
            Calls.Add($"SetMediaById:{mediaId}:{fitPage}");
            return true;
        }

        public FakeAutomationObject? GetObject(string objectName)
        {
            Calls.Add($"GetObject:{objectName}");
            return Objects.GetValueOrDefault(objectName);
        }

        public bool StartPrint(string documentName, int options)
        {
            Calls.Add($"StartPrint:{documentName}:{options}");
            return true;
        }

        public bool PrintOut(int copies, int options)
        {
            Calls.Add($"PrintOut:{copies}:{options}");
            return true;
        }

        public bool EndPrint()
        {
            Calls.Add("EndPrint");
            return true;
        }

        public bool Close()
        {
            Calls.Add("Close");
            return true;
        }
    }

    public sealed class FakeAutomationObject
    {
        public string? Text { get; set; }
        public (int Kind, string Path, int Parameter)? SetDataArguments { get; private set; }

        public bool SetData(int kind, string path, int parameter)
        {
            SetDataArguments = (kind, path, parameter);
            return true;
        }
    }

    public sealed class FakeAutomationPrinter
    {
        public List<string> Calls { get; } = [];
        public string Name { get; } = "Brother QL-820NWB";
        public string[] InstalledPrinters { get; init; } = [];
        public string[] SupportedMediaNames { get; init; } = [];
        public int[] SupportedMediaIds { get; init; } = [];
        public bool Supported { get; init; } = true;
        public bool Online { get; init; } = true;
        public bool MediaSupported { get; init; } = true;
        public string LoadedMediaName { get; init; } = "62mm x 29mm";
        public int LoadedMediaId { get; init; } = 42;
        public int ErrorCode { get; init; }
        public string? ErrorString { get; init; }

        public object[] GetInstalledPrinters()
        {
            Calls.Add("GetInstalledPrinters");
            return [.. InstalledPrinters];
        }

        public object[] GetSupportedMediaNames()
        {
            Calls.Add("GetSupportedMediaNames");
            return [.. SupportedMediaNames];
        }

        public object[] GetSupportedMediaIds()
        {
            Calls.Add("GetSupportedMediaIds");
            return [.. SupportedMediaIds.Cast<object>()];
        }

        public bool IsPrinterSupported(string printerName)
        {
            Calls.Add($"IsPrinterSupported:{printerName}");
            return Supported;
        }

        public bool IsPrinterOnline(string printerName)
        {
            Calls.Add($"IsPrinterOnline:{printerName}");
            return Online;
        }

        public bool IsMediaNameSupported(string mediaName)
        {
            Calls.Add($"IsMediaNameSupported:{mediaName}");
            return MediaSupported;
        }

        public bool IsMediaIdSupported(int mediaId)
        {
            Calls.Add($"IsMediaIdSupported:{mediaId}");
            return MediaSupported;
        }

        public string GetMediaName()
        {
            Calls.Add("GetMediaName");
            return LoadedMediaName;
        }

        public int GetMediaId()
        {
            Calls.Add("GetMediaId");
            return LoadedMediaId;
        }
    }
}