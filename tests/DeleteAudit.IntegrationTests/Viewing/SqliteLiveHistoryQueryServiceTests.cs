using System.Security.Cryptography;
using System.Text;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.LiveMonitoring;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.Viewing;

/// <summary>
/// Every test runs against a real temporary SQLite database built from the checked-in
/// schema and migrations. Nothing here reads a real Windows event log.
/// </summary>
public sealed class SqliteLiveHistoryQueryServiceTests
{
    private static readonly DateTimeOffset StartedUtc =
        new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingDatabaseIsReportedWithoutCreatingOne()
    {
        var location = CreateLocation();
        var service = new SqliteLiveHistoryQueryService(location);

        var availability = await service.GetAvailabilityAsync();

        Assert.Equal(LiveHistoryState.MissingDatabase, availability.State);
        Assert.False(File.Exists(location.DatabasePath));
    }

    /// <summary>
    /// A database that predates the live evidence migration must fail closed here and
    /// name the migration, without disturbing any other page.
    /// </summary>
    [Fact]
    public async Task MissingLiveEvidenceTablesFailClosedAndNameTheMigration()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: false);
        var service = new SqliteLiveHistoryQueryService(location);

        var availability = await service.GetAvailabilityAsync();

        Assert.Equal(LiveHistoryState.MissingSchema, availability.State);
        Assert.Contains("live_capture_sessions", availability.MissingObjects);
        Assert.Contains(
            "0004_phase_2b_live_evidence.sql",
            availability.Message,
            StringComparison.Ordinal);

        // The page reports it as a state; queries still fail closed rather than lie.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetSessionsAsync(SessionQuery()));
    }

    [Fact]
    public async Task CompletedAndIncompleteSessionsAreDistinguished()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var completed = await StartCaptureAsync(repository, StartedUtc);
        await repository.AppendRecordsAsync([Record(completed, 1)]);
        await CompleteAsync(repository, completed, LiveMonitoringState.Stopped, 1);
        // Started but never completed: an abrupt termination leaves exactly this.
        var abandoned = await StartCaptureAsync(
            repository,
            StartedUtc.AddMinutes(5));

        var service = new SqliteLiveHistoryQueryService(location);
        var page = await service.GetSessionsAsync(SessionQuery());

        Assert.Equal(2, page.TotalCount);
        // Newest first.
        Assert.Equal(abandoned, page.Items[0].LiveSessionId);
        Assert.False(page.Items[0].IsComplete);
        Assert.Null(page.Items[0].FinalState);
        Assert.Equal(completed, page.Items[1].LiveSessionId);
        Assert.True(page.Items[1].IsComplete);
        Assert.Equal("stopped", page.Items[1].FinalState);
        Assert.Equal(1, page.Items[1].StoredRecordCount);
        Assert.Equal(1, page.Items[1].PersistedRecordCount);
    }

    [Fact]
    public async Task IncompleteSessionsCanBeFilteredOnTheirOwn()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var completed = await StartCaptureAsync(repository, StartedUtc);
        await CompleteAsync(repository, completed, LiveMonitoringState.Error, 0);
        var abandoned = await StartCaptureAsync(repository, StartedUtc.AddMinutes(1));
        var service = new SqliteLiveHistoryQueryService(location);

        var incomplete = await service.GetSessionsAsync(
            SessionQuery(state: LiveHistorySessionState.Incomplete));
        var errored = await service.GetSessionsAsync(
            SessionQuery(state: LiveHistorySessionState.Error));

        Assert.Equal(abandoned, Assert.Single(incomplete.Items).LiveSessionId);
        Assert.Equal(completed, Assert.Single(errored.Items).LiveSessionId);
    }

    [Fact]
    public async Task RecordListsNeverCarryTheRawXmlItself()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        var payload = $"<Event>{new string('p', 5_000)}</Event>";
        await repository.AppendRecordsAsync([Record(session, 1, payload)]);
        var service = new SqliteLiveHistoryQueryService(location);

        var page = await service.GetRecordsAsync(RecordQuery(session));

        var row = Assert.Single(page.Items);
        Assert.Equal(payload.Length, row.RawXmlLength);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            row.RawXmlSha256);
        // The row type has no member that could carry the XML.
        Assert.Null(typeof(LiveCaptureRecordRow).GetProperty("RawXml"));
    }

    [Fact]
    public async Task RawXmlIsLoadedOnDemandAndTruncatedInsideSqlite()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        var oversized =
            $"<Event>{new string('p', RawXmlDocument.MaxPreviewCharacters)}</Event>";
        await repository.AppendRecordsAsync([Record(session, 1, oversized)]);
        var service = new SqliteLiveHistoryQueryService(location);

        var document = await service.GetRecordRawXmlAsync(
            LiveEvidenceIdentity.Create(session, 1));

        Assert.NotNull(document);
        Assert.True(document!.IsAvailable);
        Assert.True(document.IsTruncated);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, document.PreviewLength);
        Assert.Equal(oversized.Length, document.OriginalLength);
        Assert.True(document.IsReadOnly);
    }

    [Fact]
    public async Task UnknownRecordReturnsNullRatherThanThrowing()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var service = new SqliteLiveHistoryQueryService(location);

        Assert.Null(await service.GetRecordRawXmlAsync("no-such-record:1"));
    }

    [Fact]
    public async Task RecordsAreFilteredAndOrderedDeterministically()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        await repository.AppendRecordsAsync(
        [
            Record(session, 1, outcome: LiveEventOutcome.DeleteFact),
            Record(session, 2, outcome: LiveEventOutcome.Ignored),
            Record(session, 3, outcome: LiveEventOutcome.Error, errorCode: "parse_malformed"),
            Record(session, 4, outcome: LiveEventOutcome.DeleteFact)
        ]);
        var service = new SqliteLiveHistoryQueryService(location);

        var deletes = await service.GetRecordsAsync(
            RecordQuery(session, outcome: LiveCaptureOutcomes.DeleteFact));
        var errorsOnly = await service.GetRecordsAsync(
            RecordQuery(session, errorsOnly: true));
        var descending = await service.GetRecordsAsync(
            RecordQuery(session, descending: true));

        Assert.Equal([1L, 4L], deletes.Items.Select(item => item.ReceivedSequence));
        Assert.All(deletes.Items, item => Assert.True(item.EstablishesDeleteFact));
        Assert.Equal("parse_malformed", Assert.Single(errorsOnly.Items).ErrorCode);
        Assert.Equal(
            [4L, 3L, 2L, 1L],
            descending.Items.Select(item => item.ReceivedSequence));
    }

    [Fact]
    public async Task RecordPagingIsStableAcrossPages()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        for (var batch = 0; batch < 5; batch++)
        {
            await repository.AppendRecordsAsync(
                Enumerable
                    .Range(1, 10)
                    .Select(index => Record(session, (batch * 10) + index))
                    .ToArray());
        }

        var service = new SqliteLiveHistoryQueryService(location);
        var first = await service.GetRecordsAsync(
            RecordQuery(session, page: new PageRequest(0, 20)));
        var second = await service.GetRecordsAsync(
            RecordQuery(session, page: new PageRequest(20, 20)));

        Assert.Equal(50, first.TotalCount);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(20, second.Items.Count);
        Assert.True(first.HasNext);
        Assert.True(second.HasPrevious);
        // No page boundary repeats or skips a record.
        Assert.Empty(first.Items
            .Select(item => item.LiveEvidenceId)
            .Intersect(second.Items.Select(item => item.LiveEvidenceId), StringComparer.Ordinal));
        Assert.Equal(21, second.Items[0].ReceivedSequence);
    }

    [Fact]
    public async Task SessionDiagnosticsAreReturnedInOrder()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        await CompleteAsync(
            repository,
            session,
            LiveMonitoringState.Error,
            0,
            [
                new LiveMonitoringDiagnostic(
                    "live_queue_overflow",
                    "queue full",
                    ImportDiagnosticSeverity.Warning,
                    "queue",
                    StartedUtc),
                new LiveMonitoringDiagnostic(
                    "live_watcher_failed",
                    "watcher failed",
                    ImportDiagnosticSeverity.Error,
                    "receive",
                    StartedUtc.AddSeconds(1))
            ]);
        var service = new SqliteLiveHistoryQueryService(location);

        var diagnostics = await service.GetSessionDiagnosticsAsync(session);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("live_queue_overflow", diagnostics[0].Code);
        Assert.Equal(ImportDiagnosticSeverity.Warning, diagnostics[0].Severity);
        Assert.Equal("live_watcher_failed", diagnostics[1].Code);
        Assert.Equal(ImportDiagnosticSeverity.Error, diagnostics[1].Severity);
    }

    /// <summary>
    /// The whole page is a reader. Browsing must not change a single byte, including the
    /// evidence, the schema, or any bookkeeping column.
    /// </summary>
    [Fact]
    public async Task BrowsingHistoryLeavesTheDatabaseByteIdentical()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        await repository.AppendRecordsAsync([Record(session, 1)]);
        await CompleteAsync(repository, session, LiveMonitoringState.Stopped, 1);
        var before = await File.ReadAllBytesAsync(location.DatabasePath);

        var service = new SqliteLiveHistoryQueryService(location);
        await service.GetAvailabilityAsync();
        await service.GetSessionsAsync(SessionQuery());
        await service.GetRecordsAsync(RecordQuery(session));
        await service.GetSessionDiagnosticsAsync(session);
        await service.GetRecordRawXmlAsync(LiveEvidenceIdentity.Create(session, 1));

        Assert.Equal(before, await File.ReadAllBytesAsync(location.DatabasePath));
    }

    [Fact]
    public async Task FilterValuesAreParameterisedNotConcatenated()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        await repository.AppendRecordsAsync([Record(session, 1)]);
        var service = new SqliteLiveHistoryQueryService(location);

        // A value that would end the statement if it were concatenated.
        var page = await service.GetRecordsAsync(RecordQuery(
            session,
            providerContains: "'; DROP TABLE live_capture_records; --"));

        Assert.Empty(page.Items);
        // The table is still there and still holds the record.
        var survived = await service.GetRecordsAsync(RecordQuery(session));
        Assert.Single(survived.Items);
    }

    [Fact]
    public async Task WildcardCharactersInAFilterAreMatchedLiterally()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository, StartedUtc);
        await repository.AppendRecordsAsync([Record(session, 1)]);
        var service = new SqliteLiveHistoryQueryService(location);

        var page = await service.GetRecordsAsync(
            RecordQuery(session, providerContains: "%"));

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task AnUnknownOutcomeFilterIsRejectedBeforeAnyQueryRuns()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var service = new SqliteLiveHistoryQueryService(location);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRecordsAsync(RecordQuery("s", outcome: "not_an_outcome")));
    }

    [Fact]
    public async Task ACancelledQueryDoesNotReturnAResult()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applyLiveMigrations: true);
        var service = new SqliteLiveHistoryQueryService(location);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetSessionsAsync(SessionQuery(), cancellation.Token));
    }

    private static LiveHistorySessionQuery SessionQuery(
        LiveHistorySessionState? state = null,
        PageRequest? page = null) =>
        new(null, null, state, page ?? new PageRequest(0, 50));

    private static LiveHistoryRecordQuery RecordQuery(
        string sessionId,
        string? outcome = null,
        string? providerContains = null,
        bool errorsOnly = false,
        bool descending = false,
        PageRequest? page = null) =>
        new(
            sessionId,
            null,
            null,
            outcome,
            providerContains,
            null,
            null,
            null,
            null,
            errorsOnly,
            null,
            null,
            descending,
            page ?? new PageRequest(0, 50));

    private static LiveCaptureRecord Record(
        string sessionId,
        long sequence,
        string rawXml = "<Event />",
        LiveEventOutcome outcome = LiveEventOutcome.Ignored,
        string? errorCode = null) =>
        new(
            LiveEvidenceIdentity.Create(sessionId, sequence),
            sessionId,
            sequence,
            1000 + sequence,
            LiveMonitoringChannels.SysmonProvider,
            LiveMonitoringChannels.SysmonOperational,
            "LAB-PC",
            StartedUtc,
            StartedUtc,
            rawXml,
            SHA256.HashData(Encoding.UTF8.GetBytes(rawXml)),
            "raw-1",
            26,
            outcome,
            errorCode,
            null);

    private static async Task<string> StartCaptureAsync(
        SqliteLiveMonitoringRepository repository,
        DateTimeOffset startedUtc)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        await repository.StartCaptureSessionAsync(
            new LiveCaptureSessionStart(sessionId, startedUtc, 2048, "history-tests"));
        return sessionId;
    }

    private static async Task CompleteAsync(
        SqliteLiveMonitoringRepository repository,
        string sessionId,
        LiveMonitoringState finalState,
        long persistedRecordCount,
        IReadOnlyList<LiveMonitoringDiagnostic>? diagnostics = null)
    {
        var counters = new LiveMonitoringCounters(
            Received: persistedRecordCount,
            Ignored: persistedRecordCount);
        var stoppedUtc = StartedUtc.AddMinutes(1);
        await repository.CompleteSessionAsync(
            new LiveCaptureCompletion(
                sessionId,
                stoppedUtc,
                finalState,
                counters,
                persistedRecordCount),
            new LiveMonitoringSession(
                sessionId,
                StartedUtc,
                stoppedUtc,
                [],
                counters,
                finalState,
                2048,
                "history-tests"),
            diagnostics ?? []);
    }

    private static ViewerDataLocation CreateLocation()
    {
        var directory = Path.Combine(
            ViewerDataLocation.DefaultRoot,
            "tests",
            $"history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return ViewerDataLocation.CreateForTesting(
            Path.Combine(directory, "viewer.db"),
            Path.Combine(directory, "jsonl"));
    }

    private static async Task CreateDatabaseAsync(
        ViewerDataLocation location,
        bool applyLiveMigrations)
    {
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
        var scripts = new List<string>
        {
            await File.ReadAllTextAsync(Path.Combine(fixtures, "schema.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0002_phase_1b_offline_import.sql"))
        };
        if (applyLiveMigrations)
        {
            scripts.Add(await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0003_phase_2a_live_monitoring.sql")));
            scripts.Add(await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0004_phase_2b_live_evidence.sql")));
        }

        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, scripts);
        await command.ExecuteNonQueryAsync();
    }
}
