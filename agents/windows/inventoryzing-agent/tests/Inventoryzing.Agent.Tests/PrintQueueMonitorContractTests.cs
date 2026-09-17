using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintQueueMonitorContractTests
{
    private static readonly DateTimeOffset ArmedAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Correlation_requires_exact_document_and_excludes_baseline_job()
    {
        var baseline = Job(10, "inventoryzing-existing", ArmedAt.AddSeconds(-1));
        var expected = Job(11, "inventoryzing-attempt", ArmedAt.AddMilliseconds(20));
        var request = Request([baseline.Key]);

        var result = PrintQueueCorrelator.Correlate(
            request,
            [
                baseline,
                Job(12, "Inventoryzing-attempt", ArmedAt.AddMilliseconds(30)),
                expected,
            ]);

        Assert.Equal(PrintQueueCorrelationOutcome.Matched, result.Outcome);
        Assert.Equal(expected, result.Match);
    }

    [Fact]
    public void Reused_job_id_is_distinguished_by_submission_time()
    {
        var old = Job(42, "inventoryzing-attempt", ArmedAt.AddMinutes(-1));
        var reused = Job(42, "inventoryzing-attempt", ArmedAt.AddMilliseconds(10));

        var result = PrintQueueCorrelator.Correlate(Request([old.Key]), [old, reused]);

        Assert.Equal(reused, result.Match);
    }

    [Fact]
    public void Owner_and_machine_mismatch_are_rejected_when_queue_reports_them()
    {
        var matching = Job(
            1,
            "inventoryzing-attempt",
            ArmedAt,
            ownerName: "inventory-agent",
            machineName: "WORKSTATION");
        var wrongOwner = Job(
            2,
            "inventoryzing-attempt",
            ArmedAt,
            ownerName: "other-user",
            machineName: "WORKSTATION");
        var missingIdentity = Job(3, "inventoryzing-attempt", ArmedAt);

        var result = PrintQueueCorrelator.Correlate(
            Request([], "inventory-agent", "workstation"),
            [matching, wrongOwner, missingIdentity]);

        Assert.Equal(PrintQueueCorrelationOutcome.Ambiguous, result.Outcome);
        Assert.Equal([matching, missingIdentity], result.Candidates);
    }

    [Fact]
    public void Zero_and_multiple_candidates_are_never_guessed()
    {
        var request = Request([]);

        var none = PrintQueueCorrelator.Correlate(request, []);
        var multiple = PrintQueueCorrelator.Correlate(
            request,
            [
                Job(1, "inventoryzing-attempt", ArmedAt),
                Job(2, "inventoryzing-attempt", ArmedAt.AddMilliseconds(1)),
            ]);

        Assert.Equal(PrintQueueCorrelationOutcome.NoMatch, none.Outcome);
        Assert.Null(none.Match);
        Assert.Equal(PrintQueueCorrelationOutcome.Ambiguous, multiple.Outcome);
        Assert.Null(multiple.Match);
    }

    [Fact]
    public void Printed_is_positive_spooler_evidence_but_complete_is_not()
    {
        var printer = new PrintQueuePrinterSnapshot(0);

        var printed = PrintQueueStatusReducer.Assess(
            Job(1, "job", ArmedAt, PrintQueueJobStatus.Printed),
            printer);
        var sent = PrintQueueStatusReducer.Assess(
            Job(2, "job", ArmedAt, PrintQueueJobStatus.Complete),
            printer);

        Assert.Equal(PrintAttemptState.Completed, printed.Status.State);
        Assert.Equal(PrintCompletionEvidence.SpoolerConfirmed, printed.Status.CompletionEvidence);
        Assert.Equal(PrintAttemptState.SpoolerQueued, sent.Status.State);
        Assert.Null(sent.Status.CompletionEvidence);
        Assert.Equal("queue.job.sent_to_printer", sent.Code);
    }

    [Theory]
    [InlineData(PrintQueueJobStatus.Paused, "queue.job.paused", true)]
    [InlineData(PrintQueueJobStatus.Error, "queue.job.error", false)]
    [InlineData(PrintQueueJobStatus.Offline, "queue.job.offline", false)]
    [InlineData(PrintQueueJobStatus.PaperOut, "queue.job.paper_out", false)]
    [InlineData(PrintQueueJobStatus.BlockedDeviceQueue, "queue.job.blocked_device_queue", false)]
    [InlineData(PrintQueueJobStatus.UserIntervention, "queue.job.user_intervention", false)]
    public void Recoverable_job_status_remains_blocked(
        PrintQueueJobStatus status,
        string expectedCode,
        bool expectedCanResume)
    {
        var assessment = PrintQueueStatusReducer.Assess(
            Job(1, "job", ArmedAt, status, "Raw status"),
            new PrintQueuePrinterSnapshot(0));

        Assert.Equal(PrintAttemptState.Blocked, assessment.Status.State);
        Assert.Equal(expectedCode, assessment.Code);
        Assert.Equal(expectedCanResume, assessment.CanResume);
        Assert.Equal("Raw status", assessment.Detail);
    }

    [Fact]
    public void Door_open_is_actionable_blocked_state_and_deletion_is_unknown()
    {
        var job = Job(1, "job", ArmedAt);
        var doorOpen = PrintQueueStatusReducer.Assess(
            job,
            new PrintQueuePrinterSnapshot(
                (uint)PrintQueuePrinterStatus.DoorOpen,
                "Cover open"));
        var deleted = PrintQueueStatusReducer.Assess(
            Job(1, "job", ArmedAt, PrintQueueJobStatus.Deleted),
            new PrintQueuePrinterSnapshot(0));

        Assert.Equal(PrintAttemptState.Blocked, doorOpen.Status.State);
        Assert.Equal("queue.printer.door_open", doorOpen.Code);
        Assert.Equal("Cover open", doorOpen.Detail);
        Assert.Equal(PrintAttemptState.Unknown, deleted.Status.State);
        Assert.Null(deleted.Status.CompletionEvidence);
    }

    [Fact]
    public void Printing_and_unknown_raw_bits_are_preserved_without_false_completion()
    {
        const uint unknownStatusBit = 0x80000000;
        var job = Job(
            1,
            "job",
            ArmedAt,
            (PrintQueueJobStatus)((uint)PrintQueueJobStatus.Printing | unknownStatusBit));

        var assessment = PrintQueueStatusReducer.Assess(job, new PrintQueuePrinterSnapshot(0));

        Assert.Equal((uint)PrintQueueJobStatus.Printing | unknownStatusBit, job.RawStatus);
        Assert.Equal(PrintAttemptState.Printing, assessment.Status.State);
        Assert.Null(assessment.Status.CompletionEvidence);
    }

    private static PrintQueueCorrelationRequest Request(
        IEnumerable<PrintQueueJobKey> baseline,
        string? expectedOwner = null,
        string? expectedMachine = null) =>
        new(
            new PrintQueueIdentity("Brother QL-820NWB"),
            "inventoryzing-attempt",
            ArmedAt.AddSeconds(-1),
            ArmedAt.AddSeconds(5),
            baseline,
            expectedOwner,
            expectedMachine);

    private static PrintQueueJobSnapshot Job(
        uint jobId,
        string documentName,
        DateTimeOffset submittedAt,
        PrintQueueJobStatus status = PrintQueueJobStatus.Spooling,
        string? statusText = null,
        string? ownerName = null,
        string? machineName = null) =>
        new(
            jobId,
            documentName,
            submittedAt,
            (uint)status,
            statusText,
            ownerName,
            machineName);
}