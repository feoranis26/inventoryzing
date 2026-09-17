using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<PrintDispatchControlRecord> SetDispatchControlAsync(
        PrintDispatchControlRecord command,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(command);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindDispatchControlCommandAsync(
            connection, transaction, normalized.CommandId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Dispatch control command '{normalized.CommandId}' was replayed with different data.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO print_dispatch_control_commands (
                command_id, printer_id, is_held, reason, requested_by, occurred_at_utc)
            VALUES ($command_id, $printer_id, $is_held, $reason, $requested_by, $occurred_at_utc);
            """;
        AddParameters(insert, normalized);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async ValueTask<PrintDispatchControlRecord?> FindDispatchControlAsync(
        string printerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {ControlSelect}
            WHERE printer_id = $printer_id
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$printer_id", printerId);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PrintDispatchControlRecord?> FindDispatchControlCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {ControlSelect}
            WHERE command_id = $command_id;
            """;
        command.Parameters.AddWithValue("$command_id", FormatId(commandId));
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PrintDispatchControlRecord?> ReadAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PrintDispatchControlRecord(
                ParseId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)))
            : null;
    }

    private static void AddParameters(SqliteCommand sql, PrintDispatchControlRecord command)
    {
        sql.Parameters.AddWithValue("$command_id", FormatId(command.CommandId));
        sql.Parameters.AddWithValue("$printer_id", command.PrinterId);
        sql.Parameters.AddWithValue("$is_held", command.IsHeld);
        sql.Parameters.AddWithValue("$reason", (object?)command.Reason ?? DBNull.Value);
        sql.Parameters.AddWithValue("$requested_by", command.RequestedBy);
        sql.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(command.OccurredAt));
    }

    private static PrintDispatchControlRecord ValidateAndNormalize(PrintDispatchControlRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommandId(command.CommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PrinterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestedBy);
        if (command.IsHeld && string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ArgumentException("An operator hold requires a reason.", nameof(command));
        }

        return command with
        {
            Reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason,
            OccurredAt = command.OccurredAt.ToUniversalTime(),
        };
    }

    private const string ControlSelect = """
        SELECT command_id, printer_id, is_held, reason, requested_by, occurred_at_utc
        FROM print_dispatch_control_commands
        """;
}
