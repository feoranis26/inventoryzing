using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<IReadOnlyList<Guid>> ReadPendingReportAttemptIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt.attempt_id
            FROM print_attempts attempt
            JOIN print_dispatch_intents intent ON intent.attempt_id = attempt.attempt_id
            LEFT JOIN print_report_acknowledgements acknowledgement
                ON acknowledgement.attempt_id = attempt.attempt_id
            WHERE attempt.state <> 'Created'
              AND (acknowledgement.reported_version IS NULL
               OR acknowledgement.reported_version < attempt.version)
            ORDER BY attempt.created_at_utc, attempt.attempt_id;
            """;
        List<Guid> attempts = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(ParseId(reader.GetString(0)));
        }
        return attempts.AsReadOnly();
    }

    public async ValueTask MarkReportAcknowledgedAsync(
        Guid attemptId,
        long version,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO print_report_acknowledgements(
                attempt_id, reported_version, acknowledged_at_utc)
            VALUES ($attempt_id, $version, $acknowledged_at_utc)
            ON CONFLICT(attempt_id) DO UPDATE SET
                reported_version = MAX(reported_version, excluded.reported_version),
                acknowledged_at_utc = CASE
                    WHEN excluded.reported_version >= reported_version
                    THEN excluded.acknowledged_at_utc ELSE acknowledged_at_utc END;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$acknowledged_at_utc",
            FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
