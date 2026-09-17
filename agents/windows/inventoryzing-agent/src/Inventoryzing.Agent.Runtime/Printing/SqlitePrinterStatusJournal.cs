using Inventoryzing.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal
{
    public async ValueTask<PrinterStatusObservationRecord> AppendPrinterStatusAsync(
        PrinterStatusObservation observation,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        ValidatePrinterStatus(observation);
        var normalizedReceivedAt = receivedAt.ToUniversalTime();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindPrinterStatusAsync(
            connection,
            transaction,
            observation.ObservationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!StatusEquals(existing.Observation, observation) ||
                existing.ReceivedAt != normalizedReceivedAt)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Printer status observation '{observation.ObservationId}' was replayed with different data.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO printer_status_observations (
                observation_id,
                device_id,
                printer_name,
                is_supported,
                is_online,
                loaded_media_name,
                loaded_media_id,
                raw_provider_status,
                error_detail,
                completion_capability,
                observed_at_utc,
                received_at_utc)
            VALUES (
                $observation_id,
                $device_id,
                $printer_name,
                $is_supported,
                $is_online,
                $loaded_media_name,
                $loaded_media_id,
                $raw_provider_status,
                $error_detail,
                $completion_capability,
                $observed_at_utc,
                $received_at_utc)
            RETURNING sequence;
            """;
        AddPrinterStatusParameters(command, observation, normalizedReceivedAt);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var sequence = Convert.ToInt64(value);
        for (var ordinal = 0; ordinal < observation.Profiles.Count; ordinal++)
        {
            await InsertProfileStatusAsync(
                connection,
                transaction,
                observation.ObservationId,
                ordinal,
                observation.Profiles[ordinal],
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrinterStatusObservationRecord(sequence, observation, normalizedReceivedAt);
    }

    public async ValueTask<IReadOnlyList<PrinterStatusObservationRecord>> ReadPrinterStatusesAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {PrinterStatusSelect}
            WHERE status.device_id = $device_id
            ORDER BY status.sequence, profile.ordinal;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        return await ReadPrinterStatusRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private const string PrinterStatusSelect = """
        SELECT
            status.sequence,
            status.observation_id,
            status.device_id,
            status.printer_name,
            status.is_supported,
            status.is_online,
            status.loaded_media_name,
            status.loaded_media_id,
            status.raw_provider_status,
            status.error_detail,
            status.completion_capability,
            status.observed_at_utc,
            status.received_at_utc,
            profile.ordinal,
            profile.profile_id,
            profile.readiness,
            profile.expected_media,
            profile.loaded_media_name,
            profile.loaded_media_id,
            profile.raw_provider_status,
            profile.detail
        FROM printer_status_observations AS status
        INNER JOIN printer_profile_status_observations AS profile
            ON profile.observation_id = status.observation_id
        """;

    private static async ValueTask<PrinterStatusObservationRecord?> FindPrinterStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid observationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {PrinterStatusSelect}
            WHERE status.observation_id = $observation_id
            ORDER BY profile.ordinal;
            """;
        command.Parameters.AddWithValue("$observation_id", FormatId(observationId));
        return (await ReadPrinterStatusRecordsAsync(command, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();
    }

    private static async ValueTask<IReadOnlyList<PrinterStatusObservationRecord>>
        ReadPrinterStatusRecordsAsync(
            SqliteCommand command,
            CancellationToken cancellationToken)
    {
        List<PrinterStatusObservationRecord> records = [];
        StatusHeader? header = null;
        List<PrinterProfileStatus> profiles = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = reader.GetInt64(0);
            if (header is not null && header.Sequence != sequence)
            {
                records.Add(BuildStatusRecord(header, profiles));
                profiles = [];
            }
            if (header is null || header.Sequence != sequence)
            {
                header = ReadStatusHeader(reader);
            }

            profiles.Add(new PrinterProfileStatus(
                reader.GetString(14),
                ParseReadiness(reader.GetString(15)),
                reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetInt32(19),
                reader.IsDBNull(20) ? null : reader.GetString(20)));
        }
        if (header is not null)
        {
            records.Add(BuildStatusRecord(header, profiles));
        }

        return records;
    }

    private static async ValueTask InsertProfileStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid observationId,
        int ordinal,
        PrinterProfileStatus status,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO printer_profile_status_observations (
                observation_id,
                ordinal,
                profile_id,
                readiness,
                expected_media,
                loaded_media_name,
                loaded_media_id,
                raw_provider_status,
                detail)
            VALUES (
                $observation_id,
                $ordinal,
                $profile_id,
                $readiness,
                $expected_media,
                $loaded_media_name,
                $loaded_media_id,
                $raw_provider_status,
                $detail);
            """;
        command.Parameters.AddWithValue("$observation_id", FormatId(observationId));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$profile_id", status.ProfileId);
        command.Parameters.AddWithValue("$readiness", status.Readiness.ToString());
        command.Parameters.AddWithValue("$expected_media", status.ExpectedMedia);
        command.Parameters.AddWithValue("$loaded_media_name", DatabaseText(status.LoadedMediaName));
        command.Parameters.AddWithValue(
            "$loaded_media_id",
            status.LoadedMediaId is int mediaId ? mediaId : DBNull.Value);
        command.Parameters.AddWithValue(
            "$raw_provider_status",
            status.RawProviderStatus is int rawStatus ? rawStatus : DBNull.Value);
        command.Parameters.AddWithValue("$detail", DatabaseText(status.Detail));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddPrinterStatusParameters(
        SqliteCommand command,
        PrinterStatusObservation observation,
        DateTimeOffset receivedAt)
    {
        command.Parameters.AddWithValue("$observation_id", FormatId(observation.ObservationId));
        command.Parameters.AddWithValue("$device_id", observation.DeviceId);
        command.Parameters.AddWithValue("$printer_name", observation.PrinterName);
        command.Parameters.AddWithValue("$is_supported", observation.IsSupported ? 1 : 0);
        command.Parameters.AddWithValue("$is_online", observation.IsOnline ? 1 : 0);
        command.Parameters.AddWithValue("$loaded_media_name", DatabaseText(observation.LoadedMediaName));
        command.Parameters.AddWithValue(
            "$loaded_media_id",
            observation.LoadedMediaId is int mediaId ? mediaId : DBNull.Value);
        command.Parameters.AddWithValue(
            "$raw_provider_status",
            observation.RawProviderStatus is int rawStatus ? rawStatus : DBNull.Value);
        command.Parameters.AddWithValue("$error_detail", DatabaseText(observation.ErrorDetail));
        command.Parameters.AddWithValue(
            "$completion_capability",
            observation.CompletionCapability.ToString());
        command.Parameters.AddWithValue("$observed_at_utc", FormatTimestamp(observation.ObservedAt));
        command.Parameters.AddWithValue("$received_at_utc", FormatTimestamp(receivedAt));
    }

    private static StatusHeader ReadStatusHeader(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            ParseId(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            ParseCompletionCapability(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)),
            ParseTimestamp(reader.GetString(12)));

    private static PrinterStatusObservationRecord BuildStatusRecord(
        StatusHeader header,
        IReadOnlyList<PrinterProfileStatus> profiles) =>
        new(
            header.Sequence,
            new PrinterStatusObservation(
                header.ObservationId,
                header.DeviceId,
                header.PrinterName,
                header.IsSupported,
                header.IsOnline,
                header.LoadedMediaName,
                header.LoadedMediaId,
                header.RawProviderStatus,
                header.ErrorDetail,
                header.CompletionCapability,
                profiles,
                header.ObservedAt),
            header.ReceivedAt);

    private static void ValidatePrinterStatus(PrinterStatusObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        foreach (var profile in observation.Profiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.ProfileId);
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.ExpectedMedia);
            if (!Enum.IsDefined(profile.Readiness))
            {
                throw new ArgumentOutOfRangeException(nameof(observation));
            }
        }
    }

    private static bool StatusEquals(
        PrinterStatusObservation first,
        PrinterStatusObservation second) =>
        first.ObservationId == second.ObservationId &&
        string.Equals(first.DeviceId, second.DeviceId, StringComparison.Ordinal) &&
        string.Equals(first.PrinterName, second.PrinterName, StringComparison.Ordinal) &&
        first.IsSupported == second.IsSupported &&
        first.IsOnline == second.IsOnline &&
        string.Equals(first.LoadedMediaName, second.LoadedMediaName, StringComparison.Ordinal) &&
        first.LoadedMediaId == second.LoadedMediaId &&
        first.RawProviderStatus == second.RawProviderStatus &&
        string.Equals(first.ErrorDetail, second.ErrorDetail, StringComparison.Ordinal) &&
        first.CompletionCapability == second.CompletionCapability &&
        first.ObservedAt == second.ObservedAt &&
        first.Profiles.SequenceEqual(second.Profiles);

    private static PrinterProfileReadiness ParseReadiness(string value) =>
        Enum.TryParse<PrinterProfileReadiness>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"Invalid printer profile readiness '{value}'.");

    private static PrinterMonitorCompletionCapability ParseCompletionCapability(string value) =>
        Enum.TryParse<PrinterMonitorCompletionCapability>(
            value,
            ignoreCase: false,
            out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Invalid printer monitor completion capability '{value}'.");

    private sealed record StatusHeader(
        long Sequence,
        Guid ObservationId,
        string DeviceId,
        string PrinterName,
        bool IsSupported,
        bool IsOnline,
        string? LoadedMediaName,
        int? LoadedMediaId,
        int? RawProviderStatus,
        string? ErrorDetail,
        PrinterMonitorCompletionCapability CompletionCapability,
        DateTimeOffset ObservedAt,
        DateTimeOffset ReceivedAt);
}