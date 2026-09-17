using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class SqlitePrintAttemptJournalTests : IDisposable
{
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-print-journal-{Guid.NewGuid():N}");

    public SqlitePrintAttemptJournalTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Create_is_idempotent_and_persists_after_reopen()
    {
        var attemptId = Guid.Parse("24c6ab1f-9487-42e8-94cd-13d5983830e3");
        var createdAt = new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.FromHours(2));
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);

        var created = await journal.CreateAsync(attemptId, createdAt, CancellationToken.None);
        var replayed = await journal.CreateAsync(attemptId, createdAt, CancellationToken.None);
        var reopened = CreateJournal();
        await reopened.InitializeAsync(CancellationToken.None);
        var persisted = await reopened.FindAsync(attemptId, CancellationToken.None);
        var transitions = await reopened.ReadTransitionsAsync(attemptId, CancellationToken.None);

        Assert.Equal(created, replayed);
        Assert.Equal(created, persisted);
        Assert.Equal(PrintAttemptState.Created, created.Status.State);
        Assert.Equal(0, created.Version);
        Assert.Equal(createdAt.ToUniversalTime(), created.CreatedAt);
        var transition = Assert.Single(transitions);
        Assert.Equal(attemptId, transition.TransitionId);
        Assert.Null(transition.PreviousState);
        Assert.Equal(PrintAttemptState.Created, transition.Status.State);
        Assert.Equal(0, transition.ResultingVersion);
    }

    [Fact]
    public async Task Initialize_enables_wal_and_applies_known_schema_version()
    {
        var journal = CreateJournal();

        await journal.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(TestConnectionString);
        await connection.OpenAsync();
        Assert.Equal("wal", await ReadPragmaAsync<string>(connection, "journal_mode"));
        Assert.Equal(9L, await ReadPragmaAsync<long>(connection, "user_version"));
    }

    [Fact]
    public async Task Newer_schema_is_rejected_without_modification()
    {
        await using (var connection = new SqliteConnection(TestConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=10;";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateJournal().InitializeAsync(CancellationToken.None).AsTask());

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
        await using var verification = new SqliteConnection(TestConnectionString);
        await verification.OpenAsync();
        Assert.Equal(10L, await ReadPragmaAsync<long>(verification, "user_version"));
    }

    [Fact]
    public async Task Version_one_database_is_upgraded_without_losing_attempts()
    {
        var attemptId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await CreateVersionOneDatabaseAsync(attemptId, createdAt);

        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);

        var attempt = await journal.FindAsync(attemptId, CancellationToken.None);
        Assert.NotNull(attempt);
        Assert.Equal(PrintAttemptState.Created, attempt.Status.State);
        await using var connection = new SqliteConnection(TestConnectionString);
        await connection.OpenAsync();
        Assert.Equal(9L, await ReadPragmaAsync<long>(connection, "user_version"));
        Assert.Equal(
            3L,
            await CountTablesAsync(
                connection,
                "print_queue_correlations",
                "print_observations",
                "print_queue_resume_commands"));
    }

    [Fact]
    public async Task Changed_creation_replay_is_rejected_without_duplicate_history()
    {
        var attemptId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);
        await journal.CreateAsync(attemptId, createdAt, CancellationToken.None);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.CreateAsync(
                attemptId,
                createdAt.AddSeconds(1),
                CancellationToken.None).AsTask());

        Assert.Single(await journal.ReadTransitionsAsync(attemptId, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_attempt_has_no_current_state_or_history()
    {
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);
        var attemptId = Guid.NewGuid();

        Assert.Null(await journal.FindAsync(attemptId, CancellationToken.None));
        Assert.Empty(await journal.ReadTransitionsAsync(attemptId, CancellationToken.None));
    }

    [Fact]
    public async Task Transition_updates_current_state_and_appends_history_atomically()
    {
        var attemptId = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var occurredAt = createdAt.AddSeconds(1);
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);
        await journal.CreateAsync(attemptId, createdAt, CancellationToken.None);

        var transition = await journal.TransitionAsync(
            attemptId,
            transitionId,
            PrintAttemptState.Created,
            0,
            PrintAttemptState.Claimed,
            null,
            occurredAt,
            CancellationToken.None);
        var reopened = CreateJournal();
        await reopened.InitializeAsync(CancellationToken.None);
        var current = await reopened.FindAsync(attemptId, CancellationToken.None);
        var history = await reopened.ReadTransitionsAsync(attemptId, CancellationToken.None);

        Assert.Equal(PrintAttemptState.Created, transition.PreviousState);
        Assert.Equal(PrintAttemptState.Claimed, transition.Status.State);
        Assert.Equal(1, transition.ResultingVersion);
        Assert.Equal(PrintAttemptState.Claimed, current!.Status.State);
        Assert.Equal(1, current.Version);
        Assert.Equal(occurredAt.ToUniversalTime(), current.UpdatedAt);
        Assert.Equal(2, history.Count);
        Assert.Equal([0L, 1L], history.Select(item => item.ResultingVersion));
    }

    [Fact]
    public async Task Illegal_or_stale_transition_leaves_current_state_and_history_unchanged()
    {
        var attemptId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);
        await journal.CreateAsync(attemptId, createdAt, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.TransitionAsync(
                attemptId,
                Guid.NewGuid(),
                PrintAttemptState.Created,
                0,
                PrintAttemptState.Printing,
                null,
                createdAt.AddSeconds(1),
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.TransitionAsync(
                attemptId,
                Guid.NewGuid(),
                PrintAttemptState.Claimed,
                1,
                PrintAttemptState.Staged,
                null,
                createdAt.AddSeconds(2),
                CancellationToken.None).AsTask());

        var current = await journal.FindAsync(attemptId, CancellationToken.None);
        Assert.Equal(PrintAttemptState.Created, current!.Status.State);
        Assert.Equal(0, current.Version);
        Assert.Single(await journal.ReadTransitionsAsync(attemptId, CancellationToken.None));
    }

    [Fact]
    public async Task Exact_transition_replay_is_idempotent_but_changed_payload_conflicts()
    {
        var attemptId = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var occurredAt = createdAt.AddSeconds(1);
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);
        await journal.CreateAsync(attemptId, createdAt, CancellationToken.None);

        var applied = await journal.TransitionAsync(
            attemptId,
            transitionId,
            PrintAttemptState.Created,
            0,
            PrintAttemptState.Claimed,
            null,
            occurredAt,
            CancellationToken.None);
        var replayed = await journal.TransitionAsync(
            attemptId,
            transitionId,
            PrintAttemptState.Created,
            0,
            PrintAttemptState.Claimed,
            null,
            occurredAt,
            CancellationToken.None);
        await journal.TransitionAsync(
            attemptId,
            Guid.NewGuid(),
            PrintAttemptState.Claimed,
            1,
            PrintAttemptState.Staged,
            null,
            occurredAt.AddSeconds(1),
            CancellationToken.None);
        var replayedAfterProgress = await journal.TransitionAsync(
            attemptId,
            transitionId,
            PrintAttemptState.Created,
            0,
            PrintAttemptState.Claimed,
            null,
            occurredAt,
            CancellationToken.None);
        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.TransitionAsync(
                attemptId,
                transitionId,
                PrintAttemptState.Created,
                0,
                PrintAttemptState.Rejected,
                null,
                occurredAt,
                CancellationToken.None).AsTask());

        Assert.Equal(applied, replayed);
        Assert.Equal(applied, replayedAfterProgress);
        Assert.Equal(3, (await journal.ReadTransitionsAsync(attemptId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Concurrent_writers_allow_exactly_one_transition_from_expected_version()
    {
        var attemptId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var firstJournal = CreateJournal();
        var secondJournal = CreateJournal();
        await firstJournal.InitializeAsync(CancellationToken.None);
        await firstJournal.CreateAsync(attemptId, createdAt, CancellationToken.None);

        var outcomes = await Task.WhenAll(
            CaptureAsync(firstJournal.TransitionAsync(
                attemptId,
                Guid.NewGuid(),
                PrintAttemptState.Created,
                0,
                PrintAttemptState.Claimed,
                null,
                createdAt.AddSeconds(1),
                CancellationToken.None)),
            CaptureAsync(secondJournal.TransitionAsync(
                attemptId,
                Guid.NewGuid(),
                PrintAttemptState.Created,
                0,
                PrintAttemptState.Rejected,
                null,
                createdAt.AddSeconds(1),
                CancellationToken.None)));

        Assert.Single(outcomes, outcome => outcome.Error is null);
        Assert.IsType<PrintAttemptJournalConflictException>(
            Assert.Single(outcomes, outcome => outcome.Error is not null).Error);
        var current = await firstJournal.FindAsync(attemptId, CancellationToken.None);
        Assert.Equal(1, current!.Version);
        Assert.Equal(2, (await firstJournal.ReadTransitionsAsync(attemptId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Completion_evidence_and_later_upgrade_are_durable()
    {
        var attemptId = Guid.NewGuid();
        var journal = CreateJournal();
        await journal.InitializeAsync(CancellationToken.None);
        var current = await journal.CreateAsync(attemptId, DateTimeOffset.UtcNow, CancellationToken.None);
        var states = new[]
        {
            PrintAttemptState.Claimed,
            PrintAttemptState.Staged,
            PrintAttemptState.Prepared,
            PrintAttemptState.Dispatching,
            PrintAttemptState.DriverAccepted,
            PrintAttemptState.SpoolerQueued,
        };
        foreach (var state in states)
        {
            var transition = await journal.TransitionAsync(
                attemptId,
                Guid.NewGuid(),
                current.Status.State,
                current.Version,
                state,
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            current = current with
            {
                Status = transition.Status,
                Version = transition.ResultingVersion,
                UpdatedAt = transition.OccurredAt,
            };
        }

        var completed = await journal.TransitionAsync(
            attemptId,
            Guid.NewGuid(),
            current.Status.State,
            current.Version,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.SpoolerConfirmed,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var upgraded = await journal.TransitionAsync(
            attemptId,
            Guid.NewGuid(),
            completed.Status.State,
            completed.ResultingVersion,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.DeviceConfirmed,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(PrintCompletionEvidence.DeviceConfirmed, upgraded.Status.CompletionEvidence);
        var persisted = await journal.FindAsync(attemptId, CancellationToken.None);
        Assert.Equal(PrintCompletionEvidence.DeviceConfirmed, persisted!.Status.CompletionEvidence);
    }

    public void Dispose()
    {
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SqlitePrintAttemptJournal CreateJournal() =>
        new(DatabasePath);

    private string DatabasePath => Path.Combine(directoryPath, "printer.db");

    private string TestConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Pooling = false,
    }.ToString();

    private static async Task<TransitionOutcome> CaptureAsync(
        ValueTask<PrintAttemptTransitionRecord> transition)
    {
        try
        {
            return new TransitionOutcome(await transition, null);
        }
        catch (Exception exception)
        {
            return new TransitionOutcome(null, exception);
        }
    }

    private static async Task<T> ReadPragmaAsync<T>(SqliteConnection connection, string pragma)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var value = await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException($"SQLite PRAGMA '{pragma}' returned no value.");
        return (T)(Convert.ChangeType(value, typeof(T)) ??
            throw new InvalidOperationException($"SQLite PRAGMA '{pragma}' could not be converted."));
    }

    private async Task CreateVersionOneDatabaseAsync(Guid attemptId, DateTimeOffset createdAt)
    {
        await using var connection = new SqliteConnection(TestConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE print_attempts (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                state TEXT NOT NULL,
                completion_evidence TEXT NULL,
                version INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
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
                UNIQUE (attempt_id, resulting_version)
            );

            CREATE INDEX ix_print_attempt_transitions_attempt_sequence
                ON print_attempt_transitions(attempt_id, sequence);

            INSERT INTO print_attempts (
                attempt_id,
                state,
                completion_evidence,
                version,
                created_at_utc,
                updated_at_utc)
            VALUES ($attempt_id, 'Created', NULL, 0, $created_at_utc, $created_at_utc);

            INSERT INTO print_attempt_transitions (
                transition_id,
                attempt_id,
                previous_state,
                state,
                completion_evidence,
                resulting_version,
                occurred_at_utc)
            VALUES ($attempt_id, $attempt_id, NULL, 'Created', NULL, 0, $created_at_utc);

            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$attempt_id", attemptId.ToString("N"));
        command.Parameters.AddWithValue("$created_at_utc", createdAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountTablesAsync(
        SqliteConnection connection,
        params string[] tableNames)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name IN ({string.Join(", ", tableNames.Select((_, index) => $"$name{index}"))});
            """;
        for (var index = 0; index < tableNames.Length; index++)
        {
            command.Parameters.AddWithValue($"$name{index}", tableNames[index]);
        }

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private sealed record TransitionOutcome(
        PrintAttemptTransitionRecord? Transition,
        Exception? Error);
}
