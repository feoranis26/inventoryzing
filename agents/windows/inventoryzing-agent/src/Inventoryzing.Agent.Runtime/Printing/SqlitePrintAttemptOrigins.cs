using Inventoryzing.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<DerivedPrintAttempt> CreateDerivedAttemptAsync(
        PrintAttemptOriginRecord origin,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(origin);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var byCommand = await FindAttemptOriginByCommandAsync(
            connection,
            transaction,
            normalized.CommandId,
            cancellationToken).ConfigureAwait(false);
        if (byCommand is not null)
        {
            if (byCommand != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print derivation command '{origin.CommandId}' was replayed with different data.");
            }

            var replayed = await FindAsync(
                connection,
                transaction,
                normalized.AttemptId,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException(
                    $"Derived print attempt '{normalized.AttemptId}' is missing.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DerivedPrintAttempt(replayed, byCommand);
        }

        if (await FindAttemptOriginAsync(
            connection,
            transaction,
            normalized.AttemptId,
            cancellationToken).ConfigureAwait(false) is not null ||
            await FindAsync(
                connection,
                transaction,
                normalized.AttemptId,
                cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new PrintAttemptJournalConflictException(
                $"Derived print attempt ID '{normalized.AttemptId}' is already in use.");
        }

        var source = await FindAsync(
            connection,
            transaction,
            normalized.SourceAttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Source print attempt '{normalized.SourceAttemptId}' was not found.");
        _ = await FindDispatchIntentAsync(
            connection,
            transaction,
            normalized.SourceAttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptJournalConflictException(
                $"Source print attempt '{normalized.SourceAttemptId}' has no dispatch intent to reuse.");
        var sourceResult = await FindDispatchResultAsync(
            connection,
            transaction,
            normalized.SourceAttemptId,
            cancellationToken).ConfigureAwait(false);
        ValidateOriginAgainstSource(normalized, source, sourceResult);

        var status = new PrintAttemptStatus(PrintAttemptState.Created);
        await InsertAttemptAsync(
            connection,
            transaction,
            normalized.AttemptId,
            status,
            normalized.RequestedAt,
            cancellationToken).ConfigureAwait(false);
        await InsertCreationTransitionAsync(
            connection,
            transaction,
            normalized.AttemptId,
            status,
            normalized.RequestedAt,
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_attempt_origins (
                command_id,
                attempt_id,
                source_attempt_id,
                kind,
                requested_by,
                duplicate_risk_acknowledged,
                requested_at_utc)
            VALUES (
                $command_id,
                $attempt_id,
                $source_attempt_id,
                $kind,
                $requested_by,
                $duplicate_risk_acknowledged,
                $requested_at_utc);
            """;
        AddOriginParameters(command, normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DerivedPrintAttempt(
            new PrintAttemptRecord(
                normalized.AttemptId,
                status,
                0,
                normalized.RequestedAt,
                normalized.RequestedAt),
            normalized);
    }

    public async ValueTask<PrintAttemptOriginRecord?> FindAttemptOriginAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindAttemptOriginAsync(
            connection,
            null,
            attemptId,
            cancellationToken).ConfigureAwait(false);
    }

    private const string OriginSelect = """
        SELECT
            command_id,
            attempt_id,
            source_attempt_id,
            kind,
            requested_by,
            duplicate_risk_acknowledged,
            requested_at_utc
        FROM print_attempt_origins
        """;

    private static async ValueTask<PrintAttemptOriginRecord?> FindAttemptOriginAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {OriginSelect}
            WHERE attempt_id = $attempt_id;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOrigin(reader)
            : null;
    }

    private static async ValueTask<PrintAttemptOriginRecord?> FindAttemptOriginByCommandAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {OriginSelect}
            WHERE command_id = $command_id;
            """;
        command.Parameters.AddWithValue("$command_id", FormatId(commandId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOrigin(reader)
            : null;
    }

    private static void AddOriginParameters(
        SqliteCommand command,
        PrintAttemptOriginRecord origin)
    {
        command.Parameters.AddWithValue("$command_id", FormatId(origin.CommandId));
        command.Parameters.AddWithValue("$attempt_id", FormatId(origin.AttemptId));
        command.Parameters.AddWithValue("$source_attempt_id", FormatId(origin.SourceAttemptId));
        command.Parameters.AddWithValue("$kind", origin.Kind.ToString());
        command.Parameters.AddWithValue("$requested_by", origin.RequestedBy);
        command.Parameters.AddWithValue(
            "$duplicate_risk_acknowledged",
            origin.DuplicateRiskAcknowledged ? 1 : 0);
        command.Parameters.AddWithValue("$requested_at_utc", FormatTimestamp(origin.RequestedAt));
    }

    private static PrintAttemptOriginRecord ReadOrigin(SqliteDataReader reader) =>
        new(
            ParseId(reader.GetString(0)),
            ParseId(reader.GetString(1)),
            ParseId(reader.GetString(2)),
            ParseOriginKind(reader.GetString(3)),
            reader.GetString(4),
            reader.GetBoolean(5),
            ParseTimestamp(reader.GetString(6)));

    private static PrintAttemptOriginRecord ValidateAndNormalize(PrintAttemptOriginRecord origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ValidateCommandId(origin.CommandId);
        ValidateAttemptId(origin.AttemptId);
        ValidateAttemptId(origin.SourceAttemptId);
        if (origin.AttemptId == origin.SourceAttemptId)
        {
            throw new ArgumentException("A derived attempt must have a new attempt ID.", nameof(origin));
        }
        if (!Enum.IsDefined(origin.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(origin.RequestedBy);
        if (origin.Kind == PrintAttemptOriginKind.Retry && origin.DuplicateRiskAcknowledged)
        {
            throw new ArgumentException("Retry must not claim duplicate-output risk.", nameof(origin));
        }
        if (origin.Kind == PrintAttemptOriginKind.Reprint && !origin.DuplicateRiskAcknowledged)
        {
            throw new ArgumentException(
                "Reprint requires explicit duplicate-output risk acknowledgement.",
                nameof(origin));
        }

        return origin with { RequestedAt = origin.RequestedAt.ToUniversalTime() };
    }

    private static void ValidateOriginAgainstSource(
        PrintAttemptOriginRecord origin,
        PrintAttemptRecord source,
        PrintDispatchResultRecord? sourceResult)
    {
        if (origin.Kind == PrintAttemptOriginKind.Retry)
        {
            if (source.Status.State != PrintAttemptState.Rejected ||
                sourceResult?.Submission.Status != PrintSubmissionStatus.Rejected)
            {
                throw new PrintAttemptJournalConflictException(
                    "Retry requires durable proof that the source attempt was rejected before output.");
            }
            return;
        }

        if (source.Status.State is not (
            PrintAttemptState.Completed or
            PrintAttemptState.Failed or
            PrintAttemptState.Unknown))
        {
            throw new PrintAttemptJournalConflictException(
                "Reprint requires a terminal completed, failed, or unknown source attempt.");
        }
    }

    private static PrintAttemptOriginKind ParseOriginKind(string value) =>
        Enum.TryParse<PrintAttemptOriginKind>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Print journal contains invalid attempt origin kind '{value}'.");
}