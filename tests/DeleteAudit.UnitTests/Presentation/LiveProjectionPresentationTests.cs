using DeleteAudit.Application.Presentation;
using DeleteAudit.Application.Projection;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Infrastructure;

namespace DeleteAudit.UnitTests.Presentation;

public sealed class LiveProjectionPresentationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectingSessionDoesNotStartProjectionOrQuery()
    {
        var service = new FakeProjectionService();
        using var viewModel = new LiveProjectionViewModel(service);

        viewModel.SetSession("session-1");

        Assert.Equal("session-1", viewModel.SelectedSessionId);
        Assert.Equal(0, service.ProjectCalls);
        Assert.Empty(service.Queries);
        Assert.Equal(0, service.VerifyCalls);
        Assert.True(viewModel.ProjectCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshUsesBoundedPagingAndNeverLoadsEverything()
    {
        var service = new FakeProjectionService
        {
            AutoPage = query => new PageResult<LiveProjectedRecordRow>(
                Enumerable.Range(0, query.Page.Limit)
                    .Select(index => Row(index + query.Page.Offset, query.LiveSessionId))
                    .ToArray(),
                120,
                query.Page.Offset,
                query.Page.Limit)
        };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");

        await viewModel.LoadAsync();
        await viewModel.NextPageCommand.ExecuteAsync();

        Assert.Equal(2, service.Queries.Count);
        Assert.All(service.Queries, query => Assert.Equal(50, query.Page.Limit));
        Assert.Equal(50, viewModel.Records.Count);
        Assert.Contains("第 2 / 3 页", viewModel.PageStatus, StringComparison.Ordinal);
        Assert.True(viewModel.HasPreviousPage);
        Assert.True(viewModel.HasNextPage);
    }

    [Fact]
    public async Task FiltersArePassedAsDataAndResetThePage()
    {
        var service = new FakeProjectionService
        {
            AutoPage = query => new PageResult<LiveProjectedRecordRow>(
                [],
                0,
                query.Page.Offset,
                query.Page.Limit)
        };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");
        viewModel.Source = LiveProjectionSources.SysmonDelete;
        viewModel.PathContains = "fixture";
        viewModel.ProcessContains = "process";
        viewModel.Descending = true;

        await viewModel.ApplyFiltersCommand.ExecuteAsync();

        var query = Assert.Single(service.Queries);
        Assert.Equal(LiveProjectionSources.SysmonDelete, query.Source);
        Assert.Equal("fixture", query.PathContains);
        Assert.Equal("process", query.ProcessContains);
        Assert.True(query.Descending);
        Assert.Equal(0, query.Page.Offset);
        Assert.True(viewModel.IsEmpty);
    }

    [Fact]
    public async Task MissingSchemaIsAVisibleUnavailableState()
    {
        var service = new FakeProjectionService
        {
            Availability = new LiveProjectionAvailability(
                LiveProjectionState.MissingSchema,
                "Apply 0005 explicitly.",
                ["live_projected_records"])
        };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsUnavailable);
        Assert.Contains("0005", viewModel.AvailabilityMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.Records);
        Assert.Empty(service.Queries);
    }

    [Fact]
    public async Task FailedProjectionCannotAppearSuccessful()
    {
        var service = new FakeProjectionService
        {
            ProjectionResult = new LiveProjectionRunResult(
                "session-1",
                3,
                0,
                0,
                false,
                "source_digest_mismatch",
                "digest mismatch")
        };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");

        await viewModel.ProjectAsync();

        Assert.True(viewModel.LastRunFailed);
        Assert.True(viewModel.HasError);
        Assert.Contains("投影失败", viewModel.LastRunSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("投影完成", viewModel.LastRunSummary, StringComparison.Ordinal);
        Assert.Empty(service.Queries);
    }

    [Fact]
    public async Task SuccessfulReplayStatesThatNoDuplicateWasCreated()
    {
        var service = new FakeProjectionService
        {
            ProjectionResult = new LiveProjectionRunResult(
                "session-1",
                2,
                0,
                2,
                true,
                null,
                null),
            AutoPage = query => new PageResult<LiveProjectedRecordRow>(
                [Row(1, query.LiveSessionId), Row(2, query.LiveSessionId)],
                2,
                query.Page.Offset,
                query.Page.Limit),
            Continuity = new LiveContinuityStatus(
                "session-1",
                2,
                true,
                null,
                null,
                "continuity only; not tamper-proof")
        };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");

        await viewModel.ProjectAsync();

        Assert.False(viewModel.LastRunFailed);
        Assert.Contains("没有生成重复记录", viewModel.LastRunSummary, StringComparison.Ordinal);
        Assert.Contains("连续性验证通过", viewModel.ContinuitySummary, StringComparison.Ordinal);
        Assert.Equal(2, viewModel.Records.Count);
    }

    [Fact]
    public async Task NewerQueryWinsAndStaleCompletionIsDiscarded()
    {
        var service = new FakeProjectionService { DelayQueries = true };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");

        var first = viewModel.LoadAsync();
        await service.WaitForQueriesAsync(1);
        viewModel.PathContains = "new";
        var second = viewModel.LoadAsync();
        await service.WaitForQueriesAsync(2);
        service.CompleteQuery(
            2,
            new PageResult<LiveProjectedRecordRow>(
                [Row(2, "session-1")],
                1,
                0,
                50));
        await second;
        service.CompleteQuery(
            1,
            new PageResult<LiveProjectedRecordRow>(
                [Row(1, "session-1")],
                1,
                0,
                50));
        await first;

        Assert.Equal(2, Assert.Single(viewModel.Records).LiveIngestSequence);
        Assert.Contains(1, service.CancelledQueries);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task SessionChangeCancelsRequestAndRejectsItsResult()
    {
        var service = new FakeProjectionService { DelayQueries = true };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-old");
        var load = viewModel.LoadAsync();
        await service.WaitForQueriesAsync(1);

        viewModel.SetSession("session-new");
        service.CompleteQuery(
            1,
            new PageResult<LiveProjectedRecordRow>(
                [Row(1, "session-old")],
                1,
                0,
                50));
        await load;

        Assert.Equal("session-new", viewModel.SelectedSessionId);
        Assert.Empty(viewModel.Records);
        Assert.Contains(1, service.CancelledQueries);
    }

    [Fact]
    public async Task DisposeCancelsAndNoLatePropertyOrRecordUpdateOccurs()
    {
        var service = new FakeProjectionService { DelayQueries = true };
        var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var load = viewModel.LoadAsync();
        await service.WaitForQueriesAsync(1);
        viewModel.Dispose();
        changes.Clear();

        service.CompleteQuery(
            1,
            new PageResult<LiveProjectedRecordRow>(
                [Row(1, "session-1")],
                1,
                0,
                50));
        await load;

        Assert.Empty(viewModel.Records);
        Assert.Empty(changes);
        Assert.Contains(1, service.CancelledQueries);
    }

    [Fact]
    public async Task OversizedServicePageIsRejectedAndNotRendered()
    {
        var service = new FakeProjectionService
        {
            AutoPage = query => new PageResult<LiveProjectedRecordRow>(
                Enumerable.Range(0, 51)
                    .Select(index => Row(index, query.LiveSessionId))
                    .ToArray(),
                51,
                0,
                50)
        };
        using var viewModel = new LiveProjectionViewModel(service);
        viewModel.SetSession("session-1");

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Records);
        Assert.True(viewModel.HasError);
        Assert.Contains("超过每页", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionPageIsVirtualizedAccessibleAndDisclosesEvidenceBoundary()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot.Value,
            "src",
            "DeleteAudit.Viewer",
            "MainWindow.xaml"));

        Assert.Contains("Header=\"实时规范投影\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"实时规范投影记录列表\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource VirtualizedDataGrid}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding LiveProjection.Records}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding LiveProjection.Disclosure}\"",
            xaml,
            StringComparison.Ordinal);

        var disclosure = LiveProjectionViewModel.ProjectionDisclosure;
        Assert.Contains("live_evidence_id", disclosure, StringComparison.Ordinal);
        Assert.Contains("不写入或伪装成离线", disclosure, StringComparison.Ordinal);
        Assert.Contains("不会开始监控", disclosure, StringComparison.Ordinal);
        Assert.Contains("不具备防篡改能力", disclosure, StringComparison.Ordinal);
    }

    private static LiveProjectedRecordRow Row(int sequence, string sessionId) =>
        new(
            $"live-projection:{sessionId}:{sequence}",
            $"{sessionId}:{sequence}",
            sessionId,
            "live-epoch:fixture",
            sequence,
            sequence,
            sequence,
            "Microsoft-Windows-Sysmon",
            "Microsoft-Windows-Sysmon/Operational",
            "LAB-PC",
            Timestamp.AddSeconds(sequence),
            Timestamp.AddSeconds(sequence),
            LiveProjectionSources.SysmonDelete,
            $@"C:\Fixture\item-{sequence}.txt",
            "unknown",
            42,
            @"C:\Fixture\process.exe",
            null,
            null,
            null,
            null,
            null,
            "LAB\\analyst",
            null,
            "not_observed",
            false,
            "[]",
            new string('A', 64),
            new string('B', 64),
            new string('C', 64),
            sequence == 1 ? null : new string('D', 64),
            Timestamp);

    private sealed class FakeProjectionService : ILiveProjectionService
    {
        private readonly object _sync = new();
        private readonly List<PendingQuery> _pending = [];
        private readonly TaskCompletionSource _queryChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LiveProjectionAvailability Availability { get; set; } =
            new(LiveProjectionState.Ready, "ready", []);

        public LiveProjectionRunResult ProjectionResult { get; set; } =
            new("session-1", 0, 0, 0, true, null, null);

        public LiveContinuityStatus Continuity { get; set; } =
            new("session-1", 0, true, null, null, "continuous");

        public Func<LiveProjectionQuery, PageResult<LiveProjectedRecordRow>>?
            AutoPage { get; set; }

        public bool DelayQueries { get; set; }

        public int ProjectCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public List<LiveProjectionQuery> Queries { get; } = [];

        public HashSet<int> CancelledQueries { get; } = [];

        public Task<LiveProjectionAvailability> GetAvailabilityAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Availability);
        }

        public Task<LiveProjectionRunResult> ProjectSessionAsync(
            string liveSessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectCalls++;
            return Task.FromResult(ProjectionResult with
            {
                LiveSessionId = liveSessionId
            });
        }

        public Task<LiveContinuityStatus> VerifyContinuityAsync(
            string liveSessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return Task.FromResult(Continuity with
            {
                LiveSessionId = liveSessionId
            });
        }

        public Task<PageResult<LiveProjectedRecordRow>> GetProjectedRecordsAsync(
            LiveProjectionQuery query,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Queries.Add(query);
                var call = Queries.Count;
                if (!DelayQueries)
                {
                    return Task.FromResult(
                        AutoPage?.Invoke(query)
                        ?? new PageResult<LiveProjectedRecordRow>(
                            [],
                            0,
                            query.Page.Offset,
                            query.Page.Limit));
                }

                var completion =
                    new TaskCompletionSource<PageResult<LiveProjectedRecordRow>>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = cancellationToken.Register(
                    () =>
                    {
                        lock (_sync)
                        {
                            CancelledQueries.Add(call);
                        }
                    });
                _pending.Add(new PendingQuery(call, completion, registration));
                _queryChanged.TrySetResult();
                Monitor.PulseAll(_sync);
                return completion.Task;
            }
        }

        public Task WaitForQueriesAsync(int count) =>
            Task.Run(
                () =>
                {
                    lock (_sync)
                    {
                        while (Queries.Count < count)
                        {
                            if (!Monitor.Wait(_sync, TimeSpan.FromSeconds(10)))
                            {
                                throw new TimeoutException(
                                    $"Only {Queries.Count} projection queries started.");
                            }
                        }
                    }
                });

        public void CompleteQuery(
            int call,
            PageResult<LiveProjectedRecordRow> result)
        {
            PendingQuery pending;
            lock (_sync)
            {
                pending = _pending.Single(item => item.Call == call);
            }

            pending.Completion.TrySetResult(result);
            pending.Registration.Dispose();
        }

        private sealed record PendingQuery(
            int Call,
            TaskCompletionSource<PageResult<LiveProjectedRecordRow>> Completion,
            CancellationTokenRegistration Registration);
    }
}
