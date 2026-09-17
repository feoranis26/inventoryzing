using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Scanner.Host;

public sealed class ScannerAgentOptions
{
    public const string SectionName = "Inventoryzing:Scanner";

    public bool Enabled { get; set; }
    public Uri? CoordinatorUri { get; set; }
    public string CredentialFile { get; set; } = string.Empty;
    public Guid TerminalId { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan FeedbackFlashDuration { get; set; } = TimeSpan.FromMilliseconds(180);
    public ScannerIndicatorColor SuccessColor { get; set; } = ScannerIndicatorColor.Green;
    public ScannerTonePattern SuccessTone { get; set; } = ScannerTonePattern.Rising;
    public ScannerIndicatorColor CommandSuccessColor { get; set; } = ScannerIndicatorColor.Green;
    public ScannerTonePattern CommandSuccessTone { get; set; } = ScannerTonePattern.RisingDoubleHigh;
    public ScannerIndicatorColor FailureColor { get; set; } = ScannerIndicatorColor.Red;
    public ScannerTonePattern FailureTone { get; set; } = ScannerTonePattern.Falling;
    public ScannerIndicatorColor NoActionColor { get; set; } = ScannerIndicatorColor.Amber;
    public ScannerTonePattern NoActionTone { get; set; } = ScannerTonePattern.DoubleLowShort;

    public ScannerFeedback FeedbackFor(ScannerOperationOutcome outcome, bool commandCompleted = false) => outcome switch
    {
        ScannerOperationOutcome.Success when commandCompleted => new(CommandSuccessColor, CommandSuccessTone, FeedbackFlashDuration),
        ScannerOperationOutcome.Success => new(SuccessColor, SuccessTone, FeedbackFlashDuration),
        ScannerOperationOutcome.Failure => new(FailureColor, FailureTone, FeedbackFlashDuration),
        ScannerOperationOutcome.NoAction => new(NoActionColor, NoActionTone, FeedbackFlashDuration),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
