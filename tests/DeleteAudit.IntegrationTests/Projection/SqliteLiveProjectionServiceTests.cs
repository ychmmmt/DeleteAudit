using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeleteAudit.Application.Projection;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;
using DeleteAudit.Infrastructure.Analysis;
using DeleteAudit.Infrastructure.LiveMonitoring;
using DeleteAudit.Infrastructure.Parsing;
using DeleteAudit.Infrastructure.Projection;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.Projection;

public sealed class SqliteLiveProjectionServiceTests
{
    private static readonly DateTimeOffset StartedUtc =
        new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProjectsCanonicalLiveRecordsWithoutTouchingOfflineCore()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 1, ProcessXml(1), LiveEventOutcome.ProcessContext),
            CreateRecord(sessionId, 2, DeleteXml(2, "first.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 3, SecurityXml(3), LiveEventOutcome.SecurityEvidence),
            CreateRecord(sessionId, 4, NonDeleteSecurityXml(4), LiveEventOutcome.Ignored)
        ]);
        using var service = new SqliteLiveProjectionService(location);

        var result = await service.ProjectSessionAsync(sessionId);
        var continuity = await service.VerifyContinuityAsync(sessionId);
        var page = await service.GetProjectedRecordsAsync(Query(sessionId));

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(3, result.ConsideredCount);
        Assert.Equal(3, result.ProjectedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.True(continuity.IsContinuous, continuity.Detail);
        Assert.Equal(3, continuity.ProjectedCount);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal([1L, 2L, 3L], page.Items.Select(row => row.LiveIngestSequence));
        Assert.Equal([1L, 2L, 3L], page.Items.Select(row => row.SourceReceivedSequence));
        Assert.Equal(
            [
                LiveProjectionSources.SysmonProcess,
                LiveProjectionSources.SysmonDelete,
                LiveProjectionSources.Security4663
            ],
            page.Items.Select(row => row.Source));
        Assert.All(page.Items, row =>
        {
            Assert.Equal($"live-projection:{row.LiveEvidenceId}", row.LiveProjectionId);
            Assert.Equal("live_capture", row.Origin);
            Assert.Equal(64, row.RawXmlSha256.Length);
            Assert.Equal(64, row.CanonicalPayloadSha256.Length);
            Assert.Equal(64, row.EntryHash.Length);
        });
        Assert.Null(page.Items[0].PreviousEntryHash);
        Assert.Equal(page.Items[0].EntryHash, page.Items[1].PreviousEntryHash);
        Assert.Equal(0, await CountOfflineCoreRowsAsync(location));
        Assert.Equal(4, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_records;"));
    }

    [Fact]
    public async Task ReplayIsIdempotentAndKeepsStableIdentitySequenceEpochAndHash()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 2, DeleteXml(2, "a.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 9, DeleteXml(9, "b.txt"), LiveEventOutcome.DeleteFact)
        ]);
        using var service = new SqliteLiveProjectionService(location);

        var first = await service.ProjectSessionAsync(sessionId);
        var before = await service.GetProjectedRecordsAsync(Query(sessionId));
        var replay = await service.ProjectSessionAsync(sessionId);
        var after = await service.GetProjectedRecordsAsync(Query(sessionId));

        Assert.Equal(2, first.ProjectedCount);
        Assert.True(replay.WasAlreadyComplete);
        Assert.Equal(0, replay.ProjectedCount);
        Assert.Equal(2, replay.SkippedCount);
        Assert.Equal(before.Items, after.Items);
        Assert.Equal(2, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projection_runs;"));
    }

    [Fact]
    public async Task OutOfOrderCaptureInsertionProjectsByReceivedSequence()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 10, DeleteXml(10, "later.txt"), LiveEventOutcome.DeleteFact)
        ]);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 2, DeleteXml(2, "earlier.txt"), LiveEventOutcome.DeleteFact)
        ]);
        using var service = new SqliteLiveProjectionService(location);

        var result = await service.ProjectSessionAsync(sessionId);
        var page = await service.GetProjectedRecordsAsync(Query(sessionId));

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal([2L, 10L], page.Items.Select(row => row.SourceReceivedSequence));
        Assert.Equal([1L, 2L], page.Items.Select(row => row.LiveIngestSequence));
        Assert.Equal(2, await ScalarAsync(
            location,
            """
            SELECT first_received_sequence
            FROM live_channel_epochs
            WHERE live_session_id = $session;
            """,
            ("$session", sessionId)));
    }

    [Fact]
    public async Task ConcurrentProjectionRequestsCreateNoDuplicates()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 1, DeleteXml(1, "one.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 2, DeleteXml(2, "two.txt"), LiveEventOutcome.DeleteFact)
        ]);
        using var service = new SqliteLiveProjectionService(location);

        var results = await Task.WhenAll(
            service.ProjectSessionAsync(sessionId),
            service.ProjectSessionAsync(sessionId));

        Assert.All(results, result => Assert.True(result.Succeeded, result.FailureDetail));
        Assert.Equal(2, results.Sum(result => result.ProjectedCount));
        Assert.Equal(2, results.Sum(result => result.SkippedCount));
        Assert.Contains(results, result => result.WasAlreadyComplete);
        Assert.Equal(2, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projected_records;"));
    }

    [Fact]
    public async Task QueryFiltersAndPagingAreServerBoundedAndStable()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 1, ProcessXml(1), LiveEventOutcome.ProcessContext),
            CreateRecord(sessionId, 2, DeleteXml(2, "alpha.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 3, DeleteXml(3, "beta.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 4, SecurityXml(4), LiveEventOutcome.SecurityEvidence)
        ]);
        using var service = new SqliteLiveProjectionService(location);
        _ = await service.ProjectSessionAsync(sessionId);

        var first = await service.GetProjectedRecordsAsync(
            Query(sessionId) with { Page = new PageRequest(0, 1) });
        var second = await service.GetProjectedRecordsAsync(
            Query(sessionId) with { Page = new PageRequest(1, 1) });
        var deleteOnly = await service.GetProjectedRecordsAsync(
            Query(sessionId) with { Source = LiveProjectionSources.SysmonDelete });
        var path = await service.GetProjectedRecordsAsync(
            Query(sessionId) with { PathContains = "alpha" });
        var process = await service.GetProjectedRecordsAsync(
            Query(sessionId) with { ProcessContains = "--fixture" });

        Assert.Equal(4, first.TotalCount);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].LiveProjectionId, second.Items[0].LiveProjectionId);
        Assert.Equal(2, deleteOnly.TotalCount);
        Assert.Equal("alpha.txt", Path.GetFileName(Assert.Single(path.Items).NormalizedPath));
        Assert.Equal(LiveProjectionSources.SysmonProcess, Assert.Single(process.Items).Source);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (Query(sessionId) with
            {
                Page = new PageRequest(0, PageRequest.MaximumLimit + 1)
            }).Validate());
    }

    [Fact]
    public async Task Missing0005MakesOnlyProjectionUnavailableAndCreatesNothing()
    {
        var location = await CreateDatabaseAsync(applyProjection: false);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 1, DeleteXml(1, "source.txt"), LiveEventOutcome.DeleteFact)
        ]);
        using var service = new SqliteLiveProjectionService(location);

        var availability = await service.GetAvailabilityAsync();
        var result = await service.ProjectSessionAsync(sessionId);
        var historyAvailability = await new SqliteLiveHistoryQueryService(location)
            .GetAvailabilityAsync();
        var analysis = await new SqliteLiveAnalysisService(location)
            .AnalyzeAsync(sessionId);

        Assert.Equal(LiveProjectionState.MissingSchema, availability.State);
        Assert.Contains("0005", availability.Message, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
        Assert.Equal("projection_unavailable", result.FailureCode);
        Assert.True(historyAvailability.IsReady, historyAvailability.Message);
        Assert.Equal(1, analysis.DeleteFactCount);
        Assert.Equal(1, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_records;"));
        Assert.Equal(0, await ScalarAsync(
            location,
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name LIKE 'live_project%';
            """));
    }

    [Fact]
    public async Task GeneratedColumnMutationFailsClosedWithoutRepairingSchema()
    {
        var location = await CreateDatabaseAsync(
            applyProjection: true,
            projectionTransform: migration => ReplaceRequiredOnce(
                migration,
                "    projected_utc           TEXT NOT NULL,\n"
                + "    UNIQUE (live_session_id, source_received_sequence),",
                "    projected_utc           TEXT NOT NULL,\n"
                + "    shadow_projection TEXT GENERATED ALWAYS AS "
                + "(live_projection_id) VIRTUAL,\n"
                + "    UNIQUE (live_session_id, source_received_sequence),"));
        var before = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));
        using var service = new SqliteLiveProjectionService(location);

        var availability = await service.GetAvailabilityAsync();
        var after = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));

        Assert.Equal(LiveProjectionState.MissingSchema, availability.State);
        Assert.Contains("shadow_projection", availability.Message, StringComparison.Ordinal);
        Assert.Contains("0005_phase_2b4", availability.Message, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(after));
    }

    [Fact]
    public async Task PartialUpdateGuardFailsClosed()
    {
        var location = await CreateDatabaseAsync(
            applyProjection: true,
            projectionTransform: migration => ReplaceRequiredOnce(
                migration,
                "BEFORE UPDATE ON live_projected_records BEGIN",
                "BEFORE UPDATE OF entry_hash ON live_projected_records BEGIN"));
        using var service = new SqliteLiveProjectionService(location);

        var availability = await service.GetAvailabilityAsync();

        Assert.Equal(LiveProjectionState.MissingSchema, availability.State);
        Assert.Contains(
            "live_projected_records_no_update",
            availability.Message,
            StringComparison.Ordinal);
        Assert.Contains("not followed by ON", availability.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedUniqueConstraintFailsClosed()
    {
        var location = await CreateDatabaseAsync(
            applyProjection: true,
            projectionTransform: migration => ReplaceRequiredOnce(
                migration,
                "    UNIQUE (live_session_id, live_ingest_sequence),",
                "    UNIQUE (live_session_id, live_ingest_sequence),\n"
                + "    UNIQUE (provider_name),"));
        using var service = new SqliteLiveProjectionService(location);

        var availability = await service.GetAvailabilityAsync();

        Assert.Equal(LiveProjectionState.MissingSchema, availability.State);
        Assert.Contains("unexpected UNIQUE", availability.Message, StringComparison.Ordinal);
        Assert.Contains("provider_name", availability.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProjectionForeignKeyFailsClosed()
    {
        var location = await CreateDatabaseAsync(
            applyProjection: true,
            projectionTransform: migration => ReplaceRequiredOnce(
                migration,
                "    live_channel_epoch_id   TEXT NOT NULL\n"
                + "                                REFERENCES "
                + "live_channel_epochs(live_channel_epoch_id),",
                "    live_channel_epoch_id   TEXT NOT NULL,"));
        using var service = new SqliteLiveProjectionService(location);

        var availability = await service.GetAvailabilityAsync();

        Assert.Equal(LiveProjectionState.MissingSchema, availability.State);
        Assert.Contains("live_channel_epoch_id", availability.Message, StringComparison.Ordinal);
        Assert.Contains("foreign key", availability.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Applying0005DoesNotAlterOfflineTableDefinitions()
    {
        var beforeLocation = await CreateDatabaseAsync(applyProjection: false);
        var afterLocation = await CreateDatabaseAsync(applyProjection: true);

        var before = await ReadOfflineDefinitionsAsync(beforeLocation);
        var after = await ReadOfflineDefinitionsAsync(afterLocation);

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task AppendOnlyGuardsRejectUpdateDeleteAndReplace()
    {
        var location = await ProjectThreeDeletesAsync();
        await using var connection = WritableConnection(location);
        await connection.OpenAsync();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA recursive_triggers = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        foreach (var statement in new[]
                 {
                     """
                     UPDATE live_projected_records
                     SET entry_hash = zeroblob(32)
                     WHERE live_ingest_sequence = 1;
                     """,
                     """
                     DELETE FROM live_projected_records
                     WHERE live_ingest_sequence = 1;
                     """,
                     """
                     INSERT OR REPLACE INTO live_projected_records
                     SELECT * FROM live_projected_records
                     WHERE live_ingest_sequence = 1;
                     """
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            var exception = await Assert.ThrowsAsync<SqliteException>(
                () => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(3, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projected_records;"));
    }

    [Fact]
    public async Task PreCancelledProjectionWritesNothing()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 1, DeleteXml(1, "cancelled.txt"), LiveEventOutcome.DeleteFact)
        ]);
        using var service = new SqliteLiveProjectionService(location);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ProjectSessionAsync(sessionId, cancellation.Token));

        Assert.Equal(0, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projected_records;"));
        Assert.Equal(0, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projection_runs;"));
    }

    [Fact]
    public async Task ProjectionFailureRollsBackButRetainsSourceAndOfflineCore()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        const string malformedXml = "<Event";
        await repository.AppendRecordsAsync(
        [
            new LiveCaptureRecord(
                LiveEvidenceIdentity.Create(sessionId, 1),
                sessionId,
                1,
                null,
                null,
                LiveMonitoringChannels.SysmonOperational,
                null,
                null,
                StartedUtc.AddSeconds(1),
                malformedXml,
                SHA256.HashData(Encoding.UTF8.GetBytes(malformedXml)),
                null,
                null,
                LiveEventOutcome.DeleteFact,
                null,
                null)
        ]);
        using var service = new SqliteLiveProjectionService(location);

        var result = await service.ProjectSessionAsync(sessionId);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_capture_records;"));
        Assert.Equal(0, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projected_records;"));
        Assert.Equal(1, await ScalarAsync(
            location,
            "SELECT COUNT(*) FROM live_projection_runs WHERE outcome = 'failed';"));
        Assert.Equal(0, await CountOfflineCoreRowsAsync(location));
    }

    [Fact]
    public async Task ContinuityVerificationDetectsHashTampering()
    {
        var location = await ProjectThreeDeletesAsync();
        await ExecuteAsync(
            location,
            """
            DROP TRIGGER live_projected_records_no_update;
            UPDATE live_projected_records
            SET entry_hash = zeroblob(32)
            WHERE live_ingest_sequence = 2;
            CREATE TRIGGER live_projected_records_no_update
            BEFORE UPDATE ON live_projected_records BEGIN
                SELECT RAISE(ABORT, 'live_projected_records is append-only');
            END;
            """);
        var sessionId = await ReadSessionIdAsync(location);
        using var service = new SqliteLiveProjectionService(location);

        var continuity = await service.VerifyContinuityAsync(sessionId);

        Assert.False(continuity.IsContinuous);
        Assert.Equal(2, continuity.FirstBrokenSequence);
        Assert.Contains("entry hash", continuity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContinuityVerificationDetectsMissingChainNode()
    {
        var location = await ProjectThreeDeletesAsync();
        await ExecuteAsync(
            location,
            """
            DROP TRIGGER live_projected_records_no_delete;
            DELETE FROM live_projected_records
            WHERE live_ingest_sequence = 2;
            CREATE TRIGGER live_projected_records_no_delete
            BEFORE DELETE ON live_projected_records BEGIN
                SELECT RAISE(ABORT, 'live_projected_records is append-only');
            END;
            """);
        var sessionId = await ReadSessionIdAsync(location);
        using var service = new SqliteLiveProjectionService(location);

        var continuity = await service.VerifyContinuityAsync(sessionId);

        Assert.False(continuity.IsContinuous);
        Assert.Equal(2, continuity.FirstBrokenSequence);
        Assert.Contains("sequence", continuity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AvailabilityAndQueriesDoNotModifyDatabase()
    {
        var location = await ProjectThreeDeletesAsync();
        var sessionId = await ReadSessionIdAsync(location);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));
        using var service = new SqliteLiveProjectionService(location);

        var availability = await service.GetAvailabilityAsync();
        _ = await service.GetProjectedRecordsAsync(Query(sessionId));
        _ = await service.VerifyContinuityAsync(sessionId);
        var after = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));

        Assert.True(availability.IsReady, availability.Message);
        Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(after));
    }

    private static async Task<ViewerDataLocation> ProjectThreeDeletesAsync()
    {
        var location = await CreateDatabaseAsync(applyProjection: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var sessionId = await StartSessionAsync(repository);
        await repository.AppendRecordsAsync(
        [
            CreateRecord(sessionId, 1, DeleteXml(1, "one.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 2, DeleteXml(2, "two.txt"), LiveEventOutcome.DeleteFact),
            CreateRecord(sessionId, 3, DeleteXml(3, "three.txt"), LiveEventOutcome.DeleteFact)
        ]);
        using var service = new SqliteLiveProjectionService(location);
        var result = await service.ProjectSessionAsync(sessionId);
        Assert.True(result.Succeeded, result.FailureDetail);
        return location;
    }

    private static async Task<ViewerDataLocation> CreateDatabaseAsync(
        bool applyProjection,
        Func<string, string>? projectionTransform = null)
    {
        var directory = Path.Combine(
            ViewerDataLocation.DefaultRoot,
            "tests",
            $"projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var location = ViewerDataLocation.CreateForTesting(
            Path.Combine(directory, "viewer.db"),
            Path.Combine(directory, "jsonl"));
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = location.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var names = new List<string>
        {
            "schema.sql",
            "0002_phase_1b_offline_import.sql",
            "0003_phase_2a_live_monitoring.sql",
            "0004_phase_2b_live_evidence.sql"
        };
        var scripts = new List<string>();
        foreach (var name in names)
        {
            scripts.Add(await File.ReadAllTextAsync(Path.Combine(fixtures, name)));
        }

        if (applyProjection)
        {
            var projection = (await File.ReadAllTextAsync(
                    Path.Combine(fixtures, "0005_phase_2b4_live_projection.sql")))
                .ReplaceLineEndings("\n");
            scripts.Add(projectionTransform?.Invoke(projection) ?? projection);
        }

        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, scripts);
        await command.ExecuteNonQueryAsync();
        return location;
    }

    private static async Task<string> StartSessionAsync(
        SqliteLiveMonitoringRepository repository)
    {
        var sessionId = $"projection-session-{Guid.NewGuid():N}";
        await repository.StartCaptureSessionAsync(
            new LiveCaptureSessionStart(
                sessionId,
                StartedUtc,
                2_048,
                "phase-2b4-tests"));
        return sessionId;
    }

    private static LiveCaptureRecord CreateRecord(
        string sessionId,
        long sequence,
        string xml,
        LiveEventOutcome outcome)
    {
        var parsed = new WindowsEventXmlParser().Parse(xml);
        var raw = parsed.RawEvent
            ?? throw new InvalidOperationException(parsed.Error?.Message);
        return new LiveCaptureRecord(
            LiveEvidenceIdentity.Create(sessionId, sequence),
            sessionId,
            sequence,
            raw.EventRecordId,
            raw.ProviderName,
            raw.ChannelName ?? throw new InvalidOperationException("fixture channel"),
            raw.ComputerName,
            raw.EventTimeUtc,
            StartedUtc.AddSeconds(sequence),
            xml,
            SHA256.HashData(Encoding.UTF8.GetBytes(xml)),
            raw.RawEventId,
            raw.EventId,
            outcome,
            null,
            null);
    }

    private static LiveProjectionQuery Query(string sessionId) =>
        new(sessionId, null, null, null, false, new PageRequest(0, 50));

    private static async Task<long> CountOfflineCoreRowsAsync(
        ViewerDataLocation location) =>
        await ScalarAsync(
            location,
            """
            SELECT
                (SELECT COUNT(*) FROM raw_events)
              + (SELECT COUNT(*) FROM delete_events)
              + (SELECT COUNT(*) FROM delete_sessions)
              + (SELECT COUNT(*) FROM channel_epochs)
              + (SELECT COUNT(*) FROM import_sessions);
            """);

    private static async Task<long> ScalarAsync(
        ViewerDataLocation location,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = WritableConnection(location);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        ViewerDataLocation location,
        string sql)
    {
        await using var connection = WritableConnection(location);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadSessionIdAsync(
        ViewerDataLocation location)
    {
        await using var connection = WritableConnection(location);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT live_session_id
            FROM live_capture_sessions
            LIMIT 1;
            """;
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("fixture session"));
    }

    private static async Task<string[]> ReadOfflineDefinitionsAsync(
        ViewerDataLocation location)
    {
        await using var connection = WritableConnection(location);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name || ':' || COALESCE(sql, '')
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                  'raw_events',
                  'delete_events',
                  'delete_sessions',
                  'channel_epochs',
                  'import_sessions')
            ORDER BY name;
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows.ToArray();
    }

    private static SqliteConnection WritableConnection(ViewerDataLocation location) =>
        new(
            new SqliteConnectionStringBuilder
            {
                DataSource = location.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());

    private static string ReplaceRequiredOnce(
        string value,
        string oldValue,
        string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0
            || value.IndexOf(
                oldValue,
                index + oldValue.Length,
                StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "Fixture mutation target must occur exactly once.");
        }

        return string.Concat(
            value.AsSpan(0, index),
            newValue,
            value.AsSpan(index + oldValue.Length));
    }

    private static string DeleteXml(long recordId, string fileName) =>
        SysmonXml(
            26,
            recordId,
            $$"""
            <Data Name="UtcTime">2026-07-29 09:00:{{recordId % 60:00}}.000</Data>
            <Data Name="ProcessGuid">{11111111-1111-1111-1111-{{recordId:000000000000}}}</Data>
            <Data Name="ProcessId">4242</Data>
            <Data Name="Image">C:\Fixture\fixture-process.exe</Data>
            <Data Name="TargetFilename">C:\Fixture\{{fileName}}</Data>
            <Data Name="User">LAB\analyst</Data>
            """);

    private static string ProcessXml(long recordId) =>
        SysmonXml(
            1,
            recordId,
            $$"""
            <Data Name="UtcTime">2026-07-29 09:00:{{recordId % 60:00}}.000</Data>
            <Data Name="ProcessGuid">{22222222-2222-2222-2222-{{recordId:000000000000}}}</Data>
            <Data Name="ProcessId">4242</Data>
            <Data Name="Image">C:\Fixture\fixture-process.exe</Data>
            <Data Name="CommandLine">fixture-process.exe --fixture</Data>
            <Data Name="ParentProcessGuid">{33333333-3333-3333-3333-333333333333}</Data>
            <Data Name="ParentProcessId">100</Data>
            <Data Name="ParentImage">C:\Fixture\parent.exe</Data>
            <Data Name="User">LAB\analyst</Data>
            """);

    private static string SecurityXml(long recordId) =>
        SecurityXmlCore(
            recordId,
            """
            <Data Name="AccessMask">0x10000</Data>
            <Data Name="AccessList">DELETE</Data>
            """);

    private static string NonDeleteSecurityXml(long recordId) =>
        SecurityXmlCore(
            recordId,
            """
            <Data Name="AccessMask">0x1</Data>
            <Data Name="AccessList">ReadData</Data>
            """);

    private static string SecurityXmlCore(long recordId, string access) =>
        $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Security-Auditing" />
            <EventID>4663</EventID>
            <EventRecordID>{recordId}</EventRecordID>
            <TimeCreated SystemTime="2026-07-29T09:00:{recordId % 60:00}.0000000Z" />
            <Channel>Security</Channel>
            <Computer>LAB-PC</Computer>
          </System>
          <EventData>
            <Data Name="SubjectUserSid">S-1-5-21-1000</Data>
            <Data Name="SubjectUserName">analyst</Data>
            <Data Name="ObjectName">C:\Fixture\secured.txt</Data>
            <Data Name="ProcessId">0x1092</Data>
            <Data Name="ProcessName">C:\Fixture\fixture-process.exe</Data>
            {access}
          </EventData>
        </Event>
        """;

    private static string SysmonXml(
        int eventId,
        long recordId,
        string eventData) =>
        $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Sysmon" />
            <EventID>{eventId}</EventID>
            <EventRecordID>{recordId}</EventRecordID>
            <TimeCreated SystemTime="2026-07-29T09:00:{recordId % 60:00}.0000000Z" />
            <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
            <Computer>LAB-PC</Computer>
          </System>
          <EventData>
            {eventData}
          </EventData>
        </Event>
        """;
}
