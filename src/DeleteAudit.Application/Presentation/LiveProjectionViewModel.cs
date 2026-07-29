using System.Collections.ObjectModel;
using System.Globalization;
using DeleteAudit.Application.Projection;
using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

/// <summary>
/// Explicit, paged UI over the live-owned canonical projection.
/// </summary>
/// <remarks>
/// Selecting a capture session only changes local view-model state. Projection, querying
/// and continuity verification start solely through explicit commands. Every operation
/// is cancellable and follows latest-request-wins, so a stale result cannot overwrite a
/// newer session, filter, page or disposal decision.
/// </remarks>
public sealed class LiveProjectionViewModel : ViewModelBase, IDisposable
{
    public const string ProjectionDisclosure =
        "本页将“实时接入历史”中已保存的证据规范化到独立的 live-owned 投影。"
        + "它不写入或伪装成离线 raw_events、delete_events、delete_sessions、"
        + "import_session、离线序号或离线哈希链。投影保留 live_evidence_id 来源，"
        + "且只在你点击按钮后运行；打开或选择本页不会开始监控、订阅日志或后台轮询。"
        + "连续性哈希仅辅助发现顺序断裂和意外修改，SQLite 及该哈希链均不具备防篡改能力。";

    private const int PageSize = 50;
    private const string AllSources = "全部";

    private readonly ILiveProjectionService _service;
    private readonly ObservableCollection<LiveProjectedRecordRow> _records = [];
    private readonly RequestSlot _requests = new();
    private string? _selectedSessionId;
    private LiveProjectionAvailability? _availability;
    private LiveProjectionRunResult? _lastRun;
    private LiveContinuityStatus? _continuity;
    private bool _recordsLoaded;
    private long _totalCount;
    private int _offset;
    private int _inFlight;
    private bool _disposed;
    private string _source = AllSources;
    private string? _pathContains;
    private string? _processContains;
    private bool _descending;

    public LiveProjectionViewModel(ILiveProjectionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Records = new ReadOnlyObservableCollection<LiveProjectedRecordRow>(_records);

        ProjectCommand = new AsyncCommand(
            ProjectAsync,
            () => HasSelectedSession,
            ShowUnexpectedError);
        RefreshCommand = new AsyncCommand(
            () => LoadAsync(resetPage: false),
            () => HasSelectedSession,
            ShowUnexpectedError);
        VerifyCommand = new AsyncCommand(
            VerifyAsync,
            () => HasSelectedSession,
            ShowUnexpectedError);
        ApplyFiltersCommand = new AsyncCommand(
            () => LoadAsync(resetPage: true),
            () => HasSelectedSession,
            ShowUnexpectedError);
        PreviousPageCommand = new AsyncCommand(
            () => MovePageAsync(-PageSize),
            () => HasPreviousPage,
            ShowUnexpectedError);
        NextPageCommand = new AsyncCommand(
            () => MovePageAsync(PageSize),
            () => HasNextPage,
            ShowUnexpectedError);
    }

    public string Disclosure { get; } = ProjectionDisclosure;

    public ReadOnlyObservableCollection<LiveProjectedRecordRow> Records { get; }

    public AsyncCommand ProjectCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand VerifyCommand { get; }

    public AsyncCommand ApplyFiltersCommand { get; }

    public AsyncCommand PreviousPageCommand { get; }

    public AsyncCommand NextPageCommand { get; }

    public IReadOnlyList<string> SourceOptions { get; } =
        [AllSources, .. LiveProjectionSources.All];

    public string? SelectedSessionId => _selectedSessionId;

    public bool HasSelectedSession =>
        !string.IsNullOrWhiteSpace(_selectedSessionId);

    public string SelectedSessionSummary =>
        HasSelectedSession
            ? $"当前投影会话：{_selectedSessionId}"
            : "尚未选择实时接入会话。请先在“实时接入历史”页选择一个会话。";

    public bool IsLoading => Volatile.Read(ref _inFlight) > 0;

    public bool IsUnavailable => _availability is not null && !_availability.IsReady;

    public string AvailabilityMessage =>
        _availability?.Message ?? "尚未检查 0005 live projection readiness。";

    public bool HasRecords => _records.Count > 0;

    public bool IsEmpty =>
        _recordsLoaded
        && _availability?.IsReady == true
        && HasSelectedSession
        && _records.Count == 0;

    public bool HasLastRun => _lastRun is not null;

    public bool LastRunFailed => _lastRun is not null && !_lastRun.Succeeded;

    public string LastRunSummary
    {
        get
        {
            if (_lastRun is null)
            {
                return "尚未在本页执行投影。";
            }

            if (!_lastRun.Succeeded)
            {
                return $"投影失败（{_lastRun.FailureCode}）："
                    + $"{_lastRun.FailureDetail}";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "投影完成：检查 {0:N0} 条；新增 {1:N0} 条；幂等跳过 {2:N0} 条。{3}",
                _lastRun.ConsideredCount,
                _lastRun.ProjectedCount,
                _lastRun.SkippedCount,
                _lastRun.WasAlreadyComplete
                    ? "该会话先前已完整投影，本次没有生成重复记录。"
                    : "投影身份、序号与 epoch 均由源证据确定。");
        }
    }

    public bool HasContinuityResult => _continuity is not null;

    public bool ContinuityIsBroken =>
        _continuity is not null && !_continuity.IsContinuous;

    public string ContinuitySummary
    {
        get
        {
            if (_continuity is null)
            {
                return "尚未验证 live-owned continuity chain。";
            }

            if (_continuity.IsContinuous)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "连续性验证通过：{0:N0} 条投影记录。{1}",
                    _continuity.ProjectedCount,
                    _continuity.Detail);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "连续性验证未通过：首个断点序号 {0}；来源证据 {1}。{2}",
                _continuity.FirstBrokenSequence?.ToString(
                    CultureInfo.InvariantCulture) ?? "未知",
                string.IsNullOrWhiteSpace(_continuity.FirstBrokenLiveEvidenceId)
                    ? "未知"
                    : _continuity.FirstBrokenLiveEvidenceId,
                _continuity.Detail);
        }
    }

    public string PageStatus => DescribePage(
        _offset,
        _records.Count,
        _totalCount);

    public bool HasPreviousPage => _offset > 0;

    public bool HasNextPage => _offset + _records.Count < _totalCount;

    public string Source
    {
        get => _source;
        set => SetProperty(
            ref _source,
            string.IsNullOrWhiteSpace(value) ? AllSources : value);
    }

    public string? PathContains
    {
        get => _pathContains;
        set => SetProperty(ref _pathContains, value);
    }

    public string? ProcessContains
    {
        get => _processContains;
        set => SetProperty(ref _processContains, value);
    }

    public bool Descending
    {
        get => _descending;
        set => SetProperty(ref _descending, value);
    }

    /// <summary>
    /// Retires all work belonging to the previous live capture. No database operation is
    /// started here; the user chooses project, refresh or verify explicitly.
    /// </summary>
    public void SetSession(string? liveSessionId)
    {
        if (_disposed)
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(liveSessionId)
            ? null
            : liveSessionId;
        if (string.Equals(
                _selectedSessionId,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        _requests.Invalidate();
        _selectedSessionId = normalized;
        _availability = null;
        _lastRun = null;
        _continuity = null;
        _recordsLoaded = false;
        _records.Clear();
        _totalCount = 0;
        _offset = 0;
        ErrorMessage = null;
        NotifyAllStateChanged();
    }

    public Task LoadAsync(bool resetPage = true) =>
        RunLatestAsync(async ticket =>
        {
            var sessionId = _selectedSessionId;
            if (sessionId is null)
            {
                return;
            }

            var requestedOffset = resetPage ? 0 : _offset;
            var availability = await _service
                .GetAvailabilityAsync(ticket.Token)
                .ConfigureAwait(true);
            PageResult<LiveProjectedRecordRow>? page = null;
            if (availability.IsReady)
            {
                page = await _service
                    .GetProjectedRecordsAsync(
                        CreateQuery(sessionId, requestedOffset),
                        ticket.Token)
                    .ConfigureAwait(true);
                RejectOversizedPage(page.Items.Count);
            }

            if (!CanCommit(ticket, sessionId))
            {
                return;
            }

            _availability = availability;
            _recordsLoaded = page is not null;
            if (page is null)
            {
                _records.Clear();
                _totalCount = 0;
                _offset = 0;
            }
            else
            {
                ReplaceRecords(page);
            }

            NotifyQueryStateChanged();
        });

    public Task ProjectAsync() =>
        RunLatestAsync(async ticket =>
        {
            var sessionId = _selectedSessionId;
            if (sessionId is null)
            {
                return;
            }

            var result = await _service
                .ProjectSessionAsync(sessionId, ticket.Token)
                .ConfigureAwait(true);
            if (!CanCommit(ticket, sessionId))
            {
                return;
            }

            // The projection transaction is already durable at this point. Publish that
            // fact before the follow-up read, so a later query failure cannot make a
            // successful write look as if it never happened.
            _lastRun = result;
            NotifyRunStateChanged();
            if (!result.Succeeded)
            {
                ErrorMessage = result.FailureDetail ?? result.FailureCode;
                return;
            }

            var availability = await _service
                .GetAvailabilityAsync(ticket.Token)
                .ConfigureAwait(true);
            var page = await _service
                .GetProjectedRecordsAsync(
                    CreateQuery(sessionId, offset: 0),
                    ticket.Token)
                .ConfigureAwait(true);
            RejectOversizedPage(page.Items.Count);
            var continuity = await _service
                .VerifyContinuityAsync(sessionId, ticket.Token)
                .ConfigureAwait(true);

            if (!CanCommit(ticket, sessionId))
            {
                return;
            }

            _availability = availability;
            _continuity = continuity;
            _recordsLoaded = true;
            ReplaceRecords(page);
            NotifyAllStateChanged();
        });

    public Task VerifyAsync() =>
        RunLatestAsync(async ticket =>
        {
            var sessionId = _selectedSessionId;
            if (sessionId is null)
            {
                return;
            }

            var availability = await _service
                .GetAvailabilityAsync(ticket.Token)
                .ConfigureAwait(true);
            LiveContinuityStatus? continuity = null;
            if (availability.IsReady)
            {
                continuity = await _service
                    .VerifyContinuityAsync(sessionId, ticket.Token)
                    .ConfigureAwait(true);
            }

            if (!CanCommit(ticket, sessionId))
            {
                return;
            }

            _availability = availability;
            _continuity = continuity;
            NotifyQueryStateChanged();
            NotifyContinuityStateChanged();
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requests.Dispose();
    }

    private LiveProjectionQuery CreateQuery(string sessionId, int offset) =>
        new(
            sessionId,
            string.Equals(Source, AllSources, StringComparison.Ordinal)
                ? null
                : Source,
            PathContains,
            ProcessContains,
            Descending,
            new PageRequest(offset, PageSize));

    private async Task RunLatestAsync(Func<RequestTicket, Task> work)
    {
        if (_disposed)
        {
            return;
        }

        RequestTicket ticket;
        try
        {
            ticket = _requests.Begin();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        BeginLoading();
        try
        {
            if (ticket.IsCurrent)
            {
                ErrorMessage = null;
            }

            await work(ticket).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (ticket.IsCurrent)
            {
                ErrorMessage = "操作已取消。";
            }
        }
        catch (Exception exception)
        {
            if (ticket.IsCurrent)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            ticket.Complete();
            EndLoading();
        }
    }

    private bool CanCommit(RequestTicket ticket, string sessionId) =>
        ticket.IsCurrent
        && string.Equals(
            _selectedSessionId,
            sessionId,
            StringComparison.Ordinal);

    private void ReplaceRecords(PageResult<LiveProjectedRecordRow> page)
    {
        _records.Clear();
        foreach (var record in page.Items)
        {
            _records.Add(record);
        }

        _totalCount = page.TotalCount;
        _offset = page.Offset;
    }

    private Task MovePageAsync(int delta)
    {
        _offset = delta < 0
            ? Math.Max(0, _offset + delta)
            : checked(_offset + delta);
        return LoadAsync(resetPage: false);
    }

    private void BeginLoading()
    {
        if (Interlocked.Increment(ref _inFlight) == 1)
        {
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    private void EndLoading()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && !_disposed)
        {
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    private void NotifyAllStateChanged()
    {
        OnPropertyChanged(nameof(SelectedSessionId));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(SelectedSessionSummary));
        NotifyQueryStateChanged();
        NotifyRunStateChanged();
        NotifyContinuityStateChanged();
        NotifyCommands();
    }

    private void NotifyQueryStateChanged()
    {
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(AvailabilityMessage));
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PageStatus));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        NotifyCommands();
    }

    private void NotifyRunStateChanged()
    {
        OnPropertyChanged(nameof(HasLastRun));
        OnPropertyChanged(nameof(LastRunFailed));
        OnPropertyChanged(nameof(LastRunSummary));
    }

    private void NotifyContinuityStateChanged()
    {
        OnPropertyChanged(nameof(HasContinuityResult));
        OnPropertyChanged(nameof(ContinuityIsBroken));
        OnPropertyChanged(nameof(ContinuitySummary));
    }

    private void NotifyCommands()
    {
        ProjectCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        ApplyFiltersCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private static void RejectOversizedPage(int count)
    {
        if (count > PageSize)
        {
            throw new InvalidOperationException(
                $"查询返回了 {count} 行，超过每页上限 {PageSize}。");
        }
    }

    private static string DescribePage(int offset, int count, long totalCount)
    {
        if (totalCount == 0)
        {
            return "0 项";
        }

        var currentPage = (offset / PageSize) + 1;
        var pageCount = ((totalCount - 1) / PageSize) + 1;
        return string.Format(
            CultureInfo.InvariantCulture,
            "第 {0} / {1} 页，共 {2:N0} 项（本页 {3:N0} 项）",
            currentPage,
            pageCount,
            totalCount,
            count);
    }

    private sealed class RequestSlot : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _current;
        private long _generation;
        private bool _disposed;

        public RequestTicket Begin()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var previous = _current;
                var created = new CancellationTokenSource();
                _current = created;
                var generation = ++_generation;
                Cancel(previous);
                return new RequestTicket(this, created, generation);
            }
        }

        public void Invalidate()
        {
            CancellationTokenSource? current;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _generation++;
                current = _current;
                _current = null;
            }

            Cancel(current);
        }

        public bool IsCurrent(long generation)
        {
            lock (_sync)
            {
                return !_disposed && generation == _generation;
            }
        }

        public void Retire(CancellationTokenSource source)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, source))
                {
                    _current = null;
                }
            }

            source.Dispose();
        }

        public void Dispose()
        {
            CancellationTokenSource? current;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _generation++;
                current = _current;
                _current = null;
            }

            Cancel(current);
        }

        private static void Cancel(CancellationTokenSource? source)
        {
            if (source is null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private readonly struct RequestTicket(
        RequestSlot slot,
        CancellationTokenSource source,
        long generation)
    {
        public CancellationToken Token => source.Token;

        public bool IsCurrent => slot.IsCurrent(generation);

        public void Complete() => slot.Retire(source);
    }
}
