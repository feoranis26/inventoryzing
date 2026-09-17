namespace Inventoryzing.Agent.Core;

public enum ScannerIndicatorColor
{
    Green,
    Amber,
    Red,
}

public enum ScannerTonePattern
{
    Rising,
    Falling,
    DoubleShort,
    DoubleLowShort,
    RisingDoubleHigh,
}

public sealed record ScannerFeedback(
    ScannerIndicatorColor Color,
    ScannerTonePattern Tone,
    TimeSpan FlashDuration);
