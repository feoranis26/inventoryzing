namespace Inventoryzing.Agent.Brother;

public enum BrotherRasterState
{
    Ready,
    Printing,
    Completed,
    Blocked,
    Invalid,
}

public sealed record BrotherRasterStatus(
    byte ModelCode,
    byte ErrorInformation1,
    byte ErrorInformation2,
    byte MediaWidthMillimeters,
    byte MediaType,
    byte MediaLengthMillimeters,
    byte StatusType,
    byte PhaseType,
    byte NotificationNumber)
{
    public bool HasError => ErrorInformation1 != 0 || ErrorInformation2 != 0;

    public BrotherRasterState State => HasError
        ? BrotherRasterState.Blocked
        : StatusType == 0x01
            ? BrotherRasterState.Completed
            : PhaseType == 0x01
                ? BrotherRasterState.Printing
                : BrotherRasterState.Ready;

    public bool MatchesDieCutMedia(byte widthMillimeters, byte lengthMillimeters) =>
        MediaType == BrotherRasterProtocol.DieCutMediaType &&
        MediaWidthMillimeters == widthMillimeters &&
        MediaLengthMillimeters == lengthMillimeters;

    public bool MatchesMedia(bool continuous, byte widthMillimeters, byte lengthMillimeters) =>
        MediaType == (continuous
            ? BrotherRasterProtocol.ContinuousMediaType
            : BrotherRasterProtocol.DieCutMediaType) &&
        MediaWidthMillimeters == widthMillimeters &&
        MediaLengthMillimeters == lengthMillimeters;
}

public static class BrotherRasterStatusParser
{
    public const int StatusLength = 32;
    public static ReadOnlySpan<byte> StatusRequest => [0x1B, 0x69, 0x53];

    public static BrotherRasterStatus Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length != StatusLength)
        {
            throw new ArgumentException("Brother raster status responses are exactly 32 bytes.", nameof(response));
        }

        if (response[0] != 0x80 || response[1] != 0x20 || response[2] != (byte)'B' ||
            response[3] != (byte)'4' || response[5] != (byte)'0')
        {
            throw new ArgumentException("The response is not a supported Brother raster status frame.", nameof(response));
        }

        // Byte 6 is reserved; the network status observed on QL-820NWB is 0x04,
        // whereas the manual's sample uses 0x30. It is not a frame signature.
        return new BrotherRasterStatus(
            response[4], response[8], response[9], response[10], response[11], response[17],
            response[18], response[19], response[22]);
    }
}
