using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Brother;

public sealed class BrotherBpacProfile
{
    public BrotherBpacProfile(
        PrintProfile profile,
        string templatePath,
        string artifactObjectName,
        BrotherMediaSelection media,
        BrotherCutMode cutMode)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactObjectName);
        ArgumentNullException.ThrowIfNull(media);
        if (profile.Palette == OutputPalette.FullColor)
        {
            throw new ArgumentException(
                "The Brother b-PAC adapter does not map full-color output to a limited printer palette.",
                nameof(profile));
        }

        Profile = profile;
        TemplatePath = templatePath;
        ArtifactObjectName = artifactObjectName;
        Media = media;
        CutMode = cutMode;
    }

    public PrintProfile Profile { get; }
    public string TemplatePath { get; }
    public string ArtifactObjectName { get; }
    public BrotherMediaSelection Media { get; }
    public BrotherCutMode CutMode { get; }
}

public sealed class BrotherPrinterProbe : IPrinterProbe, IPrinterStatusProbe
{
    internal const int DefaultOption = 0;
    internal const int AutoCutOption = 0x00000001;
    internal const int ColorOption = 0x00000008;
    internal const int MonoOrNoCutOption = 0x10000000;

    private readonly string printerName;
    private readonly string deviceId;
    private readonly IReadOnlyDictionary<string, BrotherBpacProfile> profiles;
    private readonly PrinterCapabilities capabilities;
    private readonly Func<IBrotherBpacClient> clientFactory;

    public BrotherPrinterProbe(
        string deviceId,
        string printerName,
        IEnumerable<BrotherBpacProfile> profiles)
        : this(deviceId, printerName, profiles, static () => new BrotherBpacComClient())
    {
    }

    public BrotherPrinterProbe(
        string deviceId,
        string printerName,
        IEnumerable<BrotherBpacProfile> profiles,
        Func<IBrotherBpacClient> clientFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(clientFactory);

        var profileSnapshot = profiles.ToArray();
        if (profileSnapshot.Length == 0)
        {
            throw new ArgumentException("At least one Brother print profile is required.", nameof(profiles));
        }

        var duplicateProfile = profileSnapshot
            .GroupBy(profile => profile.Profile.ProfileId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProfile is not null)
        {
            throw new ArgumentException(
                $"Brother print profile ID '{duplicateProfile.Key}' is duplicated.",
                nameof(profiles));
        }

        this.deviceId = deviceId;
        this.printerName = printerName;
        this.profiles = profileSnapshot.ToDictionary(
            profile => profile.Profile.ProfileId,
            StringComparer.Ordinal);
        capabilities = new PrinterCapabilities(
            deviceId,
            profileSnapshot.Select(profile => profile.Profile.Palette));
        this.clientFactory = clientFactory;
    }

    public ValueTask<PrinterCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var client = CreateClient();
        return ValueTask.FromResult(GetCapabilities(client, cancellationToken));
    }

    internal PrinterCapabilities GetCapabilities(
        IBrotherBpacClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();
        var availability = client.GetPrinterAvailability(printerName);
        var availabilityIssue = ValidatePrinterAvailability(availability);
        if (availabilityIssue is not null)
        {
            throw new InvalidOperationException(availabilityIssue);
        }

        foreach (var profile in profiles.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!client.Open(profile.TemplatePath))
            {
                throw new InvalidOperationException("b-PAC failed to open the configured template.");
            }

            if (!client.SetPrinter(printerName, fitPage: false))
            {
                throw new InvalidOperationException("b-PAC failed to select the configured printer.");
            }

            var status = client.GetSelectedPrinterStatus(profile.Media);
            var issue = ValidatePrinterStatus(status, profile.Media, requireLoadedMedia: false);
            if (issue is not null)
            {
                throw new InvalidOperationException(issue);
            }

            if (!client.Close())
            {
                throw new InvalidOperationException("b-PAC failed to close the configured template.");
            }
        }

        return capabilities;
    }

    public ValueTask<PrinterStatusObservation> ObserveStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var client = CreateClient();
        return ValueTask.FromResult(ObserveStatus(
            client,
            BrotherBpacModelCapabilities.GetCompletionCapability(printerName),
            TimeProvider.System,
            Guid.NewGuid,
            cancellationToken));
    }

    internal PrinterStatusObservation ObserveStatus(
        IBrotherBpacClient client,
        PrinterMonitorCompletionCapability completionCapability,
        TimeProvider timeProvider,
        Func<Guid> createId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(createId);
        cancellationToken.ThrowIfCancellationRequested();
        var availability = client.GetPrinterAvailability(printerName);
        List<PrinterProfileStatus> statuses = [];
        string? loadedMediaName = null;
        int? loadedMediaId = null;
        var rawStatus = availability.ErrorCode;
        foreach (var profile in profiles.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!availability.IsSupported || !availability.IsOnline || availability.ErrorCode != 0)
            {
                statuses.Add(new PrinterProfileStatus(
                    profile.Profile.ProfileId,
                    AvailabilityReadiness(availability),
                    profile.Media.ToString(),
                    null,
                    null,
                    availability.ErrorCode,
                    ValidatePrinterAvailability(availability)));
                continue;
            }

            statuses.Add(ObserveProfileStatus(
                client,
                profile,
                printerName,
                ref loadedMediaName,
                ref loadedMediaId,
                ref rawStatus));
        }

        var observationId = createId();
        if (observationId == Guid.Empty)
        {
            throw new InvalidOperationException("The printer observation ID source returned an empty ID.");
        }

        return new PrinterStatusObservation(
            observationId,
            deviceId,
            printerName,
            availability.IsSupported,
            availability.IsOnline,
            loadedMediaName,
            loadedMediaId,
            rawStatus,
            availability.ErrorMessage,
            completionCapability,
            statuses,
            timeProvider.GetUtcNow());
    }

    public ValueTask<PrintSubmission> SubmitAsync(
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(SubmitCore(
            request,
            CreateClient,
            disposeClient: true,
            cancellationToken));
    }

    internal PrintSubmission Submit(
        IBrotherBpacClient client,
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        return SubmitCore(request, () => client, disposeClient: false, cancellationToken);
    }

    internal PrintSubmission Submit(
        Func<IBrotherBpacClient> acquireClient,
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acquireClient);
        return SubmitCore(request, acquireClient, disposeClient: false, cancellationToken);
    }

    private PrintSubmission SubmitCore(
        PrintProbeRequest request,
        Func<IBrotherBpacClient> acquireClient,
        bool disposeClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!profiles.TryGetValue(request.Profile.ProfileId, out var profile))
        {
            return Rejected(
                $"Brother print profile '{request.Profile.ProfileId}' is not configured.");
        }

        if (!ProfilesMatch(request.Profile, profile.Profile))
        {
            return Rejected(
                $"Print profile '{request.Profile.ProfileId}' does not match its Brother configuration.");
        }

        var preflightIssues = PrintPreflight.Validate(request.Artifact, request.Profile, capabilities);
        if (preflightIssues.Count > 0)
        {
            return Rejected(string.Join(
                " ",
                preflightIssues.Select(issue => $"{issue.Code}: {issue.Message}")));
        }

        if (request.Fields.Any(field =>
            string.Equals(field.Name, profile.ArtifactObjectName, StringComparison.Ordinal)))
        {
            return Rejected(
                $"Template object '{profile.ArtifactObjectName}' is reserved for the print artifact.");
        }

        if (!TryGetArtifactExtension(request.Artifact.MediaType, out var artifactExtension))
        {
            return Rejected(
                $"Brother b-PAC does not support artifact media type '{request.Artifact.MediaType}'.");
        }

        return SubmitPrepared(
            request,
            profile,
            artifactExtension,
            acquireClient,
            disposeClient,
            cancellationToken);
    }

    private PrintSubmission SubmitPrepared(
        PrintProbeRequest request,
        BrotherBpacProfile profile,
        string artifactExtension,
        Func<IBrotherBpacClient> acquireClient,
        bool disposeClient,
        CancellationToken cancellationToken)
    {
        IBrotherBpacClient? client = null;
        string? artifactPath = null;
        var state = new PrintOperationState();
        List<string> cleanupFailures = [];
        var submission = Rejected("Brother b-PAC submission did not begin.");

        try
        {
            client = acquireClient() ??
                throw new InvalidOperationException("The b-PAC client acquisition returned null.");
            submission = Execute(
                client,
                request,
                profile,
                artifactExtension,
                state,
                ref artifactPath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (state.PrintMayHaveBeenSubmitted)
        {
            submission = Unknown("Cancellation was requested after b-PAC may have accepted the print job.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            submission = state.PrintMayHaveBeenSubmitted
                ? Unknown($"Brother b-PAC failed after submission may have begun: {exception.Message}")
                : Rejected($"Brother b-PAC failed before submission: {exception.Message}");
        }
        finally
        {
            if (client is not null)
            {
                CleanupClient(client, state, disposeClient, cleanupFailures);
            }

            DeleteArtifact(artifactPath, cleanupFailures);
        }

        if (cleanupFailures.Count == 0)
        {
            return submission;
        }

        var status = state.PrintMayHaveBeenSubmitted || submission.Status == PrintSubmissionStatus.Submitted
            ? PrintSubmissionStatus.Unknown
            : submission.Status;
        return new PrintSubmission(
            status,
            null,
            $"{submission.Detail} Cleanup failed: {string.Join(" ", cleanupFailures)}");
    }

    private PrintSubmission Execute(
        IBrotherBpacClient client,
        PrintProbeRequest request,
        BrotherBpacProfile profile,
        string artifactExtension,
        PrintOperationState state,
        ref string? artifactPath,
        CancellationToken cancellationToken)
    {
        var availability = client.GetPrinterAvailability(printerName);
        var availabilityIssue = ValidatePrinterAvailability(availability);
        if (availabilityIssue is not null)
        {
            return Rejected(availabilityIssue);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!client.Open(profile.TemplatePath))
        {
            return OperationFailure(client, "Open", PrintSubmissionStatus.Rejected);
        }

        state.DocumentOpened = true;
        if (!client.SetPrinter(printerName, fitPage: false))
        {
            return OperationFailure(client, "SetPrinter", PrintSubmissionStatus.Rejected);
        }

        var status = client.GetSelectedPrinterStatus(profile.Media);
        var statusIssue = ValidatePrinterStatus(status, profile.Media, requireLoadedMedia: true);
        if (statusIssue is not null)
        {
            return Rejected(statusIssue);
        }

        if (!client.SetMedia(profile.Media, fitPage: false))
        {
            return OperationFailure(client, "SetMedia", PrintSubmissionStatus.Rejected);
        }

        foreach (var field in request.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!client.SetObjectText(field.Name, field.Value))
            {
                return OperationFailure(
                    client,
                    $"SetObjectText('{field.Name}')",
                    PrintSubmissionStatus.Rejected);
            }
        }

        artifactPath = Path.Combine(
            Path.GetTempPath(),
            $"inventoryzing-{request.RequestId:N}-{Path.GetRandomFileName()}{artifactExtension}");
        File.WriteAllBytes(artifactPath, request.Artifact.CopyContent());
        if (!client.SetObjectImage(profile.ArtifactObjectName, artifactPath))
        {
            return OperationFailure(
                client,
                $"SetObjectImage('{profile.ArtifactObjectName}')",
                PrintSubmissionStatus.Rejected);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = BuildPrintOptions(profile);
        if (!client.StartPrint($"inventoryzing-{request.RequestId:N}", options))
        {
            return OperationFailure(client, "StartPrint", PrintSubmissionStatus.Rejected);
        }

        state.PrintNeedsEnding = true;
        cancellationToken.ThrowIfCancellationRequested();
        state.PrintMayHaveBeenSubmitted = true;
        if (!client.PrintOut(request.Copies, DefaultOption))
        {
            return OperationFailure(client, "PrintOut", PrintSubmissionStatus.Unknown);
        }

        cancellationToken.ThrowIfCancellationRequested();
        state.PrintNeedsEnding = false;
        if (!client.EndPrint())
        {
            return OperationFailure(client, "EndPrint", PrintSubmissionStatus.Unknown);
        }

        if (client.DocumentErrorCode != 0 || client.PrinterErrorCode != 0)
        {
            return OperationFailure(client, "Print status", PrintSubmissionStatus.Unknown);
        }

        return new PrintSubmission(
            PrintSubmissionStatus.Submitted,
            null,
            "b-PAC accepted the print job; physical completion is not asserted.");
    }

    private static void CleanupClient(
        IBrotherBpacClient client,
        PrintOperationState state,
        bool disposeClient,
        List<string> failures)
    {
        if (state.PrintNeedsEnding)
        {
            try
            {
                if (!client.EndPrint())
                {
                    failures.Add("EndPrint returned failure.");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"EndPrint threw: {exception.Message}");
            }
        }

        if (state.DocumentOpened)
        {
            try
            {
                if (!client.Close())
                {
                    failures.Add("Close returned failure.");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"Close threw: {exception.Message}");
            }
        }

        if (!disposeClient)
        {
            return;
        }

        try
        {
            client.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add($"Dispose threw: {exception.Message}");
        }
    }

    private static void DeleteArtifact(string? artifactPath, List<string> failures)
    {
        if (artifactPath is null)
        {
            return;
        }

        try
        {
            File.Delete(artifactPath);
        }
        catch (Exception exception)
        {
            failures.Add($"Temporary artifact deletion threw: {exception.Message}");
        }
    }

    private IBrotherBpacClient CreateClient() =>
        clientFactory() ?? throw new InvalidOperationException("The b-PAC client factory returned null.");

    private static int BuildPrintOptions(BrotherBpacProfile profile)
    {
        var palette = profile.Profile.Palette switch
        {
            OutputPalette.Monochrome => DefaultOption,
            OutputPalette.BlackRed => ColorOption,
            _ => throw new InvalidOperationException(
                $"Brother b-PAC cannot print palette '{profile.Profile.Palette}'."),
        };
        var cut = profile.CutMode switch
        {
            BrotherCutMode.DriverDefault => DefaultOption,
            BrotherCutMode.AutoCut => AutoCutOption,
            BrotherCutMode.NoCut => MonoOrNoCutOption,
            _ => throw new InvalidOperationException($"Unknown Brother cut mode '{profile.CutMode}'."),
        };
        return palette | cut;
    }

    private static string? ValidatePrinterStatus(
        BrotherBpacPrinterStatus status,
        BrotherMediaSelection media,
        bool requireLoadedMedia)
    {
        if (!status.IsSupported)
        {
            return "The configured printer is not supported by b-PAC.";
        }

        if (!status.IsOnline)
        {
            return "The configured printer is offline.";
        }

        if (status.ErrorCode != 0)
        {
            return $"The printer reported error {status.ErrorCode}: {status.ErrorMessage}";
        }

        if (!status.IsMediaSupported)
        {
            return $"The configured printer does not support media {media}.";
        }

        if (requireLoadedMedia && !media.Matches(status.LoadedMediaName, status.LoadedMediaId))
        {
            var loadedMedia = status.LoadedMediaName is not null
                ? $"name '{status.LoadedMediaName}'"
                : "with no reported name";
            loadedMedia += status.LoadedMediaId is not null
                ? $" (ID {status.LoadedMediaId})"
                : " (no reported ID)";
            return $"Loaded media {loadedMedia} does not match configured media {media}.";
        }

        return null;
    }

    private static PrinterProfileStatus ObserveProfileStatus(
        IBrotherBpacClient client,
        BrotherBpacProfile profile,
        string printerName,
        ref string? loadedMediaName,
        ref int? loadedMediaId,
        ref int rawStatus)
    {
        var opened = false;
        PrinterProfileStatus status;
        try
        {
            if (!client.Open(profile.TemplatePath))
            {
                status = ProfileProbeError(client, profile, "b-PAC failed to open the template.");
            }
            else
            {
                opened = true;
                if (!client.SetPrinter(printerName, fitPage: false))
                {
                    status = ProfileProbeError(
                        client,
                        profile,
                        "b-PAC failed to select the printer.");
                }
                else
                {
                    var printerStatus = client.GetSelectedPrinterStatus(profile.Media);
                    loadedMediaName ??= printerStatus.LoadedMediaName;
                    loadedMediaId ??= printerStatus.LoadedMediaId;
                    rawStatus = printerStatus.ErrorCode;
                    var readiness = StatusReadiness(printerStatus, profile.Media);
                    status = new PrinterProfileStatus(
                        profile.Profile.ProfileId,
                        readiness,
                        profile.Media.ToString(),
                        printerStatus.LoadedMediaName,
                        printerStatus.LoadedMediaId,
                        printerStatus.ErrorCode,
                        ValidatePrinterStatus(
                            printerStatus,
                            profile.Media,
                            requireLoadedMedia: true));
                }
            }
        }
        catch (Exception exception)
        {
            status = ProfileProbeError(client, profile, exception.Message);
        }
        finally
        {
            if (opened)
            {
                try
                {
                    if (!client.Close())
                    {
                        status = ProfileProbeError(
                            client,
                            profile,
                            "b-PAC failed to close the template.");
                    }
                }
                catch (Exception exception)
                {
                    status = ProfileProbeError(client, profile, exception.Message);
                }
            }
        }

        return status;
    }

    private static PrinterProfileReadiness AvailabilityReadiness(
        BrotherBpacPrinterAvailability availability)
    {
        if (!availability.IsSupported)
        {
            return PrinterProfileReadiness.PrinterUnsupported;
        }
        if (!availability.IsOnline)
        {
            return PrinterProfileReadiness.Offline;
        }
        return availability.ErrorCode != 0
            ? PrinterProfileReadiness.PrinterError
            : PrinterProfileReadiness.Ready;
    }

    private static PrinterProfileReadiness StatusReadiness(
        BrotherBpacPrinterStatus status,
        BrotherMediaSelection media)
    {
        if (!status.IsSupported)
        {
            return PrinterProfileReadiness.PrinterUnsupported;
        }
        if (!status.IsOnline)
        {
            return PrinterProfileReadiness.Offline;
        }
        if (status.ErrorCode != 0)
        {
            return PrinterProfileReadiness.PrinterError;
        }
        if (!status.IsMediaSupported)
        {
            return PrinterProfileReadiness.MediaUnsupported;
        }
        return media.Matches(status.LoadedMediaName, status.LoadedMediaId)
            ? PrinterProfileReadiness.Ready
            : PrinterProfileReadiness.MediaMismatch;
    }

    private static PrinterProfileStatus ProfileProbeError(
        IBrotherBpacClient client,
        BrotherBpacProfile profile,
        string detail) =>
        new(
            profile.Profile.ProfileId,
            PrinterProfileReadiness.ProbeError,
            profile.Media.ToString(),
            null,
            null,
            client.PrinterErrorCode,
            detail);

    private static string? ValidatePrinterAvailability(BrotherBpacPrinterAvailability availability)
    {
        if (!availability.IsSupported)
        {
            return "The configured printer is not supported by b-PAC.";
        }

        if (!availability.IsOnline)
        {
            return "The configured printer is offline.";
        }

        if (availability.ErrorCode != 0)
        {
            return $"The printer reported error {availability.ErrorCode}: {availability.ErrorMessage}";
        }

        return null;
    }

    private static PrintSubmission OperationFailure(
        IBrotherBpacClient client,
        string operation,
        PrintSubmissionStatus status)
    {
        var detail = $"b-PAC {operation} failed";
        if (client.DocumentErrorCode != 0)
        {
            detail += $"; document error {client.DocumentErrorCode}";
        }

        if (client.PrinterErrorCode != 0)
        {
            detail += $"; printer error {client.PrinterErrorCode}: {client.PrinterErrorMessage}";
        }

        return new PrintSubmission(status, null, detail + ".");
    }

    private static bool ProfilesMatch(PrintProfile requested, PrintProfile configured) =>
        requested.ProfileId == configured.ProfileId &&
        requested.Palette == configured.Palette &&
        requested.DpiX == configured.DpiX &&
        requested.DpiY == configured.DpiY &&
        requested.WidthMillimeters == configured.WidthMillimeters &&
        requested.HeightMillimeters == configured.HeightMillimeters;

    private static bool TryGetArtifactExtension(string mediaType, out string extension)
    {
        extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/tiff" => ".tiff",
            _ => string.Empty,
        };
        return extension.Length > 0;
    }

    private static PrintSubmission Rejected(string detail) =>
        new(PrintSubmissionStatus.Rejected, null, detail);

    private static PrintSubmission Unknown(string detail) =>
        new(PrintSubmissionStatus.Unknown, null, detail);

    private sealed class PrintOperationState
    {
        public bool DocumentOpened { get; set; }
        public bool PrintNeedsEnding { get; set; }
        public bool PrintMayHaveBeenSubmitted { get; set; }
    }
}