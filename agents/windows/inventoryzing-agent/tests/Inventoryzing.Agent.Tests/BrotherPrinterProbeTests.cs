using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class BrotherPrinterProbeTests
{
    private static readonly byte[] ArtifactBytes = [0x89, 0x50, 0x4e, 0x47];

    [Fact]
    public async Task Submit_loads_template_populates_objects_and_prints_in_order()
    {
        var requestId = Guid.Parse("f2ca12fc-8a8c-466b-ac7d-079fa58cf01b");
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
        };
        var probe = new BrotherPrinterProbe(
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(
                printProfile,
                @"C:\labels\asset.lbx",
                "qr_image",
                media,
                BrotherCutMode.AutoCut)],
            () => client);
        var request = new PrintProbeRequest(
            requestId,
            Artifact(),
            printProfile,
            2,
            [new PrintTemplateField("asset_name", "Bench meter")]);

        var submission = await probe.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Submitted, submission.Status);
        Assert.Null(submission.SpoolJobId);
        Assert.Equal(ArtifactBytes, client.CapturedArtifact);
        Assert.NotNull(client.ArtifactPath);
        Assert.False(File.Exists(client.ArtifactPath));
        Assert.Equal(
            [
                "Availability:Brother QL-820NWB",
                @"Open:C:\labels\asset.lbx",
                "SetPrinter:Brother QL-820NWB:False",
                "Status:name:62mm x 29mm",
                "SetMedia:name:62mm x 29mm:False",
                "SetText:asset_name:Bench meter",
                "SetImage:qr_image",
                "StartPrint:inventoryzing-f2ca12fc8a8c466bac7d079fa58cf01b:1",
                "PrintOut:2:0",
                "EndPrint",
                "Close",
                "Dispose",
            ],
            client.Calls);
    }

    [Fact]
    public async Task Capabilities_verify_printer_and_media_without_requiring_loaded_media()
    {
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: "Different media"),
        };
        var mono = Profile(OutputPalette.Monochrome);
        var blackRed = new PrintProfile(
            "brother-62x29-black-red",
            OutputPalette.BlackRed,
            300,
            300,
            62,
            29);
        var probe = new BrotherPrinterProbe(
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [
                new BrotherBpacProfile(
                    mono,
                    "mono.lbx",
                    "qr_image",
                    BrotherMediaSelection.ByName("62mm x 29mm"),
                    BrotherCutMode.AutoCut),
                new BrotherBpacProfile(
                    blackRed,
                    "black-red.lbx",
                    "qr_image",
                    BrotherMediaSelection.ById(42),
                    BrotherCutMode.AutoCut),
            ],
            () => client);

        var capabilities = await probe.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Equal("brother:ql-820nwb", capabilities.DeviceId);
        Assert.True(capabilities.Supports(OutputPalette.Monochrome));
        Assert.True(capabilities.Supports(OutputPalette.BlackRed));
        Assert.False(capabilities.Supports(OutputPalette.FullColor));
        Assert.Equal(
            [
                "Availability:Brother QL-820NWB",
                "Open:mono.lbx",
                "SetPrinter:Brother QL-820NWB:False",
                "Status:name:62mm x 29mm",
                "Close",
                "Open:black-red.lbx",
                "SetPrinter:Brother QL-820NWB:False",
                "Status:id:42",
                "Close",
                "Dispose",
            ],
            client.Calls);
    }

    [Fact]
    public async Task Status_reports_loaded_media_profile_compatibility_and_unknown_completion()
    {
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var profile = Profile(OutputPalette.Monochrome);
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
        };
        var probe = CreateProbe(client, profile, media);

        var observation = await probe.ObserveStatusAsync(CancellationToken.None);

        Assert.Equal("brother:ql-820nwb", observation.DeviceId);
        Assert.Equal("Brother QL-820NWB", observation.PrinterName);
        Assert.True(observation.IsSupported);
        Assert.True(observation.IsOnline);
        Assert.Equal("62mm x 29mm", observation.LoadedMediaName);
        Assert.Equal(PrinterMonitorCompletionCapability.Unknown, observation.CompletionCapability);
        Assert.Equal(PrinterProfileReadiness.Ready, Assert.Single(observation.Profiles).Readiness);
        Assert.Equal(
            [
                "Availability:Brother QL-820NWB",
                "Open:asset.lbx",
                "SetPrinter:Brother QL-820NWB:False",
                "Status:name:62mm x 29mm",
                "Close",
                "Dispose",
            ],
            client.Calls);
    }

    [Fact]
    public async Task Status_reports_loaded_media_mismatch_without_claiming_readiness()
    {
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var profile = Profile(OutputPalette.Monochrome);
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: "62mm continuous"),
        };

        var observation = await CreateProbe(client, profile, media)
            .ObserveStatusAsync(CancellationToken.None);

        var status = Assert.Single(observation.Profiles);
        Assert.Equal(PrinterProfileReadiness.MediaMismatch, status.Readiness);
        Assert.Contains("does not match", status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OutputPalette.Monochrome, BrotherCutMode.DriverDefault, 0)]
    [InlineData(OutputPalette.Monochrome, BrotherCutMode.AutoCut, 1)]
    [InlineData(OutputPalette.Monochrome, BrotherCutMode.NoCut, 268435456)]
    [InlineData(OutputPalette.BlackRed, BrotherCutMode.DriverDefault, 8)]
    [InlineData(OutputPalette.BlackRed, BrotherCutMode.AutoCut, 9)]
    [InlineData(OutputPalette.BlackRed, BrotherCutMode.NoCut, 268435464)]
    public async Task Palette_and_cut_mode_use_verified_bpac_flags(
        OutputPalette palette,
        BrotherCutMode cutMode,
        int expectedOptions)
    {
        var printProfile = Profile(palette);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
        };
        var probe = CreateProbe(client, printProfile, media, cutMode);

        var submission = await probe.SubmitAsync(
            Request(printProfile),
            CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Submitted, submission.Status);
        Assert.Equal(expectedOptions, client.StartOptions);
        Assert.Equal(BrotherPrinterProbe.DefaultOption, client.PrintOutOptions);
    }

    [Fact]
    public async Task Media_id_is_checked_and_applied_without_fitting_the_template()
    {
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ById(42);
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaId: 42),
        };
        var probe = CreateProbe(client, printProfile, media);

        var submission = await probe.SubmitAsync(Request(printProfile), CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Submitted, submission.Status);
        Assert.Contains("Status:id:42", client.Calls);
        Assert.Contains("SetMedia:id:42:False", client.Calls);
    }

    [Theory]
    [InlineData(ReadinessFailure.UnsupportedPrinter)]
    [InlineData(ReadinessFailure.Offline)]
    [InlineData(ReadinessFailure.PrinterError)]
    [InlineData(ReadinessFailure.UnsupportedMedia)]
    [InlineData(ReadinessFailure.WrongMedia)]
    public async Task Readiness_failure_rejects_before_opening_template(ReadinessFailure failure)
    {
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Availability = failure switch
            {
                ReadinessFailure.UnsupportedPrinter =>
                    new(false, false, 0, null),
                ReadinessFailure.Offline =>
                    new(true, false, 0, null),
                ReadinessFailure.PrinterError =>
                    new(true, true, 71, "Cover open"),
                _ => ReadyAvailability(),
            },
            Status = failure switch
            {
                ReadinessFailure.UnsupportedMedia =>
                    new(true, true, false, media.Name, null, 0, null),
                ReadinessFailure.WrongMedia =>
                    ReadyStatus(loadedMediaName: "29mm x 90mm", loadedMediaId: 17),
                _ => ReadyStatus(loadedMediaName: media.Name),
            },
        };
        var probe = CreateProbe(client, printProfile, media);

        var submission = await probe.SubmitAsync(Request(printProfile), CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Rejected, submission.Status);
        if (failure == ReadinessFailure.WrongMedia)
        {
            Assert.Contains("name '29mm x 90mm' (ID 17)", submission.Detail, StringComparison.Ordinal);
        }
        var rejectsBeforeOpen = failure is
            ReadinessFailure.UnsupportedPrinter or
            ReadinessFailure.Offline or
            ReadinessFailure.PrinterError;
        Assert.Equal(
            rejectsBeforeOpen,
            !client.Calls.Any(call => call.StartsWith("Open:", StringComparison.Ordinal)));
        Assert.Equal("Dispose", client.Calls[^1]);
    }

    [Theory]
    [InlineData("Open", PrintSubmissionStatus.Rejected, false, 0)]
    [InlineData("SetPrinter", PrintSubmissionStatus.Rejected, true, 0)]
    [InlineData("SetMedia", PrintSubmissionStatus.Rejected, true, 0)]
    [InlineData("SetObjectText", PrintSubmissionStatus.Rejected, true, 0)]
    [InlineData("SetObjectImage", PrintSubmissionStatus.Rejected, true, 0)]
    [InlineData("StartPrint", PrintSubmissionStatus.Rejected, true, 0)]
    [InlineData("PrintOut", PrintSubmissionStatus.Unknown, true, 1)]
    [InlineData("EndPrint", PrintSubmissionStatus.Unknown, true, 1)]
    public async Task Lifecycle_failure_reports_safe_status_and_cleans_up(
        string failedOperation,
        PrintSubmissionStatus expectedStatus,
        bool expectsClose,
        int expectedEndPrintCalls)
    {
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
            FailedOperation = failedOperation,
            DocumentErrorCode = 19,
            PrinterErrorCode = 7,
            PrinterErrorMessage = "Synthetic failure",
        };
        var probe = CreateProbe(client, printProfile, media);
        var request = Request(
            printProfile,
            [new PrintTemplateField("asset_name", "Bench meter")]);

        var submission = await probe.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(expectedStatus, submission.Status);
        Assert.Contains(failedOperation, submission.Detail, StringComparison.Ordinal);
        Assert.Equal(expectsClose, client.Calls.Contains("Close"));
        Assert.Equal(expectedEndPrintCalls, client.Calls.Count(call => call == "EndPrint"));
        Assert.Equal("Dispose", client.Calls[^1]);
        if (client.ArtifactPath is not null)
        {
            Assert.False(File.Exists(client.ArtifactPath));
        }
    }

    [Fact]
    public async Task Close_failure_changes_an_accepted_submission_to_unknown()
    {
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
            FailedOperation = "Close",
        };
        var probe = CreateProbe(client, printProfile, media);

        var submission = await probe.SubmitAsync(Request(printProfile), CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Unknown, submission.Status);
        Assert.Contains("Close returned failure", submission.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Printer_error_after_end_print_never_reports_submitted()
    {
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
            PrinterErrorCode = 88,
            PrinterErrorMessage = "Tape empty",
        };
        var probe = CreateProbe(client, printProfile, media);

        var submission = await probe.SubmitAsync(Request(printProfile), CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Unknown, submission.Status);
        Assert.Contains("printer error 88: Tape empty", submission.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_after_print_out_returns_unknown_and_ends_the_job()
    {
        using var cancellation = new CancellationTokenSource();
        var printProfile = Profile(OutputPalette.Monochrome);
        var media = BrotherMediaSelection.ByName("62mm x 29mm");
        var client = new FakeBpacClient
        {
            Status = ReadyStatus(loadedMediaName: media.Name),
            AfterPrintOut = cancellation.Cancel,
        };
        var probe = CreateProbe(client, printProfile, media);

        var submission = await probe.SubmitAsync(Request(printProfile), cancellation.Token);

        Assert.Equal(PrintSubmissionStatus.Unknown, submission.Status);
        Assert.Contains("EndPrint", client.Calls);
        Assert.Contains("Close", client.Calls);
    }

    [Fact]
    public async Task Precancelled_submission_does_not_create_a_bpac_client()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var printProfile = Profile(OutputPalette.Monochrome);
        var clientCreated = false;
        var probe = new BrotherPrinterProbe(
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(
                printProfile,
                "asset.lbx",
                "qr_image",
                BrotherMediaSelection.ByName("62mm x 29mm"),
                BrotherCutMode.AutoCut)],
            () =>
            {
                clientCreated = true;
                return new FakeBpacClient();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.SubmitAsync(Request(printProfile), cancellation.Token).AsTask());

        Assert.False(clientCreated);
    }

    [Fact]
    public async Task Unsupported_artifact_type_is_rejected_without_bpac_activation()
    {
        var printProfile = Profile(OutputPalette.Monochrome);
        var clientCreated = false;
        var probe = new BrotherPrinterProbe(
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(
                printProfile,
                "asset.lbx",
                "qr_image",
                BrotherMediaSelection.ByName("62mm x 29mm"),
                BrotherCutMode.AutoCut)],
            () =>
            {
                clientCreated = true;
                return new FakeBpacClient();
            });
        var artifact = new PrintArtifact(
            "application/pdf",
            62,
            29,
            ArtifactColorSpace.Srgb,
            ArtifactBytes);

        var submission = await probe.SubmitAsync(
            new PrintProbeRequest(Guid.NewGuid(), artifact, printProfile, 1),
            CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Rejected, submission.Status);
        Assert.False(clientCreated);
    }

    private static PrintArtifact Artifact() =>
        new("image/png", 62, 29, ArtifactColorSpace.Srgb, ArtifactBytes);

    private static PrintProfile Profile(OutputPalette palette) =>
        new("brother-62x29", palette, 300, 300, 62, 29);

    private static BrotherBpacPrinterStatus ReadyStatus(
        string? loadedMediaName = null,
        int? loadedMediaId = null) =>
        new(true, true, true, loadedMediaName, loadedMediaId, 0, null);

    private static BrotherBpacPrinterAvailability ReadyAvailability() =>
        new(true, true, 0, null);

    private static BrotherPrinterProbe CreateProbe(
        FakeBpacClient client,
        PrintProfile printProfile,
        BrotherMediaSelection media,
        BrotherCutMode cutMode = BrotherCutMode.AutoCut) =>
        new(
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(
                printProfile,
                "asset.lbx",
                "qr_image",
                media,
                cutMode)],
            () => client);

    private static PrintProbeRequest Request(
        PrintProfile printProfile,
        IEnumerable<PrintTemplateField>? fields = null) =>
        new(Guid.NewGuid(), Artifact(), printProfile, 3, fields);

    public enum ReadinessFailure
    {
        UnsupportedPrinter,
        Offline,
        PrinterError,
        UnsupportedMedia,
        WrongMedia,
    }

    private sealed class FakeBpacClient : IBrotherBpacClient
    {
        public List<string> Calls { get; } = [];
        public BrotherBpacPrinterAvailability Availability { get; set; } = ReadyAvailability();
        public BrotherBpacPrinterStatus Status { get; set; } = ReadyStatus();
        public byte[]? CapturedArtifact { get; private set; }
        public string? ArtifactPath { get; private set; }
        public int? StartOptions { get; private set; }
        public int? PrintOutOptions { get; private set; }
        public string? FailedOperation { get; init; }
        public Action? AfterPrintOut { get; init; }
        public int DocumentErrorCode { get; set; }
        public int PrinterErrorCode { get; set; }
        public string? PrinterErrorMessage { get; set; }

        public IReadOnlyList<BrotherBpacPrinterDiscovery> DiscoverPrinters() => [];

        public BrotherBpacPrinterAvailability GetPrinterAvailability(string printerName)
        {
            Calls.Add($"Availability:{printerName}");
            return Availability;
        }

        public BrotherBpacPrinterStatus GetSelectedPrinterStatus(BrotherMediaSelection media)
        {
            Calls.Add($"Status:{Describe(media)}");
            return Status;
        }

        public bool Open(string templatePath)
        {
            Calls.Add($"Open:{templatePath}");
            return Succeeds("Open");
        }

        public bool SetPrinter(string printerName, bool fitPage)
        {
            Calls.Add($"SetPrinter:{printerName}:{fitPage}");
            return Succeeds("SetPrinter");
        }

        public bool SetMedia(BrotherMediaSelection media, bool fitPage)
        {
            Calls.Add($"SetMedia:{Describe(media)}:{fitPage}");
            return Succeeds("SetMedia");
        }

        public bool SetObjectText(string objectName, string value)
        {
            Calls.Add($"SetText:{objectName}:{value}");
            return Succeeds("SetObjectText");
        }

        public bool SetObjectImage(string objectName, string imagePath)
        {
            Calls.Add($"SetImage:{objectName}");
            ArtifactPath = imagePath;
            CapturedArtifact = File.ReadAllBytes(imagePath);
            return Succeeds("SetObjectImage");
        }

        public bool StartPrint(string documentName, int options)
        {
            Calls.Add($"StartPrint:{documentName}:{options}");
            StartOptions = options;
            return Succeeds("StartPrint");
        }

        public bool PrintOut(int copies, int options)
        {
            Calls.Add($"PrintOut:{copies}:{options}");
            PrintOutOptions = options;
            AfterPrintOut?.Invoke();
            return Succeeds("PrintOut");
        }

        public bool EndPrint()
        {
            Calls.Add("EndPrint");
            return Succeeds("EndPrint");
        }

        public bool Close()
        {
            Calls.Add("Close");
            return Succeeds("Close");
        }

        public void Dispose() => Calls.Add("Dispose");

        private static string Describe(BrotherMediaSelection media) =>
            media.Name is not null ? $"name:{media.Name}" : $"id:{media.Id}";

        private bool Succeeds(string operation) => FailedOperation != operation;
    }
}