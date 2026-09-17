using System.Security.Cryptography;

namespace Inventoryzing.Agent.Core;

public enum ArtifactColorSpace
{
    Srgb,
}

public enum OutputPalette
{
    Monochrome,
    BlackRed,
    FullColor,
}

public sealed class PrintArtifact
{
    private readonly byte[] content;

    public PrintArtifact(
        string mediaType,
        decimal widthMillimeters,
        decimal heightMillimeters,
        ArtifactColorSpace colorSpace,
        ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthMillimeters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimeters);
        if (content.IsEmpty)
        {
            throw new ArgumentException("Artifact content cannot be empty.", nameof(content));
        }

        MediaType = mediaType;
        WidthMillimeters = widthMillimeters;
        HeightMillimeters = heightMillimeters;
        ColorSpace = colorSpace;
        this.content = content.ToArray();
        Sha256 = Convert.ToHexString(SHA256.HashData(this.content)).ToLowerInvariant();
    }

    public string MediaType { get; }
    public decimal WidthMillimeters { get; }
    public decimal HeightMillimeters { get; }
    public ArtifactColorSpace ColorSpace { get; }
    public string Sha256 { get; }
    public int Length => content.Length;

    public byte[] CopyContent() => (byte[])content.Clone();
}

public sealed class PrintProfile
{
    public PrintProfile(
        string profileId,
        OutputPalette palette,
        int dpiX,
        int dpiY,
        decimal widthMillimeters,
        decimal heightMillimeters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthMillimeters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimeters);

        ProfileId = profileId;
        Palette = palette;
        DpiX = dpiX;
        DpiY = dpiY;
        WidthMillimeters = widthMillimeters;
        HeightMillimeters = heightMillimeters;
    }

    public string ProfileId { get; }
    public OutputPalette Palette { get; }
    public int DpiX { get; }
    public int DpiY { get; }
    public decimal WidthMillimeters { get; }
    public decimal HeightMillimeters { get; }
}

public sealed class PrinterCapabilities
{
    private readonly HashSet<OutputPalette> supportedPalettes;

    public PrinterCapabilities(string deviceId, IEnumerable<OutputPalette> supportedPalettes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(supportedPalettes);
        this.supportedPalettes = [.. supportedPalettes];
        if (this.supportedPalettes.Count == 0)
        {
            throw new ArgumentException("At least one output palette is required.", nameof(supportedPalettes));
        }

        DeviceId = deviceId;
    }

    public string DeviceId { get; }

    public bool Supports(OutputPalette palette) => supportedPalettes.Contains(palette);
}

public sealed record PrintPreflightIssue(string Code, string Message);

public static class PrintPreflight
{
    public static IReadOnlyList<PrintPreflightIssue> Validate(
        PrintArtifact artifact,
        PrintProfile profile,
        PrinterCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilities);
        List<PrintPreflightIssue> issues = [];

        if (!capabilities.Supports(profile.Palette))
        {
            issues.Add(new PrintPreflightIssue(
                "palette.unsupported",
                $"Printer '{capabilities.DeviceId}' does not support '{profile.Palette}'."));
        }
        if (artifact.WidthMillimeters != profile.WidthMillimeters ||
            artifact.HeightMillimeters != profile.HeightMillimeters)
        {
            issues.Add(new PrintPreflightIssue(
                "dimensions.mismatch",
                "Artifact dimensions must exactly match the selected print profile."));
        }

        return issues;
    }
}

public sealed record PrintTemplateField
{
    public PrintTemplateField(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

public sealed class PrintProbeRequest
{
    private readonly IReadOnlyList<PrintTemplateField> fields;

    public PrintProbeRequest(
        Guid requestId,
        PrintArtifact artifact,
        PrintProfile profile,
        int copies,
        IEnumerable<PrintTemplateField>? fields = null)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Print request ID cannot be empty.", nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(copies);

        var fieldSnapshot = (fields ?? []).ToArray();
        if (fieldSnapshot.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() !=
            fieldSnapshot.Length)
        {
            throw new ArgumentException("Template field names must be unique.", nameof(fields));
        }

        RequestId = requestId;
        Artifact = artifact;
        Profile = profile;
        Copies = copies;
        this.fields = Array.AsReadOnly(fieldSnapshot);
    }

    public Guid RequestId { get; }
    public PrintArtifact Artifact { get; }
    public PrintProfile Profile { get; }
    public int Copies { get; }
    public IReadOnlyList<PrintTemplateField> Fields => fields;
}

public enum PrintSubmissionStatus
{
    Submitted,
    Rejected,
    Unknown,
}

public sealed record PrintSubmission(PrintSubmissionStatus Status, string? SpoolJobId, string? Detail);

public interface IPrinterProbe
{
    ValueTask<PrinterCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    ValueTask<PrintSubmission> SubmitAsync(
        PrintProbeRequest request,
        CancellationToken cancellationToken);
}