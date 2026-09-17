using Inventoryzing.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<PrintQueueCorrelationRecord> StoreQueueCorrelationAsync(
        PrintQueueCorrelationRecord correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ValidateAttemptId(correlation.AttemptId);
        var normalized = Normalize(correlation);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindQueueCorrelationAsync(
            connection,
            transaction,
            correlation.AttemptId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print attempt '{correlation.AttemptId}' already has a different queue correlation.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        _ = await FindAsync(
            connection,
            transaction,
            correlation.AttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Print attempt '{correlation.AttemptId}' was not found.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_queue_correlations (
                attempt_id,
                monitor_generation,
                printer_name,
                server_name,
                job_id,
                document_name,
                submitted_at_utc,
                correlated_at_utc)
            VALUES (
                $attempt_id,
                $monitor_generation,
                $printer_name,
                $server_name,
                $job_id,
                $document_name,
                $submitted_at_utc,
                $correlated_at_utc);
            """;
        AddCorrelationParameters(command, normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async ValueTask<PrintQueueCorrelationRecord?> FindQueueCorrelationAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindQueueCorrelationAsync(
            connection,
            null,
            attemptId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RecoverablePrintQueueAttempt>>
        ReadRecoverableQueueAttemptsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                attempts.attempt_id,
                attempts.state,
                attempts.completion_evidence,
                attempts.version,
                attempts.created_at_utc,
                attempts.updated_at_utc,
                correlations.attempt_id,
                correlations.monitor_generation,
                correlations.printer_name,
                correlations.server_name,
                correlations.job_id,
                correlations.document_name,
                correlations.submitted_at_utc,
                correlations.correlated_at_utc
            FROM print_attempts AS attempts
            INNER JOIN print_queue_correlations AS correlations
                ON correlations.attempt_id = attempts.attempt_id
            WHERE attempts.state NOT IN ('Completed', 'Rejected', 'Failed', 'Unknown')
            ORDER BY attempts.created_at_utc, attempts.attempt_id;
            """;
        List<RecoverablePrintQueueAttempt> attempts = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(new RecoverablePrintQueueAttempt(
                ReadAttempt(reader),
                ReadCorrelation(reader, 6)));
        }

        return attempts;
    }

    public async ValueTask<PrintObservationRecord> AppendObservationAsync(
        PrintObservation observation,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(observation);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindObservationAsync(
            connection,
            transaction,
            observation.ObservationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Observation != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print observation '{observation.ObservationId}' was replayed with a different payload.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        _ = await FindAsync(
            connection,
            transaction,
            observation.AttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Print attempt '{observation.AttemptId}' was not found.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_observations (
                observation_id,
                attempt_id,
                source,
                code,
                detail,
                raw_job_status,
                raw_printer_status,
                raw_provider_status,
                monitor_generation,
                observed_at_utc,
                received_at_utc)
            VALUES (
                $observation_id,
                $attempt_id,
                $source,
                $code,
                $detail,
                $raw_job_status,
                $raw_printer_status,
                $raw_provider_status,
                $monitor_generation,
                $observed_at_utc,
                $received_at_utc)
            RETURNING sequence;
            """;
        AddObservationParameters(command, normalized);
        var sequence = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrintObservationRecord(Convert.ToInt64(sequence), normalized);
    }

    public async ValueTask<IReadOnlyList<PrintObservationRecord>> ReadObservationsAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                sequence,
                observation_id,
                attempt_id,
                source,
                code,
                detail,
                raw_job_status,
                raw_printer_status,
                raw_provider_status,
                monitor_generation,
                observed_at_utc,
                received_at_utc
            FROM print_observations
            WHERE attempt_id = $attempt_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        List<PrintObservationRecord> observations = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observations.Add(ReadObservation(reader));
        }

        return observations;
    }

    public async ValueTask<PrintQueueResumeCommandRecord> BeginResumeAsync(
        Guid commandId,
        Guid attemptId,
        PrintQueueJobReference job,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        ValidateCommandId(commandId);
        ValidateAttemptId(attemptId);
        ArgumentNullException.ThrowIfNull(job);
        var normalizedJob = Normalize(job);
        var normalizedRequestedAt = requestedAt.ToUniversalTime();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindResumeAsync(
            connection,
            transaction,
            commandId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!ResumeRequestMatches(existing, attemptId, normalizedJob, normalizedRequestedAt))
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print Resume command '{commandId}' was replayed with a different request.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var attempt = await FindAsync(
            connection,
            transaction,
            attemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");
        if (attempt.Status.State != PrintAttemptState.Blocked)
        {
            throw new PrintAttemptJournalConflictException(
                $"Print attempt '{attemptId}' is not blocked and cannot begin Resume.");
        }

        var correlation = await FindQueueCorrelationAsync(
            connection,
            transaction,
            attemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptJournalConflictException(
                $"Print attempt '{attemptId}' has no persisted queue correlation.");
        if (!QueueJobIdentityMatches(correlation.Job, normalizedJob))
        {
            throw new PrintAttemptJournalConflictException(
                $"Print Resume command '{commandId}' does not match the persisted queue correlation.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_queue_resume_commands (
                command_id,
                attempt_id,
                monitor_generation,
                printer_name,
                server_name,
                job_id,
                document_name,
                submitted_at_utc,
                requested_at_utc,
                outcome,
                result_detail,
                completed_at_utc)
            VALUES (
                $command_id,
                $attempt_id,
                $monitor_generation,
                $printer_name,
                $server_name,
                $job_id,
                $document_name,
                $submitted_at_utc,
                $requested_at_utc,
                NULL,
                NULL,
                NULL);
            """;
        AddResumeRequestParameters(
            command,
            commandId,
            attemptId,
            normalizedJob,
            normalizedRequestedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrintQueueResumeCommandRecord(
            commandId,
            attemptId,
            normalizedJob,
            normalizedRequestedAt,
            null,
            null);
    }

    public async ValueTask<PrintQueueResumeCommandRecord> CompleteResumeAsync(
        Guid commandId,
        PrintQueueResumeResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        ValidateCommandId(commandId);
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(result.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        var normalizedCompletedAt = completedAt.ToUniversalTime();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindResumeAsync(
            connection,
            transaction,
            commandId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Print Resume command '{commandId}' was not found.");
        if (normalizedCompletedAt < existing.RequestedAt)
        {
            throw new ArgumentException(
                "Resume completion time cannot precede its request time.",
                nameof(completedAt));
        }
        if (existing.Result is not null)
        {
            if (existing.Result != result || existing.CompletedAt != normalizedCompletedAt)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print Resume command '{commandId}' was completed with a different result.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE print_queue_resume_commands
            SET
                outcome = $outcome,
                result_detail = $result_detail,
                completed_at_utc = $completed_at_utc
            WHERE command_id = $command_id AND outcome IS NULL;
            """;
        command.Parameters.AddWithValue("$outcome", result.Outcome.ToString());
        command.Parameters.AddWithValue("$result_detail", DatabaseText(result.Detail));
        command.Parameters.AddWithValue("$completed_at_utc", FormatTimestamp(normalizedCompletedAt));
        command.Parameters.AddWithValue("$command_id", FormatId(commandId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PrintAttemptJournalConflictException(
                $"Print Resume command '{commandId}' changed while its result was stored.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing with { Result = result, CompletedAt = normalizedCompletedAt };
    }

    public async ValueTask<PrintQueueResumeCommandRecord?> FindResumeAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        ValidateCommandId(commandId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindResumeAsync(
            connection,
            null,
            commandId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<PrintQueueResumeCommandRecord>> ReadPendingResumesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {ResumeSelect}
            WHERE outcome IS NULL
            ORDER BY requested_at_utc, command_id;
            """;
        List<PrintQueueResumeCommandRecord> commands = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            commands.Add(ReadResume(reader));
        }

        return commands;
    }

    private const string CorrelationSelect = """
        SELECT
            attempt_id,
            monitor_generation,
            printer_name,
            server_name,
            job_id,
            document_name,
            submitted_at_utc,
            correlated_at_utc
        FROM print_queue_correlations
        """;

    private const string ObservationSelect = """
        SELECT
            sequence,
            observation_id,
            attempt_id,
            source,
            code,
            detail,
            raw_job_status,
            raw_printer_status,
            raw_provider_status,
            monitor_generation,
            observed_at_utc,
            received_at_utc
        FROM print_observations
        """;

    private const string ResumeSelect = """
        SELECT
            command_id,
            attempt_id,
            monitor_generation,
            printer_name,
            server_name,
            job_id,
            document_name,
            submitted_at_utc,
            requested_at_utc,
            outcome,
            result_detail,
            completed_at_utc
        FROM print_queue_resume_commands
        """;

    private static async ValueTask<PrintQueueCorrelationRecord?> FindQueueCorrelationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {CorrelationSelect}
            WHERE attempt_id = $attempt_id;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCorrelation(reader, 0)
            : null;
    }

    private static async ValueTask<PrintObservationRecord?> FindObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid observationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {ObservationSelect}
            WHERE observation_id = $observation_id;
            """;
        command.Parameters.AddWithValue("$observation_id", FormatId(observationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadObservation(reader)
            : null;
    }

    private static async ValueTask<PrintQueueResumeCommandRecord?> FindResumeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {ResumeSelect}
            WHERE command_id = $command_id;
            """;
        command.Parameters.AddWithValue("$command_id", FormatId(commandId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadResume(reader)
            : null;
    }

    private static void AddCorrelationParameters(
        SqliteCommand command,
        PrintQueueCorrelationRecord correlation)
    {
        command.Parameters.AddWithValue("$attempt_id", FormatId(correlation.AttemptId));
        command.Parameters.AddWithValue(
            "$monitor_generation",
            FormatId(correlation.Job.MonitorGeneration));
        command.Parameters.AddWithValue("$printer_name", correlation.Job.Queue.PrinterName);
        command.Parameters.AddWithValue("$server_name", DatabaseText(correlation.Job.Queue.ServerName));
        command.Parameters.AddWithValue("$job_id", correlation.Job.JobId);
        command.Parameters.AddWithValue("$document_name", correlation.Job.DocumentName);
        command.Parameters.AddWithValue(
            "$submitted_at_utc",
            FormatTimestamp(correlation.Job.SubmittedAt));
        command.Parameters.AddWithValue(
            "$correlated_at_utc",
            FormatTimestamp(correlation.CorrelatedAt));
    }

    private static void AddObservationParameters(SqliteCommand command, PrintObservation observation)
    {
        command.Parameters.AddWithValue("$observation_id", FormatId(observation.ObservationId));
        command.Parameters.AddWithValue("$attempt_id", FormatId(observation.AttemptId));
        command.Parameters.AddWithValue("$source", observation.Source.ToString());
        command.Parameters.AddWithValue("$code", observation.Code);
        command.Parameters.AddWithValue("$detail", DatabaseText(observation.Detail));
        command.Parameters.AddWithValue("$raw_job_status", DatabaseStatus(observation.RawJobStatus));
        command.Parameters.AddWithValue(
            "$raw_printer_status",
            DatabaseStatus(observation.RawPrinterStatus));
        command.Parameters.AddWithValue(
            "$raw_provider_status",
            observation.RawProviderStatus is int status ? status : DBNull.Value);
        command.Parameters.AddWithValue(
            "$monitor_generation",
            observation.MonitorGeneration is Guid generation ? FormatId(generation) : DBNull.Value);
        command.Parameters.AddWithValue("$observed_at_utc", FormatTimestamp(observation.ObservedAt));
        command.Parameters.AddWithValue("$received_at_utc", FormatTimestamp(observation.ReceivedAt));
    }

    private static void AddResumeRequestParameters(
        SqliteCommand command,
        Guid commandId,
        Guid attemptId,
        PrintQueueJobReference job,
        DateTimeOffset requestedAt)
    {
        command.Parameters.AddWithValue("$command_id", FormatId(commandId));
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        command.Parameters.AddWithValue("$monitor_generation", FormatId(job.MonitorGeneration));
        command.Parameters.AddWithValue("$printer_name", job.Queue.PrinterName);
        command.Parameters.AddWithValue("$server_name", DatabaseText(job.Queue.ServerName));
        command.Parameters.AddWithValue("$job_id", job.JobId);
        command.Parameters.AddWithValue("$document_name", job.DocumentName);
        command.Parameters.AddWithValue("$submitted_at_utc", FormatTimestamp(job.SubmittedAt));
        command.Parameters.AddWithValue("$requested_at_utc", FormatTimestamp(requestedAt));
    }

    private static PrintQueueCorrelationRecord ReadCorrelation(SqliteDataReader reader, int offset)
    {
        var queue = new PrintQueueIdentity(
            reader.GetString(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3));
        var job = new PrintQueueJobReference(
            ParseId(reader.GetString(offset + 1)),
            queue,
            checked((uint)reader.GetInt64(offset + 4)),
            reader.GetString(offset + 5),
            ParseTimestamp(reader.GetString(offset + 6)));
        return new PrintQueueCorrelationRecord(
            ParseId(reader.GetString(offset)),
            job,
            ParseTimestamp(reader.GetString(offset + 7)));
    }

    private static PrintObservationRecord ReadObservation(SqliteDataReader reader)
    {
        var observation = new PrintObservation(
            ParseId(reader.GetString(1)),
            ParseId(reader.GetString(2)),
            ParseObservationSource(reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ReadStatus(reader, 6),
            ReadStatus(reader, 7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : ParseId(reader.GetString(9)),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)));
        return new PrintObservationRecord(reader.GetInt64(0), observation);
    }

    private static PrintQueueResumeCommandRecord ReadResume(SqliteDataReader reader)
    {
        var queue = new PrintQueueIdentity(
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
        var job = new PrintQueueJobReference(
            ParseId(reader.GetString(2)),
            queue,
            checked((uint)reader.GetInt64(5)),
            reader.GetString(6),
            ParseTimestamp(reader.GetString(7)));
        PrintQueueResumeResult? result = reader.IsDBNull(9)
            ? null
            : new PrintQueueResumeResult(
                ParseResumeOutcome(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10));
        return new PrintQueueResumeCommandRecord(
            ParseId(reader.GetString(0)),
            ParseId(reader.GetString(1)),
            job,
            ParseTimestamp(reader.GetString(8)),
            result,
            reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)));
    }

    private static PrintQueueCorrelationRecord Normalize(PrintQueueCorrelationRecord correlation) =>
        correlation with
        {
            Job = Normalize(correlation.Job),
            CorrelatedAt = correlation.CorrelatedAt.ToUniversalTime(),
        };

    private static PrintQueueJobReference Normalize(PrintQueueJobReference job) =>
        new(
            job.MonitorGeneration,
            job.Queue,
            job.JobId,
            job.DocumentName,
            job.SubmittedAt);

    private static PrintObservation ValidateAndNormalize(PrintObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateCommandId(observation.ObservationId, nameof(observation.ObservationId));
        ValidateAttemptId(observation.AttemptId);
        if (!Enum.IsDefined(observation.Source))
        {
            throw new ArgumentOutOfRangeException(nameof(observation));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Code);
        if (observation.MonitorGeneration == Guid.Empty)
        {
            throw new ArgumentException(
                "Monitor generation cannot be empty when supplied.",
                nameof(observation));
        }

        return observation with
        {
            ObservedAt = observation.ObservedAt.ToUniversalTime(),
            ReceivedAt = observation.ReceivedAt.ToUniversalTime(),
        };
    }

    private static bool ResumeRequestMatches(
        PrintQueueResumeCommandRecord existing,
        Guid attemptId,
        PrintQueueJobReference job,
        DateTimeOffset requestedAt) =>
        existing.AttemptId == attemptId &&
        existing.Job == job &&
        existing.RequestedAt == requestedAt;

    private static bool QueueJobIdentityMatches(
        PrintQueueJobReference first,
        PrintQueueJobReference second) =>
        first.JobId == second.JobId &&
        first.SubmittedAt == second.SubmittedAt &&
        string.Equals(first.DocumentName, second.DocumentName, StringComparison.Ordinal) &&
        string.Equals(
            first.Queue.PrinterName,
            second.Queue.PrinterName,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            first.Queue.ServerName,
            second.Queue.ServerName,
            StringComparison.OrdinalIgnoreCase);

    private static PrintObservationSource ParseObservationSource(string value) =>
        Enum.TryParse<PrintObservationSource>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Print journal contains invalid observation source '{value}'.");

    private static PrintQueueResumeOutcome ParseResumeOutcome(string value) =>
        Enum.TryParse<PrintQueueResumeOutcome>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Print journal contains invalid Resume outcome '{value}'.");

    private static uint? ReadStatus(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : checked((uint)reader.GetInt64(ordinal));

    private static object DatabaseText(string? value) => (object?)value ?? DBNull.Value;

    private static object DatabaseStatus(uint? value) =>
        value is uint status ? (long)status : DBNull.Value;

    private static void ValidateCommandId(Guid commandId, string parameterName = "commandId")
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("ID cannot be empty.", parameterName);
        }
    }
}