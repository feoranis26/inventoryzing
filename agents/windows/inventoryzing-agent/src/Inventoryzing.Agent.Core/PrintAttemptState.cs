namespace Inventoryzing.Agent.Core;

public enum PrintAttemptState
{
    Created,
    Claimed,
    Staged,
    Prepared,
    Dispatching,
    DriverAccepted,
    SpoolerQueued,
    Printing,
    Blocked,
    Completed,
    Rejected,
    Failed,
    Unknown,
}

public enum PrintCompletionEvidence
{
    SpoolerConfirmed,
    BrotherMonitorConfirmed,
    DeviceConfirmed,
}

public sealed record PrintAttemptStatus
{
    public PrintAttemptStatus(
        PrintAttemptState state,
        PrintCompletionEvidence? completionEvidence = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        if (completionEvidence is { } evidence && !Enum.IsDefined(evidence))
        {
            throw new ArgumentOutOfRangeException(nameof(completionEvidence));
        }
        if (state == PrintAttemptState.Completed && completionEvidence is null)
        {
            throw new ArgumentException(
                "A completed print attempt requires positive completion evidence.",
                nameof(completionEvidence));
        }
        if (state != PrintAttemptState.Completed && completionEvidence is not null)
        {
            throw new ArgumentException(
                "Completion evidence is valid only for a completed print attempt.",
                nameof(completionEvidence));
        }

        State = state;
        CompletionEvidence = completionEvidence;
    }

    public PrintAttemptState State { get; }

    public PrintCompletionEvidence? CompletionEvidence { get; }
}

public static class PrintAttemptStateMachine
{
    public static bool IsTerminal(PrintAttemptState state)
    {
        ValidateState(state);
        return state is
            PrintAttemptState.Completed or
            PrintAttemptState.Rejected or
            PrintAttemptState.Failed or
            PrintAttemptState.Unknown;
    }

    public static bool CanTransition(PrintAttemptState current, PrintAttemptState next)
    {
        ValidateState(current);
        ValidateState(next);
        return current == next || IsForwardTransition(current, next);
    }

    public static PrintAttemptStatus Transition(
        PrintAttemptStatus current,
        PrintAttemptState next,
        PrintCompletionEvidence? completionEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateState(next);
        ValidateCompletionEvidence(next, completionEvidence);

        if (current.State == PrintAttemptState.Completed && next == PrintAttemptState.Completed)
        {
            return UpgradeCompletionEvidence(current, completionEvidence!.Value);
        }
        if (current.State == next)
        {
            return current;
        }
        if (!IsForwardTransition(current.State, next))
        {
            throw new InvalidOperationException(
                $"Print attempt cannot transition from '{current.State}' to '{next}'.");
        }
        if (next == PrintAttemptState.Completed &&
            completionEvidence == PrintCompletionEvidence.SpoolerConfirmed &&
            current.State == PrintAttemptState.DriverAccepted)
        {
            throw new InvalidOperationException(
                "Spooler completion requires a correlated spooler state before completion.");
        }

        return new PrintAttemptStatus(next, completionEvidence);
    }

    private static bool IsForwardTransition(PrintAttemptState current, PrintAttemptState next) =>
        current switch
        {
            PrintAttemptState.Created =>
                next is PrintAttemptState.Claimed or PrintAttemptState.Rejected,
            PrintAttemptState.Claimed =>
                next is PrintAttemptState.Staged or PrintAttemptState.Rejected,
            PrintAttemptState.Staged =>
                next is PrintAttemptState.Prepared or PrintAttemptState.Rejected,
            PrintAttemptState.Prepared =>
                next is PrintAttemptState.Dispatching or PrintAttemptState.Rejected,
            PrintAttemptState.Dispatching =>
                next is
                    PrintAttemptState.DriverAccepted or
                    PrintAttemptState.Rejected or
                    PrintAttemptState.Unknown,
            PrintAttemptState.DriverAccepted =>
                next is
                    PrintAttemptState.SpoolerQueued or
                    PrintAttemptState.Printing or
                    PrintAttemptState.Blocked or
                    PrintAttemptState.Completed or
                    PrintAttemptState.Failed or
                    PrintAttemptState.Unknown,
            PrintAttemptState.SpoolerQueued =>
                next is
                    PrintAttemptState.Printing or
                    PrintAttemptState.Blocked or
                    PrintAttemptState.Completed or
                    PrintAttemptState.Failed or
                    PrintAttemptState.Unknown,
            PrintAttemptState.Printing =>
                next is
                    PrintAttemptState.Blocked or
                    PrintAttemptState.Completed or
                    PrintAttemptState.Failed or
                    PrintAttemptState.Unknown,
            PrintAttemptState.Blocked =>
                next is
                    PrintAttemptState.SpoolerQueued or
                    PrintAttemptState.Printing or
                    PrintAttemptState.Completed or
                    PrintAttemptState.Failed or
                    PrintAttemptState.Unknown,
            _ => false,
        };

    private static PrintAttemptStatus UpgradeCompletionEvidence(
        PrintAttemptStatus current,
        PrintCompletionEvidence completionEvidence)
    {
        var currentEvidence = current.CompletionEvidence!.Value;
        var comparison = EvidenceStrength(completionEvidence).CompareTo(EvidenceStrength(currentEvidence));
        if (comparison < 0)
        {
            throw new InvalidOperationException(
                $"Completion evidence cannot be downgraded from '{currentEvidence}' " +
                $"to '{completionEvidence}'.");
        }

        return comparison == 0
            ? current
            : new PrintAttemptStatus(PrintAttemptState.Completed, completionEvidence);
    }

    private static int EvidenceStrength(PrintCompletionEvidence evidence) => evidence switch
    {
        PrintCompletionEvidence.SpoolerConfirmed => 1,
        PrintCompletionEvidence.BrotherMonitorConfirmed => 2,
        PrintCompletionEvidence.DeviceConfirmed => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
    };

    private static void ValidateCompletionEvidence(
        PrintAttemptState state,
        PrintCompletionEvidence? completionEvidence)
    {
        _ = new PrintAttemptStatus(state, completionEvidence);
    }

    private static void ValidateState(PrintAttemptState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
    }
}