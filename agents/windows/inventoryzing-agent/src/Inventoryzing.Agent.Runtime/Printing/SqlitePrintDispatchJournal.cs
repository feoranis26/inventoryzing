using Inventoryzing.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<PrintDispatchIntentRecord> StoreDispatchIntentAsync(
        PrintDispatchIntentRecord intent,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(intent);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindDispatchIntentAsync(
            connection,
            transaction,
            intent.AttemptId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print attempt '{intent.AttemptId}' already has a different dispatch intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        _ = await FindAsync(
            connection,
            transaction,
            intent.AttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Print attempt '{intent.AttemptId}' was not found.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_dispatch_intents (
                attempt_id,
                printer_id,
                artifact_sha256,
                request_fingerprint,
                created_at_utc)
            VALUES (
                $attempt_id,
                $printer_id,
                $artifact_sha256,
                $request_fingerprint,
                $created_at_utc);
            """;
        AddIntentParameters(command, normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async ValueTask<PrintDispatchIntentRecord?> FindDispatchIntentAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindDispatchIntentAsync(
            connection,
            null,
            attemptId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrintDispatchResultRecord> StoreDispatchResultAsync(
        PrintDispatchResultRecord result,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(result);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindDispatchResultAsync(
            connection,
            transaction,
            result.AttemptId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print attempt '{result.AttemptId}' already has a different dispatch result.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        _ = await FindDispatchIntentAsync(
            connection,
            transaction,
            result.AttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Dispatch intent for print attempt '{result.AttemptId}' was not found.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_dispatch_results (
                attempt_id,
                status,
                spool_job_id,
                detail,
                recorded_at_utc)
            VALUES (
                $attempt_id,
                $status,
                $spool_job_id,
                $detail,
                $recorded_at_utc);
            """;
        AddResultParameters(command, normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async ValueTask<PrintDispatchResultRecord?> FindDispatchResultAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindDispatchResultAsync(
            connection,
            null,
            attemptId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RecoverablePrintDispatchAttempt>>
        ReadRecoverableDispatchAttemptsAsync(CancellationToken cancellationToken)
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
                intents.attempt_id,
                intents.printer_id,
                intents.artifact_sha256,
                intents.request_fingerprint,
                intents.created_at_utc,
                results.attempt_id,
                results.status,
                results.spool_job_id,
                results.detail,
                results.recorded_at_utc
            FROM print_attempts AS attempts
            INNER JOIN print_dispatch_intents AS intents
                ON intents.attempt_id = attempts.attempt_id
            LEFT JOIN print_dispatch_results AS results
                ON results.attempt_id = attempts.attempt_id
            LEFT JOIN print_queue_correlations AS correlations
                ON correlations.attempt_id = attempts.attempt_id
            WHERE attempts.state = 'Dispatching' OR
                  (attempts.state = 'DriverAccepted' AND correlations.attempt_id IS NULL)
            ORDER BY attempts.created_at_utc, attempts.attempt_id;
            """;
        List<RecoverablePrintDispatchAttempt> attempts = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(new RecoverablePrintDispatchAttempt(
                ReadAttempt(reader),
                ReadIntent(reader, 6),
                reader.IsDBNull(11) ? null : ReadResult(reader, 11)));
        }

        return attempts;
    }

    private const string IntentSelect = """
        SELECT
            attempt_id,
            printer_id,
            artifact_sha256,
            request_fingerprint,
            created_at_utc
        FROM print_dispatch_intents
        """;

    private const string ResultSelect = """
        SELECT
            attempt_id,
            status,
            spool_job_id,
            detail,
            recorded_at_utc
        FROM print_dispatch_results
        """;

    private static async ValueTask<PrintDispatchIntentRecord?> FindDispatchIntentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {IntentSelect}
            WHERE attempt_id = $attempt_id;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadIntent(reader, 0)
            : null;
    }

    private static async ValueTask<PrintDispatchResultRecord?> FindDispatchResultAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {ResultSelect}
            WHERE attempt_id = $attempt_id;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadResult(reader, 0)
            : null;
    }

    private static void AddIntentParameters(
        SqliteCommand command,
        PrintDispatchIntentRecord intent)
    {
        command.Parameters.AddWithValue("$attempt_id", FormatId(intent.AttemptId));
        command.Parameters.AddWithValue("$printer_id", intent.PrinterId);
        command.Parameters.AddWithValue("$artifact_sha256", intent.ArtifactSha256);
        command.Parameters.AddWithValue("$request_fingerprint", intent.RequestFingerprint);
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(intent.CreatedAt));
    }

    private static void AddResultParameters(
        SqliteCommand command,
        PrintDispatchResultRecord result)
    {
        command.Parameters.AddWithValue("$attempt_id", FormatId(result.AttemptId));
        command.Parameters.AddWithValue("$status", result.Submission.Status.ToString());
        command.Parameters.AddWithValue("$spool_job_id", DatabaseText(result.Submission.SpoolJobId));
        command.Parameters.AddWithValue("$detail", DatabaseText(result.Submission.Detail));
        command.Parameters.AddWithValue("$recorded_at_utc", FormatTimestamp(result.RecordedAt));
    }

    private static PrintDispatchIntentRecord ReadIntent(SqliteDataReader reader, int offset) =>
        new(
            ParseId(reader.GetString(offset)),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            ParseTimestamp(reader.GetString(offset + 4)));

    private static PrintDispatchResultRecord ReadResult(SqliteDataReader reader, int offset) =>
        new(
            ParseId(reader.GetString(offset)),
            new PrintSubmission(
                ParseSubmissionStatus(reader.GetString(offset + 1)),
                reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
                reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3)),
            ParseTimestamp(reader.GetString(offset + 4)));

    private static PrintDispatchIntentRecord ValidateAndNormalize(PrintDispatchIntentRecord intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateAttemptId(intent.AttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.PrinterId);
        ValidateSha256(intent.ArtifactSha256, nameof(intent.ArtifactSha256));
        ValidateSha256(intent.RequestFingerprint, nameof(intent.RequestFingerprint));
        return intent with { CreatedAt = intent.CreatedAt.ToUniversalTime() };
    }

    private static PrintDispatchResultRecord ValidateAndNormalize(PrintDispatchResultRecord result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateAttemptId(result.AttemptId);
        ArgumentNullException.ThrowIfNull(result.Submission);
        if (!Enum.IsDefined(result.Submission.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        return result with { RecordedAt = result.RecordedAt.ToUniversalTime() };
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "SHA-256 values must be 64 lowercase hexadecimal characters.",
                parameterName);
        }
    }

    private static PrintSubmissionStatus ParseSubmissionStatus(string value) =>
        Enum.TryParse<PrintSubmissionStatus>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Print journal contains invalid submission status '{value}'.");
}