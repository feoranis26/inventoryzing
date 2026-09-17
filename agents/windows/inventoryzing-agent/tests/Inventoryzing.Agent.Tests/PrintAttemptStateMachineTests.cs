using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintAttemptStateMachineTests
{
    private static readonly HashSet<(PrintAttemptState Current, PrintAttemptState Next)> ForwardTransitions =
    [
        (PrintAttemptState.Created, PrintAttemptState.Claimed),
        (PrintAttemptState.Created, PrintAttemptState.Rejected),
        (PrintAttemptState.Claimed, PrintAttemptState.Staged),
        (PrintAttemptState.Claimed, PrintAttemptState.Rejected),
        (PrintAttemptState.Staged, PrintAttemptState.Prepared),
        (PrintAttemptState.Staged, PrintAttemptState.Rejected),
        (PrintAttemptState.Prepared, PrintAttemptState.Dispatching),
        (PrintAttemptState.Prepared, PrintAttemptState.Rejected),
        (PrintAttemptState.Dispatching, PrintAttemptState.DriverAccepted),
        (PrintAttemptState.Dispatching, PrintAttemptState.Rejected),
        (PrintAttemptState.Dispatching, PrintAttemptState.Unknown),
        (PrintAttemptState.DriverAccepted, PrintAttemptState.SpoolerQueued),
        (PrintAttemptState.DriverAccepted, PrintAttemptState.Printing),
        (PrintAttemptState.DriverAccepted, PrintAttemptState.Blocked),
        (PrintAttemptState.DriverAccepted, PrintAttemptState.Completed),
        (PrintAttemptState.DriverAccepted, PrintAttemptState.Failed),
        (PrintAttemptState.DriverAccepted, PrintAttemptState.Unknown),
        (PrintAttemptState.SpoolerQueued, PrintAttemptState.Printing),
        (PrintAttemptState.SpoolerQueued, PrintAttemptState.Blocked),
        (PrintAttemptState.SpoolerQueued, PrintAttemptState.Completed),
        (PrintAttemptState.SpoolerQueued, PrintAttemptState.Failed),
        (PrintAttemptState.SpoolerQueued, PrintAttemptState.Unknown),
        (PrintAttemptState.Printing, PrintAttemptState.Blocked),
        (PrintAttemptState.Printing, PrintAttemptState.Completed),
        (PrintAttemptState.Printing, PrintAttemptState.Failed),
        (PrintAttemptState.Printing, PrintAttemptState.Unknown),
        (PrintAttemptState.Blocked, PrintAttemptState.SpoolerQueued),
        (PrintAttemptState.Blocked, PrintAttemptState.Printing),
        (PrintAttemptState.Blocked, PrintAttemptState.Completed),
        (PrintAttemptState.Blocked, PrintAttemptState.Failed),
        (PrintAttemptState.Blocked, PrintAttemptState.Unknown),
    ];

    [Fact]
    public void Transition_matrix_accepts_only_documented_state_edges()
    {
        var states = Enum.GetValues<PrintAttemptState>();

        foreach (var current in states)
        {
            foreach (var next in states)
            {
                var expected = current == next || ForwardTransitions.Contains((current, next));

                Assert.Equal(expected, PrintAttemptStateMachine.CanTransition(current, next));
            }
        }
    }

    [Fact]
    public void Driver_acceptance_is_nonterminal_and_cannot_return_to_rejected()
    {
        var accepted = new PrintAttemptStatus(PrintAttemptState.DriverAccepted);

        Assert.False(PrintAttemptStateMachine.IsTerminal(accepted.State));
        Assert.Throws<InvalidOperationException>(() =>
            PrintAttemptStateMachine.Transition(accepted, PrintAttemptState.Rejected));
    }

    [Theory]
    [InlineData(PrintCompletionEvidence.DeviceConfirmed)]
    [InlineData(PrintCompletionEvidence.BrotherMonitorConfirmed)]
    public void Positive_device_or_monitor_evidence_can_complete_after_driver_acceptance(
        PrintCompletionEvidence evidence)
    {
        var accepted = new PrintAttemptStatus(PrintAttemptState.DriverAccepted);

        var completed = PrintAttemptStateMachine.Transition(
            accepted,
            PrintAttemptState.Completed,
            evidence);

        Assert.True(PrintAttemptStateMachine.IsTerminal(completed.State));
        Assert.Equal(evidence, completed.CompletionEvidence);
    }

    [Fact]
    public void Spooler_completion_requires_prior_queue_correlation()
    {
        var accepted = new PrintAttemptStatus(PrintAttemptState.DriverAccepted);
        Assert.Throws<InvalidOperationException>(() =>
            PrintAttemptStateMachine.Transition(
                accepted,
                PrintAttemptState.Completed,
                PrintCompletionEvidence.SpoolerConfirmed));

        var queued = PrintAttemptStateMachine.Transition(
            accepted,
            PrintAttemptState.SpoolerQueued);
        var completed = PrintAttemptStateMachine.Transition(
            queued,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.SpoolerConfirmed);

        Assert.Equal(PrintCompletionEvidence.SpoolerConfirmed, completed.CompletionEvidence);
    }

    [Fact]
    public void Completed_state_requires_evidence_and_other_states_reject_it()
    {
        Assert.Throws<ArgumentException>(() =>
            new PrintAttemptStatus(PrintAttemptState.Completed));
        Assert.Throws<ArgumentException>(() =>
            new PrintAttemptStatus(
                PrintAttemptState.Printing,
                PrintCompletionEvidence.DeviceConfirmed));
    }

    [Fact]
    public void Completion_evidence_can_only_upgrade()
    {
        var spoolerCompleted = new PrintAttemptStatus(
            PrintAttemptState.Completed,
            PrintCompletionEvidence.SpoolerConfirmed);

        var monitorCompleted = PrintAttemptStateMachine.Transition(
            spoolerCompleted,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.BrotherMonitorConfirmed);
        var deviceCompleted = PrintAttemptStateMachine.Transition(
            monitorCompleted,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.DeviceConfirmed);

        Assert.Equal(PrintCompletionEvidence.DeviceConfirmed, deviceCompleted.CompletionEvidence);
        Assert.Throws<InvalidOperationException>(() =>
            PrintAttemptStateMachine.Transition(
                deviceCompleted,
                PrintAttemptState.Completed,
                PrintCompletionEvidence.SpoolerConfirmed));
    }

    [Fact]
    public void Blocked_attempt_resumes_the_same_queue_flow()
    {
        var blocked = new PrintAttemptStatus(PrintAttemptState.Blocked);

        Assert.Equal(
            PrintAttemptState.SpoolerQueued,
            PrintAttemptStateMachine.Transition(blocked, PrintAttemptState.SpoolerQueued).State);
        Assert.Equal(
            PrintAttemptState.Printing,
            PrintAttemptStateMachine.Transition(blocked, PrintAttemptState.Printing).State);
        Assert.Throws<InvalidOperationException>(() =>
            PrintAttemptStateMachine.Transition(blocked, PrintAttemptState.Dispatching));
    }

    [Theory]
    [InlineData(PrintAttemptState.Completed, PrintCompletionEvidence.DeviceConfirmed)]
    [InlineData(PrintAttemptState.Rejected, null)]
    [InlineData(PrintAttemptState.Failed, null)]
    [InlineData(PrintAttemptState.Unknown, null)]
    public void Terminal_states_cannot_move_to_another_state(
        PrintAttemptState state,
        PrintCompletionEvidence? evidence)
    {
        var terminal = new PrintAttemptStatus(state, evidence);

        Assert.True(PrintAttemptStateMachine.IsTerminal(state));
        Assert.Throws<InvalidOperationException>(() =>
            PrintAttemptStateMachine.Transition(terminal, PrintAttemptState.Claimed));
    }

    [Fact]
    public void Replaying_the_same_nonterminal_state_is_idempotent()
    {
        var printing = new PrintAttemptStatus(PrintAttemptState.Printing);

        var replayed = PrintAttemptStateMachine.Transition(printing, PrintAttemptState.Printing);

        Assert.Same(printing, replayed);
    }
}