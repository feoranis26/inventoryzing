using System.Text;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintProbeContractTests
{
    [Fact]
    public void Artifact_snapshots_source_bytes_and_content_hash()
    {
        var source = Encoding.UTF8.GetBytes("full-color-canonical-artifact");
        var artifact = new PrintArtifact("image/png", 62, 29, ArtifactColorSpace.Srgb, source);
        var expected = artifact.CopyContent();
        var expectedHash = artifact.Sha256;

        source[0] = (byte)'X';

        Assert.Equal(expected, artifact.CopyContent());
        Assert.Equal(expectedHash, artifact.Sha256);
        Assert.Equal(29, artifact.Length);
    }

    [Fact]
    public void Black_red_profile_is_accepted_only_when_device_declares_support()
    {
        var artifact = Artifact();
        var profile = new PrintProfile("brother-black-red", OutputPalette.BlackRed, 300, 300, 62, 29);
        var supported = new PrinterCapabilities("brother:ql-820nwb", [OutputPalette.Monochrome, OutputPalette.BlackRed]);
        var monochromeOnly = new PrinterCapabilities("generic:mono", [OutputPalette.Monochrome]);

        Assert.Empty(PrintPreflight.Validate(artifact, profile, supported));
        var issue = Assert.Single(PrintPreflight.Validate(artifact, profile, monochromeOnly));
        Assert.Equal("palette.unsupported", issue.Code);
    }

    [Fact]
    public void Full_color_is_not_silently_reclassified_as_limited_palette()
    {
        var artifact = Artifact();
        var profile = new PrintProfile("future-full-color", OutputPalette.FullColor, 300, 300, 62, 29);
        var capabilities = new PrinterCapabilities(
            "brother:ql-820nwb",
            [OutputPalette.Monochrome, OutputPalette.BlackRed]);

        var issue = Assert.Single(PrintPreflight.Validate(artifact, profile, capabilities));

        Assert.Equal("palette.unsupported", issue.Code);
        Assert.Equal(ArtifactColorSpace.Srgb, artifact.ColorSpace);
    }

    [Fact]
    public void Physical_dimensions_must_match_selected_profile_exactly()
    {
        var artifact = Artifact();
        var profile = new PrintProfile("wrong-size", OutputPalette.Monochrome, 300, 300, 62, 30);
        var capabilities = new PrinterCapabilities("brother:ql-820nwb", [OutputPalette.Monochrome]);

        var issue = Assert.Single(PrintPreflight.Validate(artifact, profile, capabilities));

        Assert.Equal("dimensions.mismatch", issue.Code);
    }

    [Fact]
    public void Request_snapshots_named_template_fields()
    {
        var source = new List<PrintTemplateField>
        {
            new("asset_name", "Bench meter"),
        };
        var request = new PrintProbeRequest(
            Guid.NewGuid(),
            Artifact(),
            new PrintProfile("brother-62mm", OutputPalette.Monochrome, 300, 300, 62, 29),
            1,
            source);

        source.Clear();

        var field = Assert.Single(request.Fields);
        Assert.Equal("asset_name", field.Name);
        Assert.Equal("Bench meter", field.Value);
    }

    private static PrintArtifact Artifact() =>
        new("image/png", 62, 29, ArtifactColorSpace.Srgb, Encoding.UTF8.GetBytes("fixture"));
}