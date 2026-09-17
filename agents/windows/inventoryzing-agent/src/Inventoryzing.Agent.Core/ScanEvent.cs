namespace Inventoryzing.Agent.Core;

public enum ScanSourceType
{
    ZebraSnapi,
}

public sealed record ScanSource(
    string StableId,
    string RuntimeId,
    ScanSourceType Type,
    string? Model,
    string? SerialNumber);

public sealed record ScanEvent(
    Guid EventId,
    string Payload,
    ScanSource Source,
    DateTimeOffset ReceivedAt,
    string? Symbology);