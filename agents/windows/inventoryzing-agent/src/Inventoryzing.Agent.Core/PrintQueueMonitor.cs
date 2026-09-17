namespace Inventoryzing.Agent.Core;

[Flags]
public enum PrintQueueJobStatus : uint
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    Deleting = 0x00000004,
    Spooling = 0x00000008,
    Printing = 0x00000010,
    Offline = 0x00000020,
    PaperOut = 0x00000040,
    Printed = 0x00000080,
    Deleted = 0x00000100,
    BlockedDeviceQueue = 0x00000200,
    UserIntervention = 0x00000400,
    Restart = 0x00000800,
    Complete = 0x00001000,
    Retained = 0x00002000,
    RenderingLocally = 0x00004000,
}

[Flags]
public enum PrintQueuePrinterStatus : uint
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    PendingDeletion = 0x00000004,
    PaperJam = 0x00000008,
    PaperOut = 0x00000010,
    ManualFeed = 0x00000020,
    PaperProblem = 0x00000040,
    Offline = 0x00000080,
    IoActive = 0x00000100,
    Busy = 0x00000200,
    Printing = 0x00000400,
    OutputBinFull = 0x00000800,
    NotAvailable = 0x00001000,
    Waiting = 0x00002000,
    Processing = 0x00004000,
    Initializing = 0x00008000,
    WarmingUp = 0x00010000,
    TonerLow = 0x00020000,
    NoToner = 0x00040000,
    PagePunt = 0x00080000,
    UserIntervention = 0x00100000,
    OutOfMemory = 0x00200000,
    DoorOpen = 0x00400000,
    ServerUnknown = 0x00800000,
    PowerSave = 0x01000000,
}

public sealed record PrintQueueIdentity
{
    public PrintQueueIdentity(string printerName, string? serverName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (serverName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        }

        PrinterName = printerName;
        ServerName = serverName;
    }

    public string PrinterName { get; }

    public string? ServerName { get; }
}

public sealed record PrintQueueJobKey
{
    public PrintQueueJobKey(uint jobId, DateTimeOffset submittedAt)
    {
        ArgumentOutOfRangeException.ThrowIfZero(jobId);
        JobId = jobId;
        SubmittedAt = submittedAt.ToUniversalTime();
    }

    public uint JobId { get; }

    public DateTimeOffset SubmittedAt { get; }
}

public sealed record PrintQueueJobSnapshot
{
    public PrintQueueJobSnapshot(
        uint jobId,
        string documentName,
        DateTimeOffset submittedAt,
        uint rawStatus,
        string? statusText = null,
        string? ownerName = null,
        string? machineName = null,
        uint totalPages = 0,
        uint pagesPrinted = 0)
    {
        ArgumentOutOfRangeException.ThrowIfZero(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        if (pagesPrinted > totalPages && totalPages != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pagesPrinted),
                "Printed pages cannot exceed a known total page count.");
        }

        JobId = jobId;
        DocumentName = documentName;
        SubmittedAt = submittedAt.ToUniversalTime();
        RawStatus = rawStatus;
        StatusText = statusText;
        OwnerName = ownerName;
        MachineName = machineName;
        TotalPages = totalPages;
        PagesPrinted = pagesPrinted;
    }

    public uint JobId { get; }

    public string DocumentName { get; }

    public DateTimeOffset SubmittedAt { get; }

    public uint RawStatus { get; }

    public string? StatusText { get; }

    public string? OwnerName { get; }

    public string? MachineName { get; }

    public uint TotalPages { get; }

    public uint PagesPrinted { get; }

    public PrintQueueJobStatus Status => (PrintQueueJobStatus)RawStatus;

    public PrintQueueJobKey Key => new(JobId, SubmittedAt);
}

public sealed record PrintQueuePrinterSnapshot(uint RawStatus, string? StatusText = null)
{
    public PrintQueuePrinterStatus Status => (PrintQueuePrinterStatus)RawStatus;
}

public sealed class PrintQueueCorrelationRequest
{
    private readonly HashSet<PrintQueueJobKey> baselineJobs;

    public PrintQueueCorrelationRequest(
        PrintQueueIdentity queue,
        string documentName,
        DateTimeOffset earliestSubmittedAt,
        DateTimeOffset latestSubmittedAt,
        IEnumerable<PrintQueueJobKey> baselineJobs,
        string? expectedOwnerName = null,
        string? expectedMachineName = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        ArgumentNullException.ThrowIfNull(baselineJobs);
        if (latestSubmittedAt < earliestSubmittedAt)
        {
            throw new ArgumentException(
                "The latest submission time cannot precede the earliest submission time.",
                nameof(latestSubmittedAt));
        }

        Queue = queue;
        DocumentName = documentName;
        EarliestSubmittedAt = earliestSubmittedAt.ToUniversalTime();
        LatestSubmittedAt = latestSubmittedAt.ToUniversalTime();
        this.baselineJobs = [.. baselineJobs];
        ExpectedOwnerName = expectedOwnerName;
        ExpectedMachineName = expectedMachineName;
    }

    public PrintQueueIdentity Queue { get; }

    public string DocumentName { get; }

    public DateTimeOffset EarliestSubmittedAt { get; }

    public DateTimeOffset LatestSubmittedAt { get; }

    public IReadOnlySet<PrintQueueJobKey> BaselineJobs => baselineJobs;

    public string? ExpectedOwnerName { get; }

    public string? ExpectedMachineName { get; }
}

public enum PrintQueueCorrelationOutcome
{
    NoMatch,
    Matched,
    Ambiguous,
}

public sealed record PrintQueueCorrelationResult
{
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<PrintQueueJobSnapshot> candidates;

    public PrintQueueCorrelationResult(
        PrintQueueCorrelationOutcome outcome,
        IEnumerable<PrintQueueJobSnapshot> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var snapshot = candidates.ToArray();
        if ((outcome == PrintQueueCorrelationOutcome.NoMatch && snapshot.Length != 0) ||
            (outcome == PrintQueueCorrelationOutcome.Matched && snapshot.Length != 1) ||
            (outcome == PrintQueueCorrelationOutcome.Ambiguous && snapshot.Length < 2))
        {
            throw new ArgumentException(
                "Correlation outcome does not match its candidate count.",
                nameof(candidates));
        }

        Outcome = outcome;
        this.candidates = Array.AsReadOnly(snapshot);
    }

    public PrintQueueCorrelationOutcome Outcome { get; }

    public IReadOnlyList<PrintQueueJobSnapshot> Candidates => candidates;

    public PrintQueueJobSnapshot? Match =>
        Outcome == PrintQueueCorrelationOutcome.Matched ? candidates[0] : null;
}

public static class PrintQueueCorrelator
{
    public static PrintQueueCorrelationResult Correlate(
        PrintQueueCorrelationRequest request,
        IEnumerable<PrintQueueJobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        var candidates = jobs
            .Where(job => IsCandidate(request, job))
            .DistinctBy(job => job.Key)
            .ToArray();
        var outcome = candidates.Length switch
        {
            0 => PrintQueueCorrelationOutcome.NoMatch,
            1 => PrintQueueCorrelationOutcome.Matched,
            _ => PrintQueueCorrelationOutcome.Ambiguous,
        };
        return new PrintQueueCorrelationResult(outcome, candidates);
    }

    private static bool IsCandidate(
        PrintQueueCorrelationRequest request,
        PrintQueueJobSnapshot job) =>
        string.Equals(job.DocumentName, request.DocumentName, StringComparison.Ordinal) &&
        job.SubmittedAt >= request.EarliestSubmittedAt &&
        job.SubmittedAt <= request.LatestSubmittedAt &&
        !request.BaselineJobs.Contains(job.Key) &&
        OptionalIdentityMatches(request.ExpectedOwnerName, job.OwnerName) &&
        OptionalIdentityMatches(request.ExpectedMachineName, job.MachineName);

    private static bool OptionalIdentityMatches(string? expected, string? actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.IsNullOrWhiteSpace(actual) ||
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}

public sealed record PrintQueueJobAssessment(
    PrintAttemptStatus Status,
    bool CanResume,
    string Code,
    string? Detail);

public static class PrintQueueStatusReducer
{
    private const PrintQueueJobStatus BlockingJobStatuses =
        PrintQueueJobStatus.Paused |
        PrintQueueJobStatus.Error |
        PrintQueueJobStatus.Offline |
        PrintQueueJobStatus.PaperOut |
        PrintQueueJobStatus.BlockedDeviceQueue |
        PrintQueueJobStatus.UserIntervention;

    private const PrintQueuePrinterStatus BlockingPrinterStatuses =
        PrintQueuePrinterStatus.Paused |
        PrintQueuePrinterStatus.Error |
        PrintQueuePrinterStatus.PaperJam |
        PrintQueuePrinterStatus.PaperOut |
        PrintQueuePrinterStatus.ManualFeed |
        PrintQueuePrinterStatus.PaperProblem |
        PrintQueuePrinterStatus.Offline |
        PrintQueuePrinterStatus.OutputBinFull |
        PrintQueuePrinterStatus.NotAvailable |
        PrintQueuePrinterStatus.NoToner |
        PrintQueuePrinterStatus.UserIntervention |
        PrintQueuePrinterStatus.OutOfMemory |
        PrintQueuePrinterStatus.DoorOpen |
        PrintQueuePrinterStatus.ServerUnknown;

    public static PrintQueueJobAssessment Assess(
        PrintQueueJobSnapshot job,
        PrintQueuePrinterSnapshot printer)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(printer);
        var jobStatus = job.Status;
        var printerStatus = printer.Status;

        if (jobStatus.HasFlag(PrintQueueJobStatus.Printed))
        {
            return Result(
                PrintAttemptState.Completed,
                PrintCompletionEvidence.SpoolerConfirmed,
                canResume: false,
                "queue.job.printed",
                job.StatusText);
        }
        if ((jobStatus & (PrintQueueJobStatus.Deleting | PrintQueueJobStatus.Deleted)) != 0)
        {
            return Result(
                PrintAttemptState.Unknown,
                null,
                canResume: false,
                "queue.job.deleted",
                job.StatusText);
        }
        if ((jobStatus & BlockingJobStatuses) != 0)
        {
            return Result(
                PrintAttemptState.Blocked,
                null,
                jobStatus.HasFlag(PrintQueueJobStatus.Paused),
                BlockingJobCode(jobStatus),
                job.StatusText);
        }
        if ((printerStatus & BlockingPrinterStatuses) != 0)
        {
            return Result(
                PrintAttemptState.Blocked,
                null,
                jobStatus.HasFlag(PrintQueueJobStatus.Paused),
                BlockingPrinterCode(printerStatus),
                printer.StatusText);
        }
        if (jobStatus.HasFlag(PrintQueueJobStatus.Printing))
        {
            return Result(
                PrintAttemptState.Printing,
                null,
                canResume: false,
                "queue.job.printing",
                job.StatusText);
        }

        return Result(
            PrintAttemptState.SpoolerQueued,
            null,
            canResume: false,
            jobStatus.HasFlag(PrintQueueJobStatus.Complete)
                ? "queue.job.sent_to_printer"
                : "queue.job.queued",
            job.StatusText);
    }

    private static PrintQueueJobAssessment Result(
        PrintAttemptState state,
        PrintCompletionEvidence? evidence,
        bool canResume,
        string code,
        string? detail) =>
        new(new PrintAttemptStatus(state, evidence), canResume, code, detail);

    private static string BlockingJobCode(PrintQueueJobStatus status)
    {
        if (status.HasFlag(PrintQueueJobStatus.Paused))
        {
            return "queue.job.paused";
        }
        if (status.HasFlag(PrintQueueJobStatus.PaperOut))
        {
            return "queue.job.paper_out";
        }
        if (status.HasFlag(PrintQueueJobStatus.Offline))
        {
            return "queue.job.offline";
        }
        if (status.HasFlag(PrintQueueJobStatus.UserIntervention))
        {
            return "queue.job.user_intervention";
        }
        if (status.HasFlag(PrintQueueJobStatus.BlockedDeviceQueue))
        {
            return "queue.job.blocked_device_queue";
        }

        return "queue.job.error";
    }

    private static string BlockingPrinterCode(PrintQueuePrinterStatus status)
    {
        if (status.HasFlag(PrintQueuePrinterStatus.DoorOpen))
        {
            return "queue.printer.door_open";
        }
        if (status.HasFlag(PrintQueuePrinterStatus.PaperOut))
        {
            return "queue.printer.paper_out";
        }
        if (status.HasFlag(PrintQueuePrinterStatus.PaperJam))
        {
            return "queue.printer.paper_jam";
        }
        if (status.HasFlag(PrintQueuePrinterStatus.Offline))
        {
            return "queue.printer.offline";
        }
        if (status.HasFlag(PrintQueuePrinterStatus.Paused))
        {
            return "queue.printer.paused";
        }
        if (status.HasFlag(PrintQueuePrinterStatus.UserIntervention))
        {
            return "queue.printer.user_intervention";
        }

        return "queue.printer.error";
    }
}

public sealed class PrintQueueSnapshot
{
    private readonly IReadOnlyList<PrintQueueJobSnapshot> jobs;

    public PrintQueueSnapshot(
        PrintQueueIdentity queue,
        IEnumerable<PrintQueueJobSnapshot> jobs,
        PrintQueuePrinterSnapshot printer,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(printer);
        Queue = queue;
        this.jobs = Array.AsReadOnly(jobs.ToArray());
        Printer = printer;
        ObservedAt = observedAt.ToUniversalTime();
    }

    public PrintQueueIdentity Queue { get; }

    public IReadOnlyList<PrintQueueJobSnapshot> Jobs => jobs;

    public PrintQueuePrinterSnapshot Printer { get; }

    public DateTimeOffset ObservedAt { get; }
}

[Flags]
public enum PrintQueueChange : uint
{
    None = 0,
    AddJob = 0x00000100,
    SetJob = 0x00000200,
    DeleteJob = 0x00000400,
    WriteJob = 0x00000800,
    SetPrinter = 0x00000002,
    FailedConnectionPrinter = 0x00000008,
}

public sealed record PrintQueueChangeNotification(
    PrintQueueChange Changes,
    bool TimedOut,
    DateTimeOffset ObservedAt);

public sealed class PrintQueueMonitorArm
{
    private readonly IReadOnlyList<PrintQueueJobKey> baselineJobs;

    public PrintQueueMonitorArm(
        Guid generation,
        PrintQueueSnapshot baseline,
        DateTimeOffset armedAt)
    {
        if (generation == Guid.Empty)
        {
            throw new ArgumentException("Monitor generation cannot be empty.", nameof(generation));
        }

        ArgumentNullException.ThrowIfNull(baseline);
        Generation = generation;
        Queue = baseline.Queue;
        Baseline = baseline;
        ArmedAt = armedAt.ToUniversalTime();
        baselineJobs = Array.AsReadOnly(baseline.Jobs.Select(job => job.Key).ToArray());
    }

    public Guid Generation { get; }

    public PrintQueueIdentity Queue { get; }

    public PrintQueueSnapshot Baseline { get; }

    public DateTimeOffset ArmedAt { get; }

    public IReadOnlyList<PrintQueueJobKey> BaselineJobs => baselineJobs;

    public PrintQueueCorrelationRequest CreateCorrelationRequest(
        string documentName,
        DateTimeOffset deadline,
        string? expectedOwnerName = null,
        string? expectedMachineName = null) =>
        new(
            Queue,
            documentName,
            ArmedAt,
            deadline,
            baselineJobs,
            expectedOwnerName,
            expectedMachineName);
}

public sealed record PrintQueueJobReference
{
    public PrintQueueJobReference(
        Guid monitorGeneration,
        PrintQueueIdentity queue,
        PrintQueueJobSnapshot job)
        : this(
            monitorGeneration,
            queue,
            job?.JobId ?? throw new ArgumentNullException(nameof(job)),
            job.DocumentName,
            job.SubmittedAt)
    {
    }

    public PrintQueueJobReference(
        Guid monitorGeneration,
        PrintQueueIdentity queue,
        uint jobId,
        string documentName,
        DateTimeOffset submittedAt)
    {
        if (monitorGeneration == Guid.Empty)
        {
            throw new ArgumentException("Monitor generation cannot be empty.", nameof(monitorGeneration));
        }

        ArgumentNullException.ThrowIfNull(queue);
        ArgumentOutOfRangeException.ThrowIfZero(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        MonitorGeneration = monitorGeneration;
        Queue = queue;
        JobId = jobId;
        DocumentName = documentName;
        SubmittedAt = submittedAt.ToUniversalTime();
    }

    public Guid MonitorGeneration { get; }

    public PrintQueueIdentity Queue { get; }

    public uint JobId { get; }

    public string DocumentName { get; }

    public DateTimeOffset SubmittedAt { get; }
}

public enum PrintQueueResumeOutcome
{
    Resumed,
    AlreadyResumed,
    JobMissing,
    IdentityMismatch,
    NotPaused,
}

public sealed record PrintQueueResumeResult(PrintQueueResumeOutcome Outcome, string? Detail = null);

public interface IPrintQueueMonitor
{
    ValueTask<IPrintQueueMonitorSession> ArmAsync(
        PrintQueueIdentity queue,
        CancellationToken cancellationToken);
}

public interface IPrintQueueMonitorSession : IAsyncDisposable
{
    PrintQueueMonitorArm Arm { get; }

    ValueTask<PrintQueueSnapshot> RefreshAsync(CancellationToken cancellationToken);

    ValueTask<PrintQueueJobSnapshot?> ReadJobAsync(
        uint jobId,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueChangeNotification> WaitForChangeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueResumeResult> ResumeAsync(
        PrintQueueJobReference reference,
        CancellationToken cancellationToken);
}
