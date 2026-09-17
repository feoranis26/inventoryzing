namespace Inventoryzing.Agent.Brother;

/// <summary>
/// Pure QL-800-series raster command construction. This type deliberately has no transport.
/// Media preflight and status monitoring are separate network concerns.
/// </summary>
public static class BrotherRasterProtocol
{
    public const int DotColumns = 720;
    public const int BytesPerRasterLine = DotColumns / 8;
    public const byte DieCutMediaType = 0x0B;
    public const byte ContinuousMediaType = 0x0A;

    public static byte[] BuildMonochromePage(
        bool continuous,
        int mediaWidthMillimeters,
        int mediaLengthMillimeters,
        int feedMarginDots,
        int rasterLineCount,
        ReadOnlySpan<byte> rasterLines)
    {
        if (mediaWidthMillimeters is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaWidthMillimeters));
        }

        if (continuous ? mediaLengthMillimeters != 0 : mediaLengthMillimeters is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaLengthMillimeters));
        }

        if (feedMarginDots is < 0 or > ushort.MaxValue ||
            (continuous ? feedMarginDots is < 35 or > 1500 : feedMarginDots != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(feedMarginDots));
        }

        if (rasterLineCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rasterLineCount));
        }

        if (rasterLines.Length != checked(rasterLineCount * BytesPerRasterLine))
        {
            throw new ArgumentException(
                $"A monochrome page requires exactly {BytesPerRasterLine} bytes per raster line.",
                nameof(rasterLines));
        }

        var output = new byte[400 + 2 + 4 + 4 + 13 + 19 + rasterLineCount * (3 + BytesPerRasterLine) + 1];
        var cursor = 400; // 400-byte NULL invalidate command.
        output[cursor++] = 0x1B;
        output[cursor++] = 0x40; // ESC @ initialize
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x61;
        output[cursor++] = 0x01; // raster mode
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x21;
        output[cursor++] = 0x00; // automatic status notification enabled
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x7A;
        output[cursor++] = 0x4E; // validate media and prioritize print quality
        output[cursor++] = continuous ? ContinuousMediaType : DieCutMediaType;
        output[cursor++] = checked((byte)mediaWidthMillimeters);
        output[cursor++] = checked((byte)mediaLengthMillimeters);
        WriteLittleEndianInt32(output, ref cursor, rasterLineCount);
        output[cursor++] = 0x00; // first page
        output[cursor++] = 0x00; // fixed
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x4D;
        output[cursor++] = 0x40; // auto cut
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x41;
        output[cursor++] = 0x01; // cut every label
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x4B;
        output[cursor++] = 0x08; // cut at end, 300 dpi
        output[cursor++] = 0x1B;
        output[cursor++] = 0x69;
        output[cursor++] = 0x64;
        output[cursor++] = (byte)feedMarginDots;
        output[cursor++] = (byte)(feedMarginDots >> 8);
        output[cursor++] = (byte)'M';
        output[cursor++] = 0x00; // no compression

        for (var line = 0; line < rasterLineCount; line++)
        {
            output[cursor++] = (byte)'g';
            output[cursor++] = 0x00;
            output[cursor++] = BytesPerRasterLine;
            rasterLines.Slice(line * BytesPerRasterLine, BytesPerRasterLine).CopyTo(output.AsSpan(cursor));
            cursor += BytesPerRasterLine;
        }

        output[cursor++] = 0x1A; // final-page print command with feeding
        return output;
    }

    public static byte[] BuildDieCutMonochromePage(
        int mediaWidthMillimeters,
        int mediaLengthMillimeters,
        int rasterLineCount,
        ReadOnlySpan<byte> rasterLines) =>
        BuildMonochromePage(false, mediaWidthMillimeters, mediaLengthMillimeters, 0,
            rasterLineCount, rasterLines);

    private static void WriteLittleEndianInt32(byte[] output, ref int cursor, int value)
    {
        output[cursor++] = (byte)value;
        output[cursor++] = (byte)(value >> 8);
        output[cursor++] = (byte)(value >> 16);
        output[cursor++] = (byte)(value >> 24);
    }
}
