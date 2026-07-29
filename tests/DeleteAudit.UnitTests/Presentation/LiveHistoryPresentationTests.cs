using DeleteAudit.Application.Presentation;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.UnitTests.Presentation;

/// <summary>
/// The live history page is a reader. These tests drive the real view model through a
/// hand-written fake service; nothing here touches SQLite or a Windows event log.
/// </summary>
public sealed class LiveHistoryPresentationTests
{
    private static readonly DateTimeOffset StartedUtc =
        new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConstructionPerformsNoQuery()
    {
        var service = new FakeLiveHistoryQueryService();

        using var viewModel = new LiveHistoryViewModel(service);

        Assert.Equal(0, service.AvailabilityCalls);
        Assert.Equal(0, service.SessionCalls);
        Assert.Equal(0, service.RecordCalls);
        Assert.Empty(viewModel.Sessions);
        Assert.False(viewModel.IsUnavailable);
    }

    [Fact]
    public async Task AnUnusableDatabaseBecomesAStateNotAnException()
    {
        var service = new FakeLiveHistoryQueryService
        {
            Availability = new LiveHistoryAvailability(
                LiveHistoryState.MissingSchema,
                "缺少 live_capture_records。",
                ["live_capture_records"])
        };
        using var viewModel = new LiveHistoryViewModel(service);

        await viewModel.LoadSessionsAsync();

        Assert.True(viewModel.IsUnavailable);
        Assert.Contains("live_capture_records", viewModel.UnavailableMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.Sessions);
        // The unusable schema is reported as a state, not as a failed operation.
        Assert.False(viewModel.HasError);
        Assert.Equal(0, service.SessionCalls);
    }

    [Fact]
    public async Task SessionsLoadAndPageForward()
    {
        var service = new FakeLiveHistoryQueryService();
        service.SetSessions(Enumerable.Range(1, 120).Select(Session).ToArray());
        using var viewModel = new LiveHistoryViewModel(service);

        await viewModel.LoadSessionsAsync();

        Assert.Equal(50, viewModel.Sessions.Count);
        Assert.True(viewModel.HasNextSessionPage);
        Assert.False(viewModel.HasPreviousSessionPage);

        await viewModel.NextSessionPageCommand.ExecuteAsync();

        Assert.Equal(50, viewModel.Sessions.Count);
        Assert.True(viewModel.HasPreviousSessionPage);
        Assert.Contains("120", viewModel.SessionPageStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyResultIsAnEmptyStateNotAnError()
    {
        var service = new FakeLiveHistoryQueryService();
        using var viewModel = new LiveHistoryViewModel(service);

        await viewModel.LoadSessionsAsync();

        Assert.True(viewModel.IsSessionListEmpty);
        Assert.False(viewModel.HasSessions);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task SelectingASessionLoadsItsRecordsAndDiagnostics()
    {
        var service = new FakeLiveHistoryQueryService();
        service.SetSessions([Session(1)]);
        service.SetRecords([Record(1), Record(2)]);
        service.SetDiagnostics([Diagnostic("live_queue_overflow")]);
        using var viewModel = new LiveHistoryViewModel(service);
        await viewModel.LoadSessionsAsync();

        // Selecting the session is what loads its records; no explicit call is needed.
        viewModel.SelectedSession = viewModel.Sessions[0];

        Assert.Equal(2, viewModel.Records.Count);
        Assert.True(viewModel.HasDiagnostics);
        Assert.Equal("live_queue_overflow", viewModel.Diagnostics[0].Code);
    }

    [Fact]
    public async Task RawXmlIsOnlyFetchedWhenARecordIsSelected()
    {
        var service = new FakeLiveHistoryQueryService();
        service.SetSessions([Session(1)]);
        service.SetRecords([Record(1)]);
        using var viewModel = new LiveHistoryViewModel(service);
        await viewModel.LoadSessionsAsync();
        viewModel.SelectedSession = viewModel.Sessions[0];

        Assert.Equal(0, service.RawXmlCalls);
        Assert.False(viewModel.HasRawXml);

        // Selecting the record is what fetches the XML, and only then.
        viewModel.SelectedRecord = viewModel.Records[0];

        Assert.Equal(1, service.RawXmlCalls);
        Assert.True(viewModel.HasRawXml);
        Assert.Contains("<Event", viewModel.RawXmlPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedRawXmlSaysSoAndKeepsTheOriginalLength()
    {
        var service = new FakeLiveHistoryQueryService
        {
            RawXml = RawXmlDocument.CreatePreview(
                "evidence-1",
                new string('p', RawXmlDocument.MaxPreviewCharacters),
                RawXmlDocument.MaxPreviewCharacters * 2L)
        };
        service.SetSessions([Session(1)]);
        service.SetRecords([Record(1)]);
        using var viewModel = new LiveHistoryViewModel(service);
        await viewModel.LoadSessionsAsync();
        viewModel.SelectedSession = viewModel.Sessions[0];
        await viewModel.LoadRecordsAsync();
        viewModel.SelectedRecord = viewModel.Records[0];

        await viewModel.LoadRawXmlAsync();

        Assert.True(viewModel.RawXmlIsTruncated);
        Assert.Contains("262,144", viewModel.RawXmlTruncationNotice, StringComparison.Ordinal);
        Assert.Contains("524,288", viewModel.RawXmlLengthSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A slow query whose filters have already been replaced must not land on top of the
    /// newer result.
    /// </summary>
    [Fact]
    public async Task AStaleSessionResultNeverOverwritesANewerOne()
    {
        var service = new FakeLiveHistoryQueryService();
        service.SetSessions([Session(1)]);
        using var gate = new ManualResetEventSlim(false);
        service.SessionGate = gate;
        using var viewModel = new LiveHistoryViewModel(service);

        var slow = viewModel.LoadSessionsAsync();
        await service.SessionEntered.WaitAsync(TimeSpan.FromSeconds(10));

        // While the first query is parked, the data set changes and a newer load starts.
        service.SessionGate = null;
        service.SetSessions([Session(7), Session(8)]);
        gate.Set();
        await slow;
        await viewModel.LoadSessionsAsync();

        Assert.Equal(2, viewModel.Sessions.Count);
        Assert.Equal("session-7", viewModel.Sessions[0].LiveSessionId);
    }

    /// <summary>
    /// A second load started while one is still running is dropped rather than run
    /// concurrently, so two queries can never race to publish into the same list.
    /// </summary>
    [Fact]
    public async Task ASecondLoadDoesNotRunWhileOneIsStillInFlight()
    {
        var service = new FakeLiveHistoryQueryService();
        service.SetSessions([Session(1)]);
        using var gate = new ManualResetEventSlim(false);
        service.SessionGate = gate;
        using var viewModel = new LiveHistoryViewModel(service);

        var first = viewModel.LoadSessionsAsync();
        await service.SessionEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var second = viewModel.LoadSessionsAsync();
        gate.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, service.SessionCalls);
        Assert.Single(viewModel.Sessions);
    }

    [Fact]
    public async Task DisposeCancelsWhateverIsStillRunning()
    {
        var service = new FakeLiveHistoryQueryService();
        service.SetSessions([Session(1)]);
        using var gate = new ManualResetEventSlim(false);
        service.SessionGate = gate;
        var viewModel = new LiveHistoryViewModel(service);

        var loading = viewModel.LoadSessionsAsync();
        await service.SessionEntered.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.Dispose();
        gate.Set();
        await loading;

        Assert.True(service.ObservedCancellation);
        // Disposing twice is safe.
        viewModel.Dispose();
    }

    [Fact]
    public async Task AnOversizedPageIsRejectedInsteadOfRendered()
    {
        var service = new FakeLiveHistoryQueryService { OverridePageSize = 51 };
        service.SetSessions(Enumerable.Range(1, 60).Select(Session).ToArray());
        using var viewModel = new LiveHistoryViewModel(service);

        await viewModel.LoadSessionsAsync();

        Assert.True(viewModel.HasError);
        Assert.Contains("每页上限", viewModel.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void DisclosureStatesTheReadOnlyAndNoMonitoringBoundaries()
    {
        foreach (var required in new[]
                 {
                     "只读", "不会开始实时监控", "不上传", "删除", "防篡改"
                 })
        {
            Assert.Contains(
                required,
                LiveHistoryViewModel.HistoryDisclosure,
                StringComparison.Ordinal);
        }
    }

    /// <summary>The view model exposes no way to change or remove stored evidence.</summary>
    [Fact]
    public void TheViewModelExposesNoMutatingOperation()
    {
        var names = typeof(LiveHistoryViewModel)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(names, name =>
            name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Clear", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Repair", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
        Assert.Null(typeof(ILiveHistoryQueryService).GetMethod("DeleteAsync"));
    }

    private static LiveCaptureSessionRow Session(int index) =>
        new(
            $"session-{index}",
            StartedUtc.AddMinutes(index),
            StartedUtc.AddMinutes(index + 1),
            "stopped",
            "tests",
            2048,
            10,
            2,
            3,
            1,
            3,
            1,
            0,
            0,
            0,
            10,
            10);

    private static LiveCaptureRecordRow Record(long sequence) =>
        new(
            $"session-1:{sequence}",
            "session-1",
            sequence,
            1000 + sequence,
            "Microsoft-Windows-Sysmon",
            "Microsoft-Windows-Sysmon/Operational",
            "LAB-PC",
            StartedUtc,
            StartedUtc,
            new string('A', 64),
            120,
            "raw-1",
            26,
            LiveCaptureOutcomes.DeleteFact,
            null,
            null);

    private static LiveCaptureDiagnosticRow Diagnostic(string code) =>
        new(
            "session-1:1",
            "session-1",
            "queue",
            ImportDiagnosticSeverity.Warning,
            code,
            "fixture diagnostic",
            StartedUtc);

    private sealed class FakeLiveHistoryQueryService : ILiveHistoryQueryService
    {
        private readonly TaskCompletionSource _sessionEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LiveCaptureSessionRow[] _sessions = [];
        private LiveCaptureRecordRow[] _records = [];
        private LiveCaptureDiagnosticRow[] _diagnostics = [];

        public LiveHistoryAvailability Availability { get; init; } =
            new(LiveHistoryState.Ready, "ready", []);

        public RawXmlDocument? RawXml { get; init; } =
            RawXmlDocument.CreatePreview("evidence-1", "<Event />", 9);

        public ManualResetEventSlim? SessionGate { get; set; }

        public int OverridePageSize { get; init; }

        public int AvailabilityCalls { get; private set; }

        public int SessionCalls { get; private set; }

        public int RecordCalls { get; private set; }

        public int RawXmlCalls { get; private set; }

        public bool ObservedCancellation { get; private set; }

        public Task SessionEntered => _sessionEntered.Task;

        public void SetSessions(LiveCaptureSessionRow[] sessions) => _sessions = sessions;

        public void SetRecords(LiveCaptureRecordRow[] records) => _records = records;

        public void SetDiagnostics(LiveCaptureDiagnosticRow[] diagnostics) =>
            _diagnostics = diagnostics;

        public Task<LiveHistoryAvailability> GetAvailabilityAsync(
            CancellationToken cancellationToken = default)
        {
            AvailabilityCalls++;
            return Task.FromResult(Availability);
        }

        public Task<PageResult<LiveCaptureSessionRow>> GetSessionsAsync(
            LiveHistorySessionQuery query,
            CancellationToken cancellationToken = default)
        {
            SessionCalls++;
            var gate = SessionGate;
            var limit = OverridePageSize == 0 ? query.Page.Limit : OverridePageSize;

            // Parking runs off the caller's thread: a real query never blocks the thread
            // that started it, and neither may the fake.
            return Task.Run(
                () =>
                {
                    if (gate is not null)
                    {
                        _sessionEntered.TrySetResult();
                        // A real signal; the timeout only guards a hung test.
                        gate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        ObservedCancellation = true;
                    }

                    var items = _sessions
                        .Skip(query.Page.Offset)
                        .Take(limit)
                        .ToArray();
                    return new PageResult<LiveCaptureSessionRow>(
                        items,
                        _sessions.Length,
                        query.Page.Offset,
                        query.Page.Limit);
                },
                CancellationToken.None);
        }

        public Task<PageResult<LiveCaptureRecordRow>> GetRecordsAsync(
            LiveHistoryRecordQuery query,
            CancellationToken cancellationToken = default)
        {
            RecordCalls++;
            var items = _records.Skip(query.Page.Offset).Take(query.Page.Limit).ToArray();
            return Task.FromResult(new PageResult<LiveCaptureRecordRow>(
                items,
                _records.Length,
                query.Page.Offset,
                query.Page.Limit));
        }

        public Task<IReadOnlyList<LiveCaptureDiagnosticRow>> GetSessionDiagnosticsAsync(
            string liveSessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LiveCaptureDiagnosticRow>>(_diagnostics);

        public Task<RawXmlDocument?> GetRecordRawXmlAsync(
            string liveEvidenceId,
            CancellationToken cancellationToken = default)
        {
            RawXmlCalls++;
            return Task.FromResult(RawXml);
        }
    }
}
