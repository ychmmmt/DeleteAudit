using System.Globalization;
using System.Security.Cryptography;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.LiveMonitoring;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.LiveMonitoring;

public sealed class SqliteLiveMonitoringRepositoryTests
{
    private static readonly DateTimeOffset StartedUtc =
        new(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingLiveSchemaFailsClosedWithoutCreatingTables()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: false);
        var repository = new SqliteLiveMonitoringRepository(location);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ValidateSchemaAsync());

        Assert.Contains("live_monitoring_sessions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0003_phase_2a_live_monitoring.sql", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountLiveTablesAsync(location));
    }

    [Fact]
    public async Task MissingDatabaseIsReportedWithoutCreatingTheFile()
    {
        var location = CreateLocation();
        var repository = new SqliteLiveMonitoringRepository(location);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ValidateSchemaAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveSessionAsync(CreateSession(), []));

        Assert.False(File.Exists(location.DatabasePath));
    }

    [Fact]
    public async Task AppliedLiveSchemaValidatesSuccessfully()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);

        await repository.ValidateSchemaAsync();

        Assert.Equal(3, await CountLiveTablesAsync(location));
    }

    [Fact]
    public async Task SessionChannelsAndDiagnosticsArePersistedExactly()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = CreateSession(
            counters: new LiveMonitoringCounters(
                Received: 10,
                DeleteFact: 2,
                ProcessContext: 1,
                SecurityEvidence: 1,
                Ignored: 3,
                Error: 1,
                Dropped: 2,
                LateDiscarded: 5,
                SuppressedDiagnostics: 7),
            statuses:
            [
                new LiveChannelStatus(
                    LiveMonitoringChannels.SysmonOperational,
                    LiveChannelAvailability.Available,
                    "可只读访问"),
                new LiveChannelStatus(
                    LiveMonitoringChannels.Security,
                    LiveChannelAvailability.AccessDenied,
                    "无权限")
            ]);
        var diagnostics = new[]
        {
            new LiveMonitoringDiagnostic(
                "channel_access_denied",
                "Security: 无权限",
                ImportDiagnosticSeverity.Warning,
                "probe",
                StartedUtc),
            new LiveMonitoringDiagnostic(
                "live_queue_overflow",
                "The bounded queue reached its capacity.",
                ImportDiagnosticSeverity.Warning,
                "queue",
                StartedUtc.AddSeconds(5))
        };

        await repository.SaveSessionAsync(session, diagnostics);

        var stored = await ReadSessionAsync(location, session.LiveSessionId);
        Assert.Equal("stopped", stored.FinalState);
        Assert.Equal(10, stored.Received);
        Assert.Equal(2, stored.DeleteFact);
        Assert.Equal(1, stored.ProcessContext);
        Assert.Equal(1, stored.SecurityEvidence);
        Assert.Equal(3, stored.Ignored);
        Assert.Equal(1, stored.Error);
        Assert.Equal(2, stored.Dropped);
        Assert.Equal(5, stored.LateDiscarded);
        Assert.Equal(7, stored.SuppressedDiagnostics);
        Assert.Equal(2048, stored.QueueCapacity);
        Assert.Equal(
            [("Microsoft-Windows-Sysmon/Operational", "available"), ("Security", "access_denied")],
            await ReadChannelsAsync(location, session.LiveSessionId));
        Assert.Equal(
            ["channel_access_denied", "live_queue_overflow"],
            await ReadDiagnosticCodesAsync(location, session.LiveSessionId));
    }

    [Fact]
    public async Task UnbalancedCountersAreRejectedBeforeAnyWrite()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = CreateSession(
            counters: new LiveMonitoringCounters(
                Received: 10,
                DeleteFact: 1,
                Ignored: 1));

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.SaveSessionAsync(session, []));

        Assert.Equal(0, await CountSessionsAsync(location));
    }

    [Fact]
    public async Task DatabaseCheckConstraintRejectsUnbalancedCounts()
    {
        // Proves the storage-level guard fires on its own, independently of the
        // application guard exercised above.
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);

        await using var connection = CreateWritableConnection(location.DatabasePath);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO live_monitoring_sessions (
                live_session_id, started_utc, final_state, received_count,
                delete_fact_count, process_context_count, security_evidence_count,
                ignored_count, error_count, dropped_count, late_discarded_count,
                suppressed_diagnostic_count, queue_capacity, application_version)
            VALUES ('unbalanced', '2026-07-25T09:00:00.0000000+00:00', 'stopped',
                    10, 1, 0, 0, 1, 0, 0, 0, 0, 2048, 'test');
            """;

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(0, await CountSessionsAsync(location));
    }

    [Fact]
    public async Task DatabaseCheckConstraintRejectsOverlongDiagnosticMessages()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = CreateSession();
        await repository.SaveSessionAsync(session, []);

        await using var connection = CreateWritableConnection(location.DatabasePath);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO live_monitoring_diagnostics (
                live_diagnostic_id, live_session_id, stage, severity, code,
                message, occurred_utc)
            VALUES ('overlong', $session, 'parse', 'error', 'too_long',
                    $message, '2026-07-25T09:00:00.0000000+00:00');
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = session.LiveSessionId;
        command.Parameters.Add("$message", SqliteType.Text).Value =
            new string('x', LiveMonitoringLimits.MaxDiagnosticMessageCharacters + 1);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task DiagnosticsArePersistedUpToTheHardCap()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = CreateSession();
        var diagnostics = Enumerable
            .Range(0, LiveMonitoringLimits.MaxDiagnostics + 50)
            .Select(index => new LiveMonitoringDiagnostic(
                $"code_{index}",
                new string('m', LiveMonitoringLimits.MaxDiagnosticMessageCharacters * 2),
                ImportDiagnosticSeverity.Error,
                "parse",
                StartedUtc.AddSeconds(index)))
            .ToArray();

        await repository.SaveSessionAsync(session, diagnostics);

        Assert.Equal(
            LiveMonitoringLimits.MaxDiagnostics,
            await ScalarAsync(
                location,
                "SELECT COUNT(*) FROM live_monitoring_diagnostics;"));
        Assert.Equal(
            LiveMonitoringLimits.MaxDiagnosticMessageCharacters,
            await ScalarAsync(
                location,
                "SELECT MAX(length(message)) FROM live_monitoring_diagnostics;"));
    }

    [Fact]
    public async Task LiveWritesDoNotDisturbPhase1CEvidenceTables()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var evidenceBefore = await ReadEvidenceTableShapeAsync(location);

        await repository.SaveSessionAsync(CreateSession(), []);

        Assert.Equal(evidenceBefore, await ReadEvidenceTableShapeAsync(location));
        Assert.Equal(1, await CountSessionsAsync(location));
    }

    [Fact]
    public async Task LiveMonitoringSchemaIsNotRequiredByTheViewerQueryService()
    {
        // Phase 1C databases without the 0003 increment must keep working.
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: false);
        var viewer = new SqliteViewerQueryService(location);

        var status = await viewer.GetDatabaseStatusAsync();

        Assert.True(status.IsReady);
        Assert.Empty(status.MissingObjects);
    }

    [Fact]
    public async Task ViewerConnectionStillRefusesWritesToLiveTables()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);

        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO live_monitoring_sessions (
                live_session_id, started_utc, final_state, received_count,
                delete_fact_count, process_context_count, security_evidence_count,
                ignored_count, error_count, dropped_count, late_discarded_count,
                suppressed_diagnostic_count, queue_capacity, application_version)
            VALUES ('forbidden', '2026-07-25T09:00:00.0000000+00:00', 'stopped',
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 'test');
            """;

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(8, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task MigrationFileDoesNotTouchExistingSchemaObjects()
    {
        var migration = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "0003_phase_2a_live_monitoring.sql"));

        foreach (var forbidden in new[]
                 {
                     "DROP ", "DELETE FROM", "TRUNCATE", "VACUUM",
                     "ATTACH", "DETACH", "ALTER TABLE"
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                migration,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("CREATE TABLE live_monitoring_sessions", migration, StringComparison.Ordinal);
    }

    private static LiveMonitoringSession CreateSession(
        LiveMonitoringCounters? counters = null,
        IReadOnlyList<LiveChannelStatus>? statuses = null) =>
        new(
            Guid.NewGuid().ToString("D"),
            StartedUtc,
            StartedUtc.AddMinutes(3),
            statuses ??
            [
                new LiveChannelStatus(
                    LiveMonitoringChannels.SysmonOperational,
                    LiveChannelAvailability.Available)
            ],
            counters ?? LiveMonitoringCounters.Empty,
            LiveMonitoringState.Stopped,
            2048,
            "2.0.0-phase2a-test");

    private static ViewerDataLocation CreateLocation()
    {
        var directory = Path.Combine(
            ViewerDataLocation.DefaultRoot,
            "tests",
            $"live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return ViewerDataLocation.CreateForTesting(
            Path.Combine(directory, "viewer.db"),
            Path.Combine(directory, "jsonl"));
    }

    private static async Task CreateDatabaseAsync(
        ViewerDataLocation location,
        bool applyLiveMigration)
    {
        await using var connection = CreateWritableConnection(location.DatabasePath);
        await connection.OpenAsync();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var scripts = new List<string>
        {
            await File.ReadAllTextAsync(Path.Combine(fixtures, "schema.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0002_phase_1b_offline_import.sql"))
        };
        if (applyLiveMigration)
        {
            scripts.Add(await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0003_phase_2a_live_monitoring.sql")));
        }

        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, scripts);
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection CreateWritableConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());

    private static async Task<long> CountLiveTablesAsync(ViewerDataLocation location) =>
        await ScalarAsync(
            location,
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name LIKE 'live_monitoring_%';
            """);

    private static async Task<long> CountSessionsAsync(ViewerDataLocation location) =>
        await ScalarAsync(location, "SELECT COUNT(*) FROM live_monitoring_sessions;");

    private static async Task<long> ScalarAsync(
        ViewerDataLocation location,
        string sql)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<StoredSession> ReadSessionAsync(
        ViewerDataLocation location,
        string liveSessionId)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT final_state, received_count, delete_fact_count,
                   process_context_count, security_evidence_count, ignored_count,
                   error_count, dropped_count, late_discarded_count,
                   suppressed_diagnostic_count, queue_capacity
            FROM live_monitoring_sessions
            WHERE live_session_id = $id;
            """;
        command.Parameters.Add("$id", SqliteType.Text).Value = liveSessionId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new StoredSession(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10));
    }

    private static async Task<(string Channel, string Availability)[]> ReadChannelsAsync(
        ViewerDataLocation location,
        string liveSessionId)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT channel_name, availability
            FROM live_monitoring_channels
            WHERE live_session_id = $id
            ORDER BY channel_name;
            """;
        command.Parameters.Add("$id", SqliteType.Text).Value = liveSessionId;
        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return [.. rows];
    }

    private static async Task<string[]> ReadDiagnosticCodesAsync(
        ViewerDataLocation location,
        string liveSessionId)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code
            FROM live_monitoring_diagnostics
            WHERE live_session_id = $id
            ORDER BY occurred_utc;
            """;
        command.Parameters.Add("$id", SqliteType.Text).Value = liveSessionId;
        var codes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return [.. codes];
    }

    private static async Task<string> ReadEvidenceTableShapeAsync(
        ViewerDataLocation location)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE name IN ('raw_events', 'delete_events', 'delete_sessions',
                           'import_sessions', 'v_delete_audit')
            ORDER BY name;
            """;
        var builder = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            builder.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1));
        }

        return Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed record StoredSession(
        string FinalState,
        long Received,
        long DeleteFact,
        long ProcessContext,
        long SecurityEvidence,
        long Ignored,
        long Error,
        long Dropped,
        long LateDiscarded,
        long SuppressedDiagnostics,
        long QueueCapacity);
}
