namespace Inventoryzing.Agent.Tests;

public sealed class HardwarePrerequisiteAcceptanceStubs
{
    private const string SkipReason =
        "Requires the read-only Windows prerequisite probe and an approved hardware baseline.";

    [HardwareFact(SkipReason)]
    [Trait("Category", "Hardware")]
    public void Selected_dotnet_runtime_and_process_architecture_match_vendor_adapters() =>
        Assert.Fail("Wire the prerequisite inventory probe before enabling this test.");

    [HardwareFact(SkipReason)]
    [Trait("Category", "Hardware")]
    public void Zebra_sdk_driver_com_registration_and_versions_are_recorded() =>
        Assert.Fail("Persist the verified Zebra dependency baseline before enabling this test.");

    [HardwareFact(SkipReason)]
    [Trait("Category", "Hardware")]
    public void Brother_driver_transport_media_modes_and_versions_are_recorded() =>
        Assert.Fail("Select the Brother transport and capture its capabilities before enabling this test.");
}