using Inventoryzing.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<PrintDispatchSlotRecord> AcquireDispatchSlotAsync(
        PrintDispatchSlotRecord slot,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(slot);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var attempt = await FindAsync(
            connection,
            transaction,
            normalized.AttemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Print attempt '{normalized.AttemptId}' was not found.");
        if (attempt.Status.State != PrintAttemptState.Dispatching)
        {
            throw new PrintAttemptJournalConflictException(
                "A printer dispatch slot can only be acquired at the durable Dispatching barrier.");
        }

        var existing = await FindDispatchSlotAsync(
            connection,
            transaction,
            normalized.PrinterId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.AttemptId != normalized.AttemptId)
            {
                throw new PrinterDispatchBusyException(
                    normalized.PrinterId,
                    existing.AttemptId);
            }
            if (existing != normalized)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print attempt '{normalized.AttemptId}' replayed a different slot acquisition.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_dispatch_slots (printer_id, attempt_id, acquired_at_utc)
            VALUES ($printer_id, $attempt_id, $acquired_at_utc);
            """;
        command.Parameters.AddWithValue("$printer_id", normalized.PrinterId);
        command.Parameters.AddWithValue("$attempt_id", FormatId(normalized.AttemptId));
        command.Parameters.AddWithValue("$acquired_at_utc", FormatTimestamp(normalized.AcquiredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async ValueTask<PrintDispatchSlotRecord?> FindDispatchSlotAsync(
        string printerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindDispatchSlotAsync(
            connection,
            null,
            printerId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PrintDispatchSlotRecord?> FindDispatchSlotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string printerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT printer_id, attempt_id, acquired_at_utc
            FROM print_dispatch_slots
            WHERE printer_id = $printer_id;
            """;
        command.Parameters.AddWithValue("$printer_id", printerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PrintDispatchSlotRecord(
                reader.GetString(0),
                ParseId(reader.GetString(1)),
                ParseTimestamp(reader.GetString(2)))
            : null;
    }

    private static PrintDispatchSlotRecord ValidateAndNormalize(PrintDispatchSlotRecord slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(slot.PrinterId);
        ValidateAttemptId(slot.AttemptId);
        return slot with { AcquiredAt = slot.AcquiredAt.ToUniversalTime() };
    }
}