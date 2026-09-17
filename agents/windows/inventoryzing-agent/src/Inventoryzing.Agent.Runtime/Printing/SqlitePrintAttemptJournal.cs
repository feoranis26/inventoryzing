using System.Globalization;
using Inventoryzing.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed partial class SqlitePrintAttemptJournal : IPrintAttemptJournal, IPrinterStatusJournal
{
    private const int SchemaVersion = 9;
    private const int BusyTimeoutMilliseconds = 5_000;

    private readonly string connectionString;

    public SqlitePrintAttemptJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("Journal database path must be fully qualified.", nameof(databasePath));
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL;";
            var result = await journalMode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToString(result, CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SQLite did not enable WAL journal mode.");
            }
        }

        var currentVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (currentVersion > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Print journal schema version {currentVersion} is newer than supported version {SchemaVersion}.");
        }
        if (currentVersion < 1)
        {
            await ApplyVersionOneAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 1;
        }
        if (currentVersion < 2)
        {
            await ApplyVersionTwoAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 2;
        }
        if (currentVersion < 3)
        {
            await ApplyVersionThreeAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 3;
        }
        if (currentVersion < 4)
        {
            await ApplyVersionFourAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 4;
        }
        if (currentVersion < 5)
        {
            await ApplyVersionFiveAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 5;
        }
        if (currentVersion < 6)
        {
            await ApplyVersionSixAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 6;
        }
        if (currentVersion < 7)
        {
            await ApplyVersionSevenAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 7;
        }
        if (currentVersion < 8)
        {
            await ApplyVersionEightAsync(connection, cancellationToken).ConfigureAwait(false);
            currentVersion = 8;
        }
        if (currentVersion < 9)
        {
            await ApplyVersionNineAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask ApplyVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_attempts (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                state TEXT NOT NULL,
                completion_evidence TEXT NULL,
                version INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                CHECK (version >= 0),
                CHECK (
                    (state = 'Completed' AND completion_evidence IN (
                        'SpoolerConfirmed',
                        'BrotherMonitorConfirmed',
                        'DeviceConfirmed')) OR
                    (state <> 'Completed' AND completion_evidence IS NULL))
            );

            CREATE TABLE print_attempt_transitions (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                transition_id TEXT NOT NULL UNIQUE,
                attempt_id TEXT NOT NULL,
                previous_state TEXT NULL,
                state TEXT NOT NULL,
                completion_evidence TEXT NULL,
                resulting_version INTEGER NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id),
                UNIQUE (attempt_id, resulting_version),
                CHECK (resulting_version >= 0),
                CHECK (
                    (state = 'Completed' AND completion_evidence IN (
                        'SpoolerConfirmed',
                        'BrotherMonitorConfirmed',
                        'DeviceConfirmed')) OR
                    (state <> 'Completed' AND completion_evidence IS NULL))
            );

            CREATE INDEX ix_print_attempt_transitions_attempt_sequence
                ON print_attempt_transitions(attempt_id, sequence);

            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionTwoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_queue_correlations (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                monitor_generation TEXT NOT NULL,
                printer_name TEXT NOT NULL,
                server_name TEXT NULL,
                job_id INTEGER NOT NULL,
                document_name TEXT NOT NULL,
                submitted_at_utc TEXT NOT NULL,
                correlated_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id),
                CHECK (job_id > 0 AND job_id <= 4294967295)
            );

            CREATE TABLE print_observations (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                observation_id TEXT NOT NULL UNIQUE,
                attempt_id TEXT NOT NULL,
                source TEXT NOT NULL,
                code TEXT NOT NULL,
                detail TEXT NULL,
                raw_job_status INTEGER NULL,
                raw_printer_status INTEGER NULL,
                monitor_generation TEXT NULL,
                observed_at_utc TEXT NOT NULL,
                received_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id),
                CHECK (raw_job_status IS NULL OR
                       (raw_job_status >= 0 AND raw_job_status <= 4294967295)),
                CHECK (raw_printer_status IS NULL OR
                       (raw_printer_status >= 0 AND raw_printer_status <= 4294967295))
            );

            CREATE INDEX ix_print_observations_attempt_sequence
                ON print_observations(attempt_id, sequence);

            CREATE TABLE print_queue_resume_commands (
                command_id TEXT NOT NULL PRIMARY KEY,
                attempt_id TEXT NOT NULL,
                monitor_generation TEXT NOT NULL,
                printer_name TEXT NOT NULL,
                server_name TEXT NULL,
                job_id INTEGER NOT NULL,
                document_name TEXT NOT NULL,
                submitted_at_utc TEXT NOT NULL,
                requested_at_utc TEXT NOT NULL,
                outcome TEXT NULL,
                result_detail TEXT NULL,
                completed_at_utc TEXT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id),
                CHECK (job_id > 0 AND job_id <= 4294967295),
                CHECK (
                    (outcome IS NULL AND result_detail IS NULL AND completed_at_utc IS NULL) OR
                    (outcome IS NOT NULL AND completed_at_utc IS NOT NULL))
            );

            CREATE INDEX ix_print_queue_resume_commands_attempt_requested
                ON print_queue_resume_commands(attempt_id, requested_at_utc);

            PRAGMA user_version=2;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionThreeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE print_observations
                ADD COLUMN raw_provider_status INTEGER NULL
                CHECK (raw_provider_status IS NULL OR
                       (raw_provider_status >= -2147483648 AND raw_provider_status <= 2147483647));

            PRAGMA user_version=3;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionFourAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_dispatch_intents (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                printer_id TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id),
                CHECK (length(printer_id) > 0),
                CHECK (length(artifact_sha256) = 64 AND
                       artifact_sha256 NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(request_fingerprint) = 64 AND
                       request_fingerprint NOT GLOB '*[^0-9a-f]*')
            );

            CREATE TABLE print_dispatch_results (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                spool_job_id TEXT NULL,
                detail TEXT NULL,
                recorded_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_dispatch_intents(attempt_id),
                CHECK (status IN ('Submitted', 'Rejected', 'Unknown'))
            );

            PRAGMA user_version=4;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionFiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_attempt_origins (
                command_id TEXT NOT NULL PRIMARY KEY,
                attempt_id TEXT NOT NULL UNIQUE,
                source_attempt_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                requested_by TEXT NOT NULL,
                duplicate_risk_acknowledged INTEGER NOT NULL,
                requested_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id),
                FOREIGN KEY (source_attempt_id) REFERENCES print_attempts(attempt_id),
                CHECK (attempt_id <> source_attempt_id),
                CHECK (kind IN ('Retry', 'Reprint')),
                CHECK (length(requested_by) > 0),
                CHECK (duplicate_risk_acknowledged IN (0, 1)),
                CHECK ((kind = 'Retry' AND duplicate_risk_acknowledged = 0) OR
                       (kind = 'Reprint' AND duplicate_risk_acknowledged = 1))
            );

            CREATE INDEX ix_print_attempt_origins_source
                ON print_attempt_origins(source_attempt_id, requested_at_utc);

            PRAGMA user_version=5;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionSixAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE printer_status_observations (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                observation_id TEXT NOT NULL UNIQUE,
                device_id TEXT NOT NULL,
                printer_name TEXT NOT NULL,
                is_supported INTEGER NOT NULL,
                is_online INTEGER NOT NULL,
                loaded_media_name TEXT NULL,
                loaded_media_id INTEGER NULL,
                raw_provider_status INTEGER NULL,
                error_detail TEXT NULL,
                completion_capability TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                received_at_utc TEXT NOT NULL,
                CHECK (length(device_id) > 0),
                CHECK (length(printer_name) > 0),
                CHECK (is_supported IN (0, 1)),
                CHECK (is_online IN (0, 1)),
                CHECK (raw_provider_status IS NULL OR
                       (raw_provider_status >= -2147483648 AND raw_provider_status <= 2147483647)),
                CHECK (completion_capability IN ('Unknown', 'Unsupported', 'PageCompletion'))
            );

            CREATE INDEX ix_printer_status_observations_device_sequence
                ON printer_status_observations(device_id, sequence);

            CREATE TABLE printer_profile_status_observations (
                observation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                profile_id TEXT NOT NULL,
                readiness TEXT NOT NULL,
                expected_media TEXT NOT NULL,
                loaded_media_name TEXT NULL,
                loaded_media_id INTEGER NULL,
                raw_provider_status INTEGER NULL,
                detail TEXT NULL,
                PRIMARY KEY (observation_id, ordinal),
                FOREIGN KEY (observation_id)
                    REFERENCES printer_status_observations(observation_id) ON DELETE CASCADE,
                CHECK (ordinal >= 0),
                CHECK (length(profile_id) > 0),
                CHECK (length(expected_media) > 0),
                CHECK (readiness IN (
                    'Ready',
                    'PrinterUnsupported',
                    'Offline',
                    'PrinterError',
                    'MediaUnsupported',
                    'MediaMismatch',
                    'ProbeError')),
                CHECK (raw_provider_status IS NULL OR
                       (raw_provider_status >= -2147483648 AND raw_provider_status <= 2147483647))
            );

            PRAGMA user_version=6;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionSevenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_dispatch_slots (
                printer_id TEXT NOT NULL PRIMARY KEY,
                attempt_id TEXT NOT NULL UNIQUE,
                acquired_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_dispatch_intents(attempt_id),
                CHECK (length(printer_id) > 0)
            );

            CREATE TRIGGER release_terminal_print_dispatch_slot
            AFTER UPDATE OF state ON print_attempts
            WHEN NEW.state IN ('Completed', 'Rejected', 'Failed', 'Unknown')
            BEGIN
                DELETE FROM print_dispatch_slots WHERE attempt_id = NEW.attempt_id;
            END;

            PRAGMA user_version=7;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionEightAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_dispatch_control_commands (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                command_id TEXT NOT NULL UNIQUE,
                printer_id TEXT NOT NULL,
                is_held INTEGER NOT NULL,
                reason TEXT NULL,
                requested_by TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                CHECK (length(printer_id) > 0),
                CHECK (is_held IN (0, 1)),
                CHECK (length(requested_by) > 0)
            );

            CREATE INDEX ix_print_dispatch_control_printer_sequence
                ON print_dispatch_control_commands(printer_id, sequence DESC);

            PRAGMA user_version=8;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyVersionNineAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE print_report_acknowledgements (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                reported_version INTEGER NOT NULL CHECK (reported_version >= 0),
                acknowledged_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES print_attempts(attempt_id)
            );

            PRAGMA user_version=9;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrintAttemptRecord> CreateAsync(
        Guid attemptId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        var normalizedCreatedAt = createdAt.ToUniversalTime();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindAsync(connection, transaction, attemptId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.CreatedAt != normalizedCreatedAt)
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print attempt '{attemptId}' was replayed with a different creation time.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var status = new PrintAttemptStatus(PrintAttemptState.Created);
        await InsertAttemptAsync(
            connection,
            transaction,
            attemptId,
            status,
            normalizedCreatedAt,
            cancellationToken).ConfigureAwait(false);
        await InsertCreationTransitionAsync(
            connection,
            transaction,
            attemptId,
            status,
            normalizedCreatedAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrintAttemptRecord(
            attemptId,
            status,
            0,
            normalizedCreatedAt,
            normalizedCreatedAt);
    }

    public async ValueTask<PrintAttemptTransitionRecord> TransitionAsync(
        Guid attemptId,
        Guid transitionId,
        PrintAttemptState expectedState,
        long expectedVersion,
        PrintAttemptState nextState,
        PrintCompletionEvidence? completionEvidence,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        ValidateTransitionId(transitionId);
        ValidateExpectedVersion(expectedVersion);
        ValidateState(expectedState, nameof(expectedState));
        _ = new PrintAttemptStatus(nextState, completionEvidence);
        var normalizedOccurredAt = occurredAt.ToUniversalTime();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var replay = await FindTransitionAsync(
            connection,
            transaction,
            transitionId,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!TransitionMatches(
                replay,
                attemptId,
                expectedState,
                expectedVersion,
                nextState,
                completionEvidence,
                normalizedOccurredAt))
            {
                throw new PrintAttemptJournalConflictException(
                    $"Print transition '{transitionId}' was replayed with a different payload.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        var current = await FindAsync(
            connection,
            transaction,
            attemptId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");
        if (current.StateDoesNotMatch(expectedState, expectedVersion))
        {
            throw new PrintAttemptJournalConflictException(
                $"Print attempt '{attemptId}' expected state '{expectedState}' at version " +
                $"{expectedVersion}, but is '{current.Status.State}' at version {current.Version}.");
        }

        var nextStatus = PrintAttemptStateMachine.Transition(
            current.Status,
            nextState,
            completionEvidence);
        var resultingVersion = checked(current.Version + 1);
        var updated = await UpdateAttemptAsync(
            connection,
            transaction,
            current,
            nextStatus,
            resultingVersion,
            normalizedOccurredAt,
            cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            throw new PrintAttemptJournalConflictException(
                $"Print attempt '{attemptId}' changed while transition '{transitionId}' was being stored.");
        }

        var sequence = await InsertTransitionAsync(
            connection,
            transaction,
            transitionId,
            attemptId,
            current.Status.State,
            nextStatus,
            resultingVersion,
            normalizedOccurredAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrintAttemptTransitionRecord(
            sequence,
            transitionId,
            attemptId,
            current.Status.State,
            nextStatus,
            resultingVersion,
            normalizedOccurredAt);
    }

    public async ValueTask<PrintAttemptRecord?> FindAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(connection, null, attemptId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<PrintAttemptTransitionRecord>> ReadTransitionsAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                sequence,
                transition_id,
                attempt_id,
                previous_state,
                state,
                completion_evidence,
                resulting_version,
                occurred_at_utc
            FROM print_attempt_transitions
            WHERE attempt_id = $attempt_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        List<PrintAttemptTransitionRecord> transitions = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(ReadTransition(reader));
        }

        return transitions;
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA foreign_keys=ON;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout={BusyTimeoutMilliseconds};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask InsertAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid attemptId,
        PrintAttemptStatus status,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_attempts (
                attempt_id,
                state,
                completion_evidence,
                version,
                created_at_utc,
                updated_at_utc)
            VALUES ($attempt_id, $state, NULL, 0, $created_at_utc, $created_at_utc);
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        command.Parameters.AddWithValue("$state", status.State.ToString());
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertCreationTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid attemptId,
        PrintAttemptStatus status,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_attempt_transitions (
                transition_id,
                attempt_id,
                previous_state,
                state,
                completion_evidence,
                resulting_version,
                occurred_at_utc)
            VALUES ($transition_id, $attempt_id, NULL, $state, NULL, 0, $occurred_at_utc);
            """;
        command.Parameters.AddWithValue("$transition_id", FormatId(attemptId));
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        command.Parameters.AddWithValue("$state", status.State.ToString());
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> UpdateAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PrintAttemptRecord current,
        PrintAttemptStatus nextStatus,
        long resultingVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE print_attempts
            SET
                state = $next_state,
                completion_evidence = $next_completion_evidence,
                version = $resulting_version,
                updated_at_utc = $updated_at_utc
            WHERE
                attempt_id = $attempt_id AND
                state = $current_state AND
                version = $current_version AND
                (($current_completion_evidence IS NULL AND completion_evidence IS NULL) OR
                 completion_evidence = $current_completion_evidence);
            """;
        command.Parameters.AddWithValue("$next_state", nextStatus.State.ToString());
        command.Parameters.AddWithValue(
            "$next_completion_evidence",
            DatabaseValue(nextStatus.CompletionEvidence));
        command.Parameters.AddWithValue("$resulting_version", resultingVersion);
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(occurredAt));
        command.Parameters.AddWithValue("$attempt_id", FormatId(current.AttemptId));
        command.Parameters.AddWithValue("$current_state", current.Status.State.ToString());
        command.Parameters.AddWithValue("$current_version", current.Version);
        command.Parameters.AddWithValue(
            "$current_completion_evidence",
            DatabaseValue(current.Status.CompletionEvidence));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<long> InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid transitionId,
        Guid attemptId,
        PrintAttemptState previousState,
        PrintAttemptStatus status,
        long resultingVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO print_attempt_transitions (
                transition_id,
                attempt_id,
                previous_state,
                state,
                completion_evidence,
                resulting_version,
                occurred_at_utc)
            VALUES (
                $transition_id,
                $attempt_id,
                $previous_state,
                $state,
                $completion_evidence,
                $resulting_version,
                $occurred_at_utc)
            RETURNING sequence;
            """;
        command.Parameters.AddWithValue("$transition_id", FormatId(transitionId));
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        command.Parameters.AddWithValue("$previous_state", previousState.ToString());
        command.Parameters.AddWithValue("$state", status.State.ToString());
        command.Parameters.AddWithValue(
            "$completion_evidence",
            DatabaseValue(status.CompletionEvidence));
        command.Parameters.AddWithValue("$resulting_version", resultingVersion);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(occurredAt));
        var sequence = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(sequence, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<PrintAttemptRecord?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                attempt_id,
                state,
                completion_evidence,
                version,
                created_at_utc,
                updated_at_utc
            FROM print_attempts
            WHERE attempt_id = $attempt_id;
            """;
        command.Parameters.AddWithValue("$attempt_id", FormatId(attemptId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAttempt(reader)
            : null;
    }

    private static async ValueTask<PrintAttemptTransitionRecord?> FindTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid transitionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                sequence,
                transition_id,
                attempt_id,
                previous_state,
                state,
                completion_evidence,
                resulting_version,
                occurred_at_utc
            FROM print_attempt_transitions
            WHERE transition_id = $transition_id;
            """;
        command.Parameters.AddWithValue("$transition_id", FormatId(transitionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTransition(reader)
            : null;
    }

    private static bool TransitionMatches(
        PrintAttemptTransitionRecord transition,
        Guid attemptId,
        PrintAttemptState expectedState,
        long expectedVersion,
        PrintAttemptState nextState,
        PrintCompletionEvidence? completionEvidence,
        DateTimeOffset occurredAt) =>
        transition.AttemptId == attemptId &&
        transition.PreviousState == expectedState &&
        transition.ResultingVersion == expectedVersion + 1 &&
        transition.Status.State == nextState &&
        transition.Status.CompletionEvidence == completionEvidence &&
        transition.OccurredAt == occurredAt;

    private static PrintAttemptRecord ReadAttempt(SqliteDataReader reader)
    {
        var state = ParseState(reader.GetString(1));
        PrintCompletionEvidence? evidence = reader.IsDBNull(2)
            ? null
            : ParseEvidence(reader.GetString(2));
        return new PrintAttemptRecord(
            ParseId(reader.GetString(0)),
            new PrintAttemptStatus(state, evidence),
            reader.GetInt64(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)));
    }

    private static PrintAttemptTransitionRecord ReadTransition(SqliteDataReader reader)
    {
        var state = ParseState(reader.GetString(4));
        PrintCompletionEvidence? evidence = reader.IsDBNull(5)
            ? null
            : ParseEvidence(reader.GetString(5));
        return new PrintAttemptTransitionRecord(
            reader.GetInt64(0),
            ParseId(reader.GetString(1)),
            ParseId(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseState(reader.GetString(3)),
            new PrintAttemptStatus(state, evidence),
            reader.GetInt64(6),
            ParseTimestamp(reader.GetString(7)));
    }

    private static string FormatId(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private static object DatabaseValue(PrintCompletionEvidence? evidence) =>
        (object?)evidence?.ToString() ?? DBNull.Value;

    private static Guid ParseId(string value) =>
        Guid.TryParseExact(value, "N", out var parsed)
            ? parsed
            : throw new InvalidDataException($"Print journal contains invalid ID '{value}'.");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : throw new InvalidDataException($"Print journal contains invalid timestamp '{value}'.");

    private static PrintAttemptState ParseState(string value) =>
        Enum.TryParse<PrintAttemptState>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"Print journal contains invalid state '{value}'.");

    private static PrintCompletionEvidence ParseEvidence(string value) =>
        Enum.TryParse<PrintCompletionEvidence>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"Print journal contains invalid completion evidence '{value}'.");

    private static void ValidateAttemptId(Guid attemptId)
    {
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Print attempt ID cannot be empty.", nameof(attemptId));
        }
    }

    private static void ValidateTransitionId(Guid transitionId)
    {
        if (transitionId == Guid.Empty)
        {
            throw new ArgumentException("Print transition ID cannot be empty.", nameof(transitionId));
        }
    }

    private static void ValidateExpectedVersion(long expectedVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
    }

    private static void ValidateState(PrintAttemptState state, string parameterName)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal static class PrintAttemptRecordExtensions
{
    public static bool StateDoesNotMatch(
        this PrintAttemptRecord current,
        PrintAttemptState expectedState,
        long expectedVersion) =>
        current.Status.State != expectedState || current.Version != expectedVersion;
}
