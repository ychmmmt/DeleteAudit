using System.Globalization;
using System.Security.Cryptography;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;
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
            () => SaveSessionAsync(repository, CreateSession(), []));

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

    [Theory]
    [InlineData(EvidenceSchemaMutation.MissingColumn, "provider_name")]
    [InlineData(EvidenceSchemaMutation.WrongType, "raw_xml_sha256")]
    [InlineData(EvidenceSchemaMutation.MissingNotNull, "channel_name")]
    [InlineData(EvidenceSchemaMutation.WrongPrimaryKey, "live_evidence_id")]
    [InlineData(EvidenceSchemaMutation.MissingUnique, "live_session_id, received_sequence")]
    [InlineData(EvidenceSchemaMutation.MissingForeignKey, "live_session_id")]
    [InlineData(
        EvidenceSchemaMutation.MissingUpdateTrigger,
        "live_capture_records_no_update")]
    [InlineData(
        EvidenceSchemaMutation.MissingDeleteTrigger,
        "live_capture_records_no_delete")]
    [InlineData(
        EvidenceSchemaMutation.WrongTriggerBinding,
        "live_capture_records_no_update")]
    [InlineData(
        EvidenceSchemaMutation.TriggerWithoutRaise,
        "live_capture_records_no_update")]
    [InlineData(
        EvidenceSchemaMutation.ConditionalTrigger,
        "live_capture_records_no_update")]
    [InlineData(EvidenceSchemaMutation.MissingStrict, "live_capture_sessions")]
    public async Task MalformedEvidenceSchemaFailsClosedWithSpecificObjectAndMigration(
        EvidenceSchemaMutation mutation,
        string expectedObject)
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(
            location,
            applyLiveMigration: true,
            evidenceMigrationTransform: sql => MutateEvidenceMigration(sql, mutation));
        var repository = new SqliteLiveMonitoringRepository(location);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ValidateSchemaAsync());

        Assert.Contains(expectedObject, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "db/migrations/0004_phase_2b_live_evidence.sql",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "runtime structural readiness check",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "does not prove database integrity, tamper resistance, or migration authenticity",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSummarySchemaNamesThe0003ObjectAndMigration()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(
            location,
            applyLiveMigration: true,
            liveMigrationTransform: sql => ReplaceRequiredOnce(
                sql,
                "started_utc             TEXT NOT NULL,",
                "started_utc             BLOB NOT NULL,"));
        var repository = new SqliteLiveMonitoringRepository(location);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ValidateSchemaAsync());

        Assert.Contains(
            "live_monitoring_sessions",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("started_utc", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "db/migrations/0003_phase_2a_live_monitoring.sql",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "0004_phase_2b_live_evidence.sql",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessUsesReadOnlyConnectionAndContainsNoWritePath()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot.Value,
            "src",
            "DeleteAudit.Infrastructure",
            "LiveMonitoring",
            "SqliteLiveMonitoringRepository.cs"));
        var start = source.IndexOf(
            "public async Task ValidateSchemaAsync",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public async Task StartCaptureSessionAsync",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var readinessPath = source[start..end];
        Assert.Contains(
            "_location.CreateReadOnlyConnection()",
            readinessPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenWritableAsync",
            readinessPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExecuteNonQuery",
            readinessPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessLeavesSchemaAndBusinessDataUnchanged()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        await repository.StartCaptureSessionAsync(new LiveCaptureSessionStart(
            "aaaaaaaa-1111-2222-3333-444444444444",
            StartedUtc,
            2048,
            "readiness-snapshot"));
        var before = await ReadReadinessSnapshotAsync(location);

        await repository.ValidateSchemaAsync();

        Assert.Equal(before, await ReadReadinessSnapshotAsync(location));
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

        await SaveSessionAsync(repository, session, diagnostics);

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
    public async Task MissingEvidenceSchemaFailsClosedNamingThe0004Migration()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(
            location,
            applyLiveMigration: true,
            applyEvidenceMigration: false);
        var repository = new SqliteLiveMonitoringRepository(location);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ValidateSchemaAsync());

        Assert.Contains("live_capture_sessions", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "0004_phase_2b_live_evidence.sql",
            exception.Message,
            StringComparison.Ordinal);
        // Failing validation must never bring the missing tables into existence.
        Assert.Equal(0, await CountCaptureTablesAsync(location));
    }

    [Fact]
    public async Task CaptureSessionStartIsPersistedExactly()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var start = new LiveCaptureSessionStart(
            "aaaaaaaa-0000-0000-0000-000000000001",
            StartedUtc,
            2048,
            "0.1.0-alpha");

        await repository.StartCaptureSessionAsync(start);

        Assert.Equal(1, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_sessions WHERE queue_capacity = 2048;"));
        // A start on its own is a legal, queryable state: the capture is running.
        Assert.Equal(0, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_completions;"));
    }

    [Fact]
    public async Task RecordsAreAppendedInSequenceOrderWithTheirExactDigest()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartCaptureAsync(repository);

        await repository.AppendRecordsAsync(
        [
            CaptureRecord(sessionId, 1, "<Event>one</Event>"),
            CaptureRecord(sessionId, 2, "<Event>two</Event>")
        ]);

        Assert.Equal(
            [$"{sessionId}:1", $"{sessionId}:2"],
            await ReadEvidenceIdsAsync(location));
        var storedDigest = await BlobAsync(
            location,
            $"SELECT raw_xml_sha256 FROM live_capture_records WHERE received_sequence = 1;");
        Assert.Equal(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("<Event>one</Event>")),
            storedDigest);
    }

    [Fact]
    public async Task ADuplicateSequenceAbortsTheWholeBatch()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync([CaptureRecord(sessionId, 1)]);

        // The second batch reuses sequence 1. Nothing in it may survive: no OR IGNORE,
        // no OR REPLACE, no partial batch.
        await Assert.ThrowsAsync<SqliteException>(
            () => repository.AppendRecordsAsync(
            [
                CaptureRecord(sessionId, 1, "<Event>replayed</Event>"),
                CaptureRecord(sessionId, 5)
            ]));

        Assert.Equal([$"{sessionId}:1"], await ReadEvidenceIdsAsync(location));
        Assert.Equal("<Event />", await TextAsync(
            location,
            "SELECT raw_xml FROM live_capture_records WHERE received_sequence = 1;"));
    }

    [Fact]
    public async Task ABatchMayNotSpanTwoSessionsOrGoBackwards()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartCaptureAsync(repository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AppendRecordsAsync(
            [
                CaptureRecord(sessionId, 1),
                CaptureRecord("bbbbbbbb-0000-0000-0000-000000000002", 2)
            ]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AppendRecordsAsync(
            [
                CaptureRecord(sessionId, 2),
                CaptureRecord(sessionId, 1)
            ]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AppendRecordsAsync([]));

        Assert.Equal(0, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_records;"));
    }

    [Fact]
    public async Task AForgedEvidenceIdIsRejectedBeforeAnyWrite()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync([CaptureRecord(sessionId, 1)]);

        // The id must be exactly session + ":" + sequence; a free-form one is a defect.
        var forged = CaptureRecord(sessionId, 2) with { LiveEvidenceId = $"{sessionId}:999" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AppendRecordsAsync([forged, CaptureRecord(sessionId, 3)]));

        // Rejected before SQLite was touched: nothing new, nothing disturbed.
        Assert.Equal([$"{sessionId}:1"], await ReadEvidenceIdsAsync(location));
    }

    [Fact]
    public async Task AMismatchedRawXmlDigestIsRejectedBeforeAnyWrite()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync([CaptureRecord(sessionId, 1)]);

        // A digest that belongs to different content must never be stored beside this XML.
        var tampered = CaptureRecord(sessionId, 2, "<Event>real</Event>") with
        {
            RawXmlSha256 = SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("<Event>other</Event>"))
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AppendRecordsAsync([tampered]));

        Assert.Equal([$"{sessionId}:1"], await ReadEvidenceIdsAsync(location));
        Assert.Equal("<Event />", await TextAsync(
            location,
            "SELECT raw_xml FROM live_capture_records WHERE received_sequence = 1;"));
    }

    [Fact]
    public async Task RepositoryUsesNeitherIgnoreNorReplace()
    {
        // Structural guard: evidence must never be silently skipped or overwritten.
        var source = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot.Value,
            "src",
            "DeleteAudit.Infrastructure",
            "LiveMonitoring",
            "SqliteLiveMonitoringRepository.cs"));

        Assert.DoesNotContain("INSERT OR IGNORE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT OR REPLACE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE live_capture", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM live_capture", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletionAndSummaryShareOneTransactionAndTheSameCounts()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var counters = new LiveMonitoringCounters(
            Received: 4,
            DeleteFact: 1,
            Ignored: 1,
            Error: 1,
            Dropped: 1,
            LateDiscarded: 2,
            SuppressedDiagnostics: 3);
        var session = CreateSession(counters: counters);
        await repository.StartCaptureSessionAsync(new LiveCaptureSessionStart(
            session.LiveSessionId,
            session.StartedUtc,
            session.QueueCapacity,
            session.ApplicationVersion));

        await repository.CompleteSessionAsync(
            new LiveCaptureCompletion(
                session.LiveSessionId,
                StartedUtc.AddMinutes(3),
                LiveMonitoringState.Stopped,
                counters,
                PersistedRecordCount: 3),
            session,
            []);

        // Both rows exist and agree; that is what one transaction buys.
        var completionRow = await RowAsync(
            location,
            """
            SELECT received_count, delete_fact_count, ignored_count, error_count,
                   dropped_count, persisted_record_count
            FROM live_capture_completions;
            """);
        var summaryRow = await RowAsync(
            location,
            """
            SELECT received_count, delete_fact_count, ignored_count, error_count,
                   dropped_count
            FROM live_monitoring_sessions;
            """);

        Assert.Equal(new long[] { 4, 1, 1, 1, 1, 3 }, completionRow);
        Assert.Equal(new long[] { 4, 1, 1, 1, 1 }, summaryRow);
    }

    [Fact]
    public async Task CompletingAnUnstartedSessionWritesNothing()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = CreateSession();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.CompleteSessionAsync(
                new LiveCaptureCompletion(
                    session.LiveSessionId,
                    StartedUtc,
                    LiveMonitoringState.Stopped,
                    LiveMonitoringCounters.Empty,
                    0),
                session,
                []));

        // The whole transaction rolled back, so no summary leaked out either.
        Assert.Equal(0, await CountSessionsAsync(location));
        Assert.Equal(0, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_completions;"));
    }

    [Fact]
    public async Task ASessionMayBeCompletedOnlyOnce()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = CreateSession();
        await SaveSessionAsync(repository, session, []);

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.CompleteSessionAsync(
                new LiveCaptureCompletion(
                    session.LiveSessionId,
                    StartedUtc.AddMinutes(9),
                    LiveMonitoringState.Error,
                    session.Counters,
                    0),
                session,
                []));

        Assert.Equal(1, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_completions;"));
        Assert.Equal("stopped", await TextAsync(
            location,
            "SELECT final_state FROM live_capture_completions;"));
    }

    [Fact]
    public async Task ViewerConnectionStillRefusesWritesToCaptureTables()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);

        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO live_capture_sessions (
                live_session_id, started_utc, queue_capacity, application_version)
            VALUES ('forbidden', '2026-07-25T09:00:00.0000000+00:00', 1, 'test');
            """;

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(8, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task CaptureWritesDoNotDisturbOfflineEvidenceTables()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigration: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var evidenceBefore = await ReadEvidenceTableShapeAsync(location);
        var sessionId = await StartCaptureAsync(repository);

        await repository.AppendRecordsAsync([CaptureRecord(sessionId, 1)]);

        Assert.Equal(evidenceBefore, await ReadEvidenceTableShapeAsync(location));
        Assert.Equal(0, await ScalarAsync(location, "SELECT COUNT(*) FROM raw_events;"));
        Assert.Equal(0, await ScalarAsync(location, "SELECT COUNT(*) FROM delete_events;"));
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
            () => SaveSessionAsync(repository, session, []));

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
        await SaveSessionAsync(repository, session, []);

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

        await SaveSessionAsync(repository, session, diagnostics);

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

        await SaveSessionAsync(repository, CreateSession(), []);

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

    /// <summary>
    /// Starts a capture session and then completes it, which is what the service does.
    /// Completion and the Phase 2A summary share one transaction, so the summary can only
    /// be written this way.
    /// </summary>
    private static async Task SaveSessionAsync(
        SqliteLiveMonitoringRepository repository,
        LiveMonitoringSession session,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        long persistedRecordCount = 0)
    {
        await repository.StartCaptureSessionAsync(new LiveCaptureSessionStart(
            session.LiveSessionId,
            session.StartedUtc,
            session.QueueCapacity,
            session.ApplicationVersion));
        await repository.CompleteSessionAsync(
            new LiveCaptureCompletion(
                session.LiveSessionId,
                session.StoppedUtc ?? session.StartedUtc,
                session.FinalState,
                session.Counters,
                persistedRecordCount),
            session,
            diagnostics);
    }

    private static async Task CreateDatabaseAsync(
        ViewerDataLocation location,
        bool applyLiveMigration,
        bool? applyEvidenceMigration = null,
        Func<string, string>? liveMigrationTransform = null,
        Func<string, string>? evidenceMigrationTransform = null)
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
            var migration = (await File.ReadAllTextAsync(
                    Path.Combine(fixtures, "0003_phase_2a_live_monitoring.sql")))
                .ReplaceLineEndings("\n");
            scripts.Add(liveMigrationTransform?.Invoke(migration) ?? migration);
        }

        if (applyEvidenceMigration ?? applyLiveMigration)
        {
            var migration = (await File.ReadAllTextAsync(
                    Path.Combine(fixtures, "0004_phase_2b_live_evidence.sql")))
                .ReplaceLineEndings("\n");
            scripts.Add(evidenceMigrationTransform?.Invoke(migration) ?? migration);
        }

        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, scripts);
        await command.ExecuteNonQueryAsync();
    }

    private static string MutateEvidenceMigration(
        string migration,
        EvidenceSchemaMutation mutation) =>
        mutation switch
        {
            EvidenceSchemaMutation.MissingColumn => ReplaceRequiredOnce(
                migration,
                "    provider_name           TEXT,\n",
                string.Empty),
            EvidenceSchemaMutation.WrongType => ReplaceRequiredOnce(
                migration,
                "    raw_xml_sha256          BLOB NOT NULL",
                "    raw_xml_sha256          TEXT NOT NULL"),
            EvidenceSchemaMutation.MissingNotNull => ReplaceRequiredOnce(
                migration,
                "    channel_name            TEXT NOT NULL",
                "    channel_name            TEXT"),
            EvidenceSchemaMutation.WrongPrimaryKey => ReplaceRequiredOnce(
                migration,
                "    live_evidence_id        TEXT PRIMARY KEY,",
                "    live_evidence_id        TEXT NOT NULL,"),
            EvidenceSchemaMutation.MissingUnique => ReplaceRequiredOnce(
                migration,
                ",\n    UNIQUE (live_session_id, received_sequence)",
                string.Empty),
            EvidenceSchemaMutation.MissingForeignKey => ReplaceRequiredOnce(
                migration,
                """
                CREATE TABLE live_capture_records (
                    live_evidence_id        TEXT PRIMARY KEY,
                    live_session_id         TEXT NOT NULL
                                                REFERENCES live_capture_sessions(live_session_id),
                """,
                """
                CREATE TABLE live_capture_records (
                    live_evidence_id        TEXT PRIMARY KEY,
                    live_session_id         TEXT NOT NULL,
                """),
            EvidenceSchemaMutation.MissingUpdateTrigger => ReplaceRequiredOnce(
                migration,
                """
                CREATE TRIGGER live_capture_records_no_update
                BEFORE UPDATE ON live_capture_records BEGIN
                    SELECT RAISE(ABORT, 'live_capture_records is append-only');
                END;
                """,
                string.Empty),
            EvidenceSchemaMutation.MissingDeleteTrigger => ReplaceRequiredOnce(
                migration,
                """
                CREATE TRIGGER live_capture_records_no_delete
                BEFORE DELETE ON live_capture_records BEGIN
                    SELECT RAISE(ABORT, 'live_capture_records is append-only');
                END;
                """,
                string.Empty),
            EvidenceSchemaMutation.WrongTriggerBinding => ReplaceRequiredOnce(
                migration,
                """
                CREATE TRIGGER live_capture_records_no_update
                BEFORE UPDATE ON live_capture_records
                """,
                """
                CREATE TRIGGER live_capture_records_no_update
                BEFORE UPDATE ON live_capture_sessions
                """),
            EvidenceSchemaMutation.TriggerWithoutRaise => ReplaceRequiredOnce(
                migration,
                """
                CREATE TRIGGER live_capture_records_no_update
                BEFORE UPDATE ON live_capture_records BEGIN
                    SELECT RAISE(ABORT, 'live_capture_records is append-only');
                END;
                """,
                """
                CREATE TRIGGER live_capture_records_no_update
                BEFORE UPDATE ON live_capture_records BEGIN
                    SELECT 1;
                END;
                """),
            EvidenceSchemaMutation.ConditionalTrigger => ReplaceRequiredOnce(
                migration,
                "BEFORE UPDATE ON live_capture_records BEGIN",
                "BEFORE UPDATE ON live_capture_records WHEN 1 = 1 BEGIN"),
            EvidenceSchemaMutation.MissingStrict => ReplaceRequiredOnce(
                migration,
                """
                    application_version     TEXT NOT NULL CHECK (length(application_version) > 0)
                ) STRICT;
                """,
                """
                    application_version     TEXT NOT NULL CHECK (length(application_version) > 0)
                );
                """),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation),
                mutation,
                null)
        };

    private static string ReplaceRequiredOnce(
        string source,
        string oldValue,
        string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(
            index >= 0,
            $"The fixture text to mutate was not found: {oldValue}");
        Assert.Equal(
            -1,
            source.IndexOf(
                oldValue,
                index + oldValue.Length,
                StringComparison.Ordinal));
        return string.Concat(
            source.AsSpan(0, index),
            newValue,
            source.AsSpan(index + oldValue.Length));
    }

    private static SqliteConnection CreateWritableConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());

    /// <summary>
    /// A synthetic captured record. The machine and provider names are fixtures; no real
    /// event log is read anywhere in these tests.
    /// </summary>
    private static LiveCaptureRecord CaptureRecord(
        string sessionId,
        long sequence,
        string rawXml = "<Event />") =>
        new(
            LiveEvidenceIdentity.Create(sessionId, sequence),
            sessionId,
            sequence,
            41,
            LiveMonitoringChannels.SysmonProvider,
            LiveMonitoringChannels.SysmonOperational,
            "LAB-PC",
            StartedUtc,
            StartedUtc,
            rawXml,
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawXml)),
            "raw-1",
            26,
            LiveEventOutcome.Ignored,
            null,
            null);

    private static async Task<string> StartCaptureAsync(
        SqliteLiveMonitoringRepository repository)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        await repository.StartCaptureSessionAsync(new LiveCaptureSessionStart(
            sessionId,
            StartedUtc,
            2048,
            "0.1.0-alpha"));
        return sessionId;
    }

    private static async Task<string[]> ReadEvidenceIdsAsync(ViewerDataLocation location)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT live_evidence_id
            FROM live_capture_records
            ORDER BY received_sequence;
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task<byte[]> BlobAsync(ViewerDataLocation location, string sql)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (byte[])(await command.ExecuteScalarAsync() ?? Array.Empty<byte>());
    }

    private static async Task<string> TextAsync(ViewerDataLocation location, string sql)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    private static async Task<long[]> RowAsync(ViewerDataLocation location, string sql)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var values = new long[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetInt64(index);
        }

        return values;
    }

    private static async Task<long> CountCaptureTablesAsync(ViewerDataLocation location) =>
        await ScalarAsync(
            location,
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name LIKE 'live_capture_%';
            """);

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

    private static async Task<string> ReadReadinessSnapshotAsync(
        ViewerDataLocation location)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        var builder = new System.Text.StringBuilder();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT type, name, tbl_name, COALESCE(sql, '')
                FROM sqlite_master
                WHERE name LIKE 'live_%'
                ORDER BY type, name;
                """;
            await using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    builder
                        .Append(reader.GetString(ordinal))
                        .Append('\u001f');
                }

                builder.Append('\u001e');
            }
        }

        using (var data = connection.CreateCommand())
        {
            data.CommandText = """
                SELECT live_session_id, started_utc, queue_capacity, application_version
                FROM live_capture_sessions
                ORDER BY live_session_id;
                """;
            await using var reader = await data.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder
                    .Append(reader.GetString(0))
                    .Append('\u001f')
                    .Append(reader.GetString(1))
                    .Append('\u001f')
                    .Append(reader.GetInt64(2))
                    .Append('\u001f')
                    .Append(reader.GetString(3))
                    .Append('\u001e');
            }
        }

        return Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public enum EvidenceSchemaMutation
    {
        MissingColumn,
        WrongType,
        MissingNotNull,
        WrongPrimaryKey,
        MissingUnique,
        MissingForeignKey,
        MissingUpdateTrigger,
        MissingDeleteTrigger,
        WrongTriggerBinding,
        TriggerWithoutRaise,
        ConditionalTrigger,
        MissingStrict
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
