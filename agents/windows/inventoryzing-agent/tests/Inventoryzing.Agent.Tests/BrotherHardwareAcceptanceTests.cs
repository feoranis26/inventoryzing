using System.Globalization;
using System.Text.Json;
using Inventoryzing.Agent.Core;
using Xunit.Abstractions;

namespace Inventoryzing.Agent.Tests;

public sealed class BrotherHardwareAcceptanceTests(ITestOutputHelper output)
{
    public const string DiscoverScenario = "brother-discover";
    public const string MonochromeScenario = "brother-monochrome";

    private const string PrinterEnvironmentVariable = "INVENTORYZING_BROTHER_PRINTER";
    private const string TemplateEnvironmentVariable = "INVENTORYZING_BROTHER_TEMPLATE";
    private const string ArtifactEnvironmentVariable = "INVENTORYZING_BROTHER_ARTIFACT";
    private const string ArtifactObjectEnvironmentVariable = "INVENTORYZING_BROTHER_ARTIFACT_OBJECT";
    private const string MediaNameEnvironmentVariable = "INVENTORYZING_BROTHER_MEDIA_NAME";
    private const string MediaIdEnvironmentVariable = "INVENTORYZING_BROTHER_MEDIA_ID";
    private const string WidthEnvironmentVariable = "INVENTORYZING_BROTHER_WIDTH_MM";
    private const string HeightEnvironmentVariable = "INVENTORYZING_BROTHER_HEIGHT_MM";
    private const string DpiXEnvironmentVariable = "INVENTORYZING_BROTHER_DPI_X";
    private const string DpiYEnvironmentVariable = "INVENTORYZING_BROTHER_DPI_Y";
    private const string CutModeEnvironmentVariable = "INVENTORYZING_BROTHER_CUT_MODE";
    private const string FieldsEnvironmentVariable = "INVENTORYZING_BROTHER_FIELDS_JSON";
    private const string TimeoutEnvironmentVariable = "INVENTORYZING_BROTHER_TIMEOUT_SECONDS";
    private const string SkipReason =
        "Requires explicit Brother hardware authorization and operator-selected media.";

    [HardwareFact(DiscoverScenario, SkipReason)]
    [Trait("Category", "Hardware")]
    public void Discovers_bpac_printers_supported_media_and_loaded_status()
    {
        using var client = new BrotherBpacComClient();

        var discoveries = client.DiscoverPrinters();

        Assert.NotEmpty(discoveries);
        foreach (var discovery in discoveries)
        {
            WriteDiscovery(discovery);
        }

        var expectedPrinter = Environment.GetEnvironmentVariable(PrinterEnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(expectedPrinter))
        {
            return;
        }

        var target = Assert.Single(discoveries, discovery =>
            string.Equals(discovery.Name, expectedPrinter, StringComparison.Ordinal));
        Assert.True(target.IsOnline, $"Printer '{target.Name}' is offline.");
        Assert.Equal(0, target.ErrorCode);
        Assert.True(
            target.MediaDetailsAvailable,
            $"Printer '{target.Name}' is installed but is not the standalone b-PAC printer whose read-only Name supplies media details.");
        Assert.NotEmpty(target.SupportedMediaNames);
        Assert.NotEmpty(target.SupportedMediaIds);
    }

    [HardwareFact(MonochromeScenario, SkipReason)]
    [Trait("Category", "Hardware")]
    public async Task Submits_exactly_one_authorized_monochrome_sample()
    {
        var printerName = RequiredEnvironmentVariable(PrinterEnvironmentVariable);
        var templatePath = RequiredFile(TemplateEnvironmentVariable, [".lbx"]);
        var artifactPath = RequiredFile(
            ArtifactEnvironmentVariable,
            [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"]);
        var artifactObjectName = RequiredEnvironmentVariable(ArtifactObjectEnvironmentVariable);
        var media = ReadMedia();
        var widthMillimeters = ReadPositiveDecimal(WidthEnvironmentVariable);
        var heightMillimeters = ReadPositiveDecimal(HeightEnvironmentVariable);
        var dpiX = ReadPositiveInteger(DpiXEnvironmentVariable, maximum: 2400);
        var dpiY = ReadPositiveInteger(DpiYEnvironmentVariable, maximum: 2400);
        var cutMode = ReadCutMode();
        var fields = ReadFields();
        var profile = new PrintProfile(
            "brother-physical-monochrome",
            OutputPalette.Monochrome,
            dpiX,
            dpiY,
            widthMillimeters,
            heightMillimeters);
        var brotherProfile = new BrotherBpacProfile(
            profile,
            templatePath,
            artifactObjectName,
            media,
            cutMode);
        var artifact = new PrintArtifact(
            ArtifactMediaType(artifactPath),
            widthMillimeters,
            heightMillimeters,
            ArtifactColorSpace.Srgb,
            await File.ReadAllBytesAsync(artifactPath));
        var request = new PrintProbeRequest(Guid.NewGuid(), artifact, profile, copies: 1, fields);
        var probe = new BrotherPrinterProbe(
            $"brother:physical:{printerName}",
            printerName,
            [brotherProfile]);
        using var cancellation = new CancellationTokenSource(ReadTimeout());

        output.WriteLine("Printer: {0}", printerName);
        output.WriteLine("Template: {0}", templatePath);
        output.WriteLine("Artifact: {0} ({1})", artifactPath, artifact.Sha256);
        output.WriteLine(
            "Profile: {0} x {1} mm at {2} x {3} dpi; media {4}; cut {5}; copies 1",
            widthMillimeters,
            heightMillimeters,
            dpiX,
            dpiY,
            media,
            cutMode);

        var submission = await probe.SubmitAsync(request, cancellation.Token);

        output.WriteLine("Submission: {0}; {1}", submission.Status, submission.Detail);
        Assert.Equal(PrintSubmissionStatus.Submitted, submission.Status);
    }

    [HardwareFact(SkipReason)]
    [Trait("Category", "Hardware")]
    public void Black_red_sample_uses_validated_color_planes_and_media_mode() =>
        Assert.Fail("Add a separately authorized black/red scenario after monochrome physical acceptance.");

    [HardwareFact(SkipReason)]
    [Trait("Category", "Hardware")]
    public void Printed_full_uuid_and_local_alias_qr_codes_decode_exactly() =>
        Assert.Fail("Add independently decoded physical samples after the first monochrome print.");

    [HardwareFact(SkipReason)]
    [Trait("Category", "Hardware")]
    public void Driver_or_media_failure_never_reports_physical_success() =>
        Assert.Fail("Add guarded physical failure injection after baseline printing is proven.");

    private void WriteDiscovery(BrotherBpacPrinterDiscovery discovery)
    {
        output.WriteLine(
            "Printer: name={0}, online={1}, mediaDetailsAvailable={2}, loadedMediaName={3}, loadedMediaId={4}, error={5}:{6}",
            discovery.Name,
            discovery.IsOnline,
            discovery.MediaDetailsAvailable,
            discovery.LoadedMediaName ?? "<none>",
            discovery.LoadedMediaId?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
            discovery.ErrorCode,
            discovery.ErrorMessage ?? "<none>");
        if (discovery.MediaDetailsAvailable)
        {
            output.WriteLine(
                "Supported media names: {0}",
                string.Join(", ", discovery.SupportedMediaNames));
            output.WriteLine(
                "Supported media IDs: {0}",
                string.Join(", ", discovery.SupportedMediaIds));
        }
        else
        {
            output.WriteLine("Supported and loaded media: unavailable for this unbound printer.");
        }
    }

    private static BrotherMediaSelection ReadMedia()
    {
        var name = Environment.GetEnvironmentVariable(MediaNameEnvironmentVariable)?.Trim();
        var idValue = Environment.GetEnvironmentVariable(MediaIdEnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(name) == string.IsNullOrEmpty(idValue))
        {
            throw new InvalidOperationException(
                $"Set exactly one of {MediaNameEnvironmentVariable} or {MediaIdEnvironmentVariable}.");
        }

        if (!string.IsNullOrEmpty(name))
        {
            return BrotherMediaSelection.ByName(name);
        }

        return int.TryParse(idValue, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? BrotherMediaSelection.ById(id)
            : throw new InvalidOperationException(
                $"{MediaIdEnvironmentVariable} must be a non-negative integer.");
    }

    private static BrotherCutMode ReadCutMode()
    {
        var value = RequiredEnvironmentVariable(CutModeEnvironmentVariable);
        return Enum.TryParse<BrotherCutMode>(value, ignoreCase: false, out var mode) &&
            Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException(
                $"{CutModeEnvironmentVariable} must be DriverDefault, AutoCut, or NoCut.");
    }

    private static PrintTemplateField[] ReadFields()
    {
        var json = Environment.GetEnvironmentVariable(FieldsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ??
            throw new InvalidOperationException($"{FieldsEnvironmentVariable} must be a JSON object.");
        return [.. values.Select(pair => new PrintTemplateField(pair.Key, pair.Value))];
    }

    private static string ArtifactMediaType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".tif" or ".tiff" => "image/tiff",
            _ => throw new InvalidOperationException($"Unsupported artifact extension '{Path.GetExtension(path)}'."),
        };

    private static string RequiredFile(string variableName, IReadOnlyCollection<string> extensions)
    {
        var path = Path.GetFullPath(RequiredEnvironmentVariable(variableName));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File configured by {variableName} was not found.", path);
        }

        if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{variableName} must reference one of: {string.Join(", ", extensions)}.");
        }

        return path;
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"Set {name} before running this scenario.")
            : value;
    }

    private static decimal ReadPositiveDecimal(string name)
    {
        var value = RequiredEnvironmentVariable(name);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            number > 0
            ? number
            : throw new InvalidOperationException($"{name} must be a positive decimal number.");
    }

    private static int ReadPositiveInteger(string name, int maximum)
    {
        var value = RequiredEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number is > 0 && number <= maximum
            ? number
            : throw new InvalidOperationException($"{name} must be an integer from 1 through {maximum}.");
    }

    private static TimeSpan ReadTimeout()
    {
        const int defaultSeconds = 90;
        var value = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 5 and <= 600
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException(
                $"{TimeoutEnvironmentVariable} must be an integer from 5 through 600.");
    }
}