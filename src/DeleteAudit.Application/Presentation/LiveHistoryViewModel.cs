using System.Collections.ObjectModel;
using System.Globalization;
using DeleteAudit.Application.Analysis;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

/// <summary>
/// Read-only browser over what live capture sessions actually recorded.
/// </summary>
/// <remarks>
/// <para>
/// Opening this page reads the database and nothing else. It never subscribes to a
/// Windows event log, never starts monitoring, and never polls: refreshing is an
/// explicit user action.
/// </para>
/// <para>
/// Unlike the offline paged pages, every load here is cancellable and carries a
/// generation stamp. A slow query whose filters have already been replaced is discarded
/// instead of overwriting newer results.
/// </para>
/// </remarks>
public sealed class LiveHistoryViewModel : ViewModelBase, IDisposable
{
    /// <summary>Always visible on the page: what this view is, and what it is not.</summary>
    public const string HistoryDisclosure =
        "实时接入历史只读展示本机查看器数据库中已保存的实时接入记录。打开本页不会开始实时监控，"
        + "也不会订阅任何事件日志——实时监控只能在“实时接入预览”页由你手动开始。"
        + "本页不上传任何数据，也不提供删除、清空或修复数据库的操作。"
        + "没有完成记录的会话表示该次接入可能异常中断，其计数不完整。"
        + "SQLite 不是防篡改介质，本功能仍不是完整或生产级取证系统。";

    private const int PageSize = 50;

    private readonly ILiveHistoryQueryService _queryService;
    private readonly ILiveAnalysisService _analysisService;
    private readonly Action<string?>? _selectedSessionChanged;
    private readonly ObservableCollection<LiveCaptureSessionRow> _sessions = [];
    private readonly ObservableCollection<LiveCaptureRecordRow> _records = [];
    private readonly ObservableCollection<LiveCaptureDiagnosticRow> _diagnostics = [];
    private readonly ObservableCollection<LiveDeleteSessionRow> _analysisSessions = [];
    private readonly ObservableCollection<LiveCorrelatedDeleteRow> _analysisDeletes = [];

    private readonly RequestSlot _sessionRequests = new();
    private readonly RequestSlot _recordRequests = new();
    private readonly RequestSlot _rawXmlRequests = new();
    private readonly RequestSlot _analysisRequests = new();
    private LiveSessionAnalysis? _analysis;
    private int _inFlight;
    private bool _disposed;

    private LiveHistoryAvailability? _availability;
    private LiveCaptureSessionRow? _selectedSession;
    private LiveCaptureRecordRow? _selectedRecord;
    private RawXmlDocument? _rawXml;
    private long _sessionTotalCount;
    private int _sessionOffset;
    private long _recordTotalCount;
    private int _recordOffset;

    private string? _fromUtcText;
    private string? _toUtcText;
    private LiveHistorySessionState? _sessionState;
    private string? _outcome;
    private string? _providerContains;
    private string? _channelContains;
    private string? _errorCodeContains;
    private bool _errorsOnly;
    private bool _newestFirst;

    public LiveHistoryViewModel(
        ILiveHistoryQueryService queryService,
        ILiveAnalysisService analysisService,
        Action<string?>? selectedSessionChanged = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _analysisService = analysisService
            ?? throw new ArgumentNullException(nameof(analysisService));
        _selectedSessionChanged = selectedSessionChanged;

        // None of these gate on a load being in flight: a newer request replaces an
        // older one rather than being refused, so the user is never locked out of
        // changing a filter or page while a slow query is still running.
        RefreshCommand = new AsyncCommand(
            () => LoadSessionsAsync(resetPage: false),
            null,
            ShowUnexpectedError);
        ApplyFiltersCommand = new AsyncCommand(
            () => LoadSessionsAsync(resetPage: true),
            null,
            ShowUnexpectedError);
        PreviousSessionPageCommand = new AsyncCommand(
            () => MoveSessionPageAsync(-PageSize),
            () => HasPreviousSessionPage,
            ShowUnexpectedError);
        NextSessionPageCommand = new AsyncCommand(
            () => MoveSessionPageAsync(PageSize),
            () => HasNextSessionPage,
            ShowUnexpectedError);
        PreviousRecordPageCommand = new AsyncCommand(
            () => MoveRecordPageAsync(-PageSize),
            () => HasPreviousRecordPage,
            ShowUnexpectedError);
        NextRecordPageCommand = new AsyncCommand(
            () => MoveRecordPageAsync(PageSize),
            () => HasNextRecordPage,
            ShowUnexpectedError);
        ApplyRecordFiltersCommand = new AsyncCommand(
            () => LoadRecordsAsync(resetPage: true),
            () => SelectedSession is not null,
            ShowUnexpectedError);

        // Analysis is never automatic: deriving correlation re-reads and re-parses the
        // session's evidence, so the user asks for it explicitly.
        AnalyzeCommand = new AsyncCommand(
            AnalyzeAsync,
            () => SelectedSession is not null,
            ShowUnexpectedError);

        Sessions = new ReadOnlyObservableCollection<LiveCaptureSessionRow>(_sessions);
        Records = new ReadOnlyObservableCollection<LiveCaptureRecordRow>(_records);
        Diagnostics = new ReadOnlyObservableCollection<LiveCaptureDiagnosticRow>(_diagnostics);
        AnalysisSessions =
            new ReadOnlyObservableCollection<LiveDeleteSessionRow>(_analysisSessions);
        AnalysisDeletes =
            new ReadOnlyObservableCollection<LiveCorrelatedDeleteRow>(_analysisDeletes);
    }

    public string Disclosure { get; } = HistoryDisclosure;

    public ReadOnlyObservableCollection<LiveCaptureSessionRow> Sessions { get; }

    public ReadOnlyObservableCollection<LiveCaptureRecordRow> Records { get; }

    public ReadOnlyObservableCollection<LiveCaptureDiagnosticRow> Diagnostics { get; }

    /// <summary>Delete sessions derived from the selected capture, never stored.</summary>
    public ReadOnlyObservableCollection<LiveDeleteSessionRow> AnalysisSessions { get; }

    /// <summary>Correlated deletes derived from the selected capture, never stored.</summary>
    public ReadOnlyObservableCollection<LiveCorrelatedDeleteRow> AnalysisDeletes { get; }

    public AsyncCommand AnalyzeCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand ApplyFiltersCommand { get; }

    public AsyncCommand ApplyRecordFiltersCommand { get; }

    public AsyncCommand PreviousSessionPageCommand { get; }

    public AsyncCommand NextSessionPageCommand { get; }

    public AsyncCommand PreviousRecordPageCommand { get; }

    public AsyncCommand NextRecordPageCommand { get; }

    /// <summary>
    /// At least one query is running. This page tracks its own loading state: unlike
    /// <see cref="ViewModelBase.IsBusy"/> it never refuses a newer request.
    /// </summary>
    public bool IsLoading => Volatile.Read(ref _inFlight) > 0;

    /// <summary>True once a load has run and the live capture tables were unusable.</summary>
    public bool IsUnavailable => _availability is not null && !_availability.IsReady;

    public string UnavailableMessage => _availability?.Message ?? string.Empty;

    public bool HasSessions => _sessions.Count > 0;

    public bool IsSessionListEmpty =>
        _availability is not null && _availability.IsReady && _sessions.Count == 0;

    public bool HasRecords => _records.Count > 0;

    public bool IsRecordListEmpty => SelectedSession is not null && _records.Count == 0;

    public bool HasDiagnostics => _diagnostics.Count > 0;

    public bool HasAnalysis => _analysis is not null;

    public bool AnalysisIsEmpty => _analysis is not null && !_analysis.HasDeletes;

    public bool AnalysisWasTruncated => _analysis?.WasTruncated ?? false;

    public string AnalysisTruncationNotice =>
        AnalysisWasTruncated
            ? string.Format(
                CultureInfo.InvariantCulture,
                "该会话的记录超过分析上限 {0:N0} 条，下面只分析了最早的一部分；其余记录仍完整保存在数据库中。",
                LiveAnalysisLimits.MaxAnalyzedRecords)
            : string.Empty;

    /// <summary>
    /// States plainly what the derived numbers are and are not. Correlation is
    /// corroboration, not proof of intent.
    /// </summary>
    public string AnalysisSummary =>
        _analysis is null
            ? "尚未分析。分析只读取该会话已保存的记录，重新解析并套用与离线导入相同的确定性规则，不会写入任何数据。"
            : string.Format(
                CultureInfo.InvariantCulture,
                "已分析记录 {0:N0}；删除事实 {1:N0}；进程上下文 {2:N0}；安全补强 {3:N0}；"
                + "无法解析 {4:N0}；未能关联的删除 {5:N0}；归纳出删除会话 {6:N0} 个。"
                + "关联结果是证据之间的印证，不代表已确认的攻击或责任判定。",
                _analysis.AnalyzedRecordCount,
                _analysis.DeleteFactCount,
                _analysis.ProcessContextCount,
                _analysis.SecurityEvidenceCount,
                _analysis.UnparsableRecordCount,
                _analysis.UncorrelatedDeleteCount,
                _analysis.DeleteSessions.Count);

    public string SessionPageStatus => DescribePage(_sessionOffset, _sessions.Count, _sessionTotalCount);

    public string RecordPageStatus => DescribePage(_recordOffset, _records.Count, _recordTotalCount);

    public bool HasPreviousSessionPage => _sessionOffset > 0;

    public bool HasNextSessionPage => _sessionOffset + _sessions.Count < _sessionTotalCount;

    public bool HasPreviousRecordPage => _recordOffset > 0;

    public bool HasNextRecordPage => _recordOffset + _records.Count < _recordTotalCount;

    public LiveCaptureSessionRow? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                _selectedSessionChanged?.Invoke(value?.LiveSessionId);
                OnPropertyChanged(nameof(SelectedSessionSummary));
                OnPropertyChanged(nameof(SelectedSessionIsIncomplete));
                // An analysis belongs to the session it was derived from. Switching
                // sessions retires it immediately rather than leaving another session's
                // conclusions on screen while the new records load.
                SetAnalysis(null);
                NotifyCommands();
                // Selecting a session loads its records; the previous load is cancelled.
                _ = LoadRecordsAsync(resetPage: true);
            }
        }
    }

    public LiveCaptureRecordRow? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                OnPropertyChanged(nameof(SelectedRecordSummary));
                NotifyCommands();
                // Raw XML is fetched only once a record is actually selected.
                _ = LoadRawXmlAsync();
            }
        }
    }

    public RawXmlDocument? RawXml => _rawXml;

    public bool HasRawXml => _rawXml is not null;

    public string RawXmlPreview =>
        _rawXml is null
            ? string.Empty
            : _rawXml.IsAvailable
                ? _rawXml.PreviewText ?? string.Empty
                : ViewerDisplay.Value(_rawXml.UnavailableReason);

    public bool RawXmlIsTruncated => _rawXml?.IsTruncated ?? false;

    public string RawXmlTruncationNotice =>
        RawXmlIsTruncated
            ? string.Format(
                CultureInfo.InvariantCulture,
                "原始 XML 超过 {0:N0} 个字符，下面只显示前 {0:N0} 个字符。完整内容仍保存在数据库中，未被修改。",
                RawXmlDocument.MaxPreviewCharacters)
            : string.Empty;

    public string RawXmlLengthSummary =>
        _rawXml is null || !_rawXml.IsAvailable
            ? string.Empty
            : string.Format(
                CultureInfo.InvariantCulture,
                "原始字符数：{0:N0}；当前预览字符数：{1:N0}",
                _rawXml.OriginalLength,
                _rawXml.PreviewLength);

    public string SelectedSessionSummary =>
        _selectedSession is null
            ? "尚未选择会话。"
            : string.Format(
                CultureInfo.InvariantCulture,
                "接收 {0:N0}；删除事实 {1:N0}；进程上下文 {2:N0}；安全补强 {3:N0}；忽略 {4:N0}；错误 {5:N0}；丢弃 {6:N0}；已保存明细 {7:N0}",
                _selectedSession.ReceivedCount,
                _selectedSession.DeleteFactCount,
                _selectedSession.ProcessContextCount,
                _selectedSession.SecurityEvidenceCount,
                _selectedSession.IgnoredCount,
                _selectedSession.ErrorCount,
                _selectedSession.DroppedCount,
                _selectedSession.StoredRecordCount);

    public bool SelectedSessionIsIncomplete =>
        _selectedSession is not null && !_selectedSession.IsComplete;

    public string SelectedRecordSummary =>
        _selectedRecord is null
            ? "尚未选择记录。"
            : string.Format(
                CultureInfo.InvariantCulture,
                "接收序号 {0:N0}；分类 {1}；SHA-256 {2}",
                _selectedRecord.ReceivedSequence,
                LiveCaptureOutcomes.Label(_selectedRecord.Outcome),
                _selectedRecord.RawXmlSha256);

    public IReadOnlyList<string> OutcomeOptions { get; } =
        ["全部", .. LiveCaptureOutcomes.All.Select(LiveCaptureOutcomes.Label)];

    public string? FromUtcText
    {
        get => _fromUtcText;
        set => SetProperty(ref _fromUtcText, value);
    }

    public string? ToUtcText
    {
        get => _toUtcText;
        set => SetProperty(ref _toUtcText, value);
    }

    public LiveHistorySessionState? SessionState
    {
        get => _sessionState;
        set => SetProperty(ref _sessionState, value);
    }

    public string? Outcome
    {
        get => _outcome;
        set => SetProperty(ref _outcome, value);
    }

    public string? ProviderContains
    {
        get => _providerContains;
        set => SetProperty(ref _providerContains, value);
    }

    public string? ChannelContains
    {
        get => _channelContains;
        set => SetProperty(ref _channelContains, value);
    }

    public string? ErrorCodeContains
    {
        get => _errorCodeContains;
        set => SetProperty(ref _errorCodeContains, value);
    }

    public bool ErrorsOnly
    {
        get => _errorsOnly;
        set => SetProperty(ref _errorsOnly, value);
    }

    public bool NewestFirst
    {
        get => _newestFirst;
        set => SetProperty(ref _newestFirst, value);
    }

    /// <summary>
    /// Loads the session list. Safe to call on first navigation; it performs the
    /// availability check itself and reports an unusable database as a state rather than
    /// an exception.
    /// </summary>
    public Task LoadSessionsAsync(bool resetPage = true) =>
        RunLatestAsync(_sessionRequests, async ticket =>
        {
            if (resetPage)
            {
                _sessionOffset = 0;
            }

            var availability = await _queryService
                .GetAvailabilityAsync(ticket.Token)
                .ConfigureAwait(true);
            var query = new LiveHistorySessionQuery(
                FilterPresentation.ParseOptionalUtc(FromUtcText, "开始时间"),
                FilterPresentation.ParseOptionalUtc(ToUtcText, "结束时间"),
                SessionState,
                new PageRequest(_sessionOffset, PageSize));

            PageResult<LiveCaptureSessionRow>? page = null;
            if (availability.IsReady)
            {
                page = await _queryService
                    .GetSessionsAsync(query, ticket.Token)
                    .ConfigureAwait(true);
                RejectOversizedPage(page.Items.Count);
            }

            // Nothing above touched the UI. The single commit point below runs only for
            // the newest request, so a slower predecessor can never publish over it.
            if (!ticket.IsCurrent)
            {
                return;
            }

            _availability = availability;
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(UnavailableMessage));
            if (page is null)
            {
                ClearSessions();
                ClearRecords();
                return;
            }

            _sessions.Clear();
            foreach (var row in page.Items)
            {
                _sessions.Add(row);
            }

            _sessionTotalCount = page.TotalCount;
            _sessionOffset = page.Offset;
            NotifySessionListChanged();
        });

    public Task LoadRecordsAsync(bool resetPage = true) =>
        RunLatestAsync(_recordRequests, async ticket =>
        {
            var session = SelectedSession;
            if (session is null)
            {
                if (ticket.IsCurrent)
                {
                    ClearRecords();
                }

                return;
            }

            if (resetPage)
            {
                _recordOffset = 0;
            }

            var page = await _queryService
                .GetRecordsAsync(
                    new LiveHistoryRecordQuery(
                        session.LiveSessionId,
                        FilterPresentation.ParseOptionalUtc(FromUtcText, "开始时间"),
                        FilterPresentation.ParseOptionalUtc(ToUtcText, "结束时间"),
                        Outcome,
                        ProviderContains,
                        ChannelContains,
                        null,
                        null,
                        ErrorCodeContains,
                        ErrorsOnly,
                        null,
                        null,
                        NewestFirst,
                        new PageRequest(_recordOffset, PageSize)),
                    ticket.Token)
                .ConfigureAwait(true);
            RejectOversizedPage(page.Items.Count);
            var diagnostics = await _queryService
                .GetSessionDiagnosticsAsync(session.LiveSessionId, ticket.Token)
                .ConfigureAwait(true);

            if (!ticket.IsCurrent)
            {
                return;
            }

            _records.Clear();
            foreach (var row in page.Items)
            {
                _records.Add(row);
            }

            _diagnostics.Clear();
            foreach (var row in diagnostics)
            {
                _diagnostics.Add(row);
            }

            _recordTotalCount = page.TotalCount;
            _recordOffset = page.Offset;
            NotifyRecordListChanged();
        });

    public Task LoadRawXmlAsync() =>
        RunLatestAsync(_rawXmlRequests, async ticket =>
        {
            var record = SelectedRecord;
            if (record is null)
            {
                if (ticket.IsCurrent)
                {
                    SetRawXml(null);
                }

                return;
            }

            var document = await _queryService
                .GetRecordRawXmlAsync(record.LiveEvidenceId, ticket.Token)
                .ConfigureAwait(true);

            // A preview for a record the user has already moved off must not land.
            if (!ticket.IsCurrent)
            {
                return;
            }

            SetRawXml(document);
            if (document is null)
            {
                ErrorMessage = "找不到该记录的原始 XML。";
            }
        });

    /// <summary>
    /// Derives correlation, delete sessions and risk for the selected capture session.
    /// Reads and re-parses stored evidence; writes nothing.
    /// </summary>
    public Task AnalyzeAsync() =>
        RunLatestAsync(_analysisRequests, async ticket =>
        {
            var session = SelectedSession;
            if (session is null)
            {
                if (ticket.IsCurrent)
                {
                    SetAnalysis(null);
                }

                return;
            }

            var analysis = await _analysisService
                .AnalyzeAsync(session.LiveSessionId, ticket.Token)
                .ConfigureAwait(true);

            if (!ticket.IsCurrent)
            {
                return;
            }

            SetAnalysis(analysis);
        });

    private void SetAnalysis(LiveSessionAnalysis? analysis)
    {
        _analysis = analysis;
        _analysisSessions.Clear();
        _analysisDeletes.Clear();
        if (analysis is not null)
        {
            foreach (var row in analysis.DeleteSessions)
            {
                _analysisSessions.Add(row);
            }

            foreach (var row in analysis.Deletes)
            {
                _analysisDeletes.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasAnalysis));
        OnPropertyChanged(nameof(AnalysisIsEmpty));
        OnPropertyChanged(nameof(AnalysisWasTruncated));
        OnPropertyChanged(nameof(AnalysisTruncationNotice));
        OnPropertyChanged(nameof(AnalysisSummary));
    }

    /// <summary>
    /// Runs one request under a latest-request-wins policy: starting a new one cancels
    /// the request it supersedes, and only the newest may touch the view model.
    /// </summary>
    /// <remarks>
    /// <see cref="ViewModelBase.RunSafelyAsync"/> is deliberately not used here. It
    /// drops a second concurrent call instead of replacing it, which is right for the
    /// offline pages but would silently ignore a user changing a filter while a slow
    /// query is still running. A superseded request's cancellation and its failures are
    /// both swallowed: neither may disturb the state a newer request already published.
    /// </remarks>
    private async Task RunLatestAsync(RequestSlot slot, Func<RequestTicket, Task> work)
    {
        if (_disposed)
        {
            return;
        }

        RequestTicket ticket;
        try
        {
            ticket = slot.Begin();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and here; nothing may run.
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
            // A stale failure must never replace a newer request's success.
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

    private void BeginLoading()
    {
        if (Interlocked.Increment(ref _inFlight) == 1)
        {
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    private void EndLoading()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0)
        {
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    /// <summary>
    /// Cancels anything still running and permanently retires every request slot, so a
    /// query that completes after the window closed can no longer touch this view model.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionRequests.Dispose();
        _recordRequests.Dispose();
        _rawXmlRequests.Dispose();
        _analysisRequests.Dispose();
    }

    private Task MoveSessionPageAsync(int delta)
    {
        _sessionOffset = Math.Max(0, _sessionOffset + delta);
        return LoadSessionsAsync(resetPage: false);
    }

    private Task MoveRecordPageAsync(int delta)
    {
        _recordOffset = Math.Max(0, _recordOffset + delta);
        return LoadRecordsAsync(resetPage: false);
    }

    /// <summary>
    /// The service is the authority on page size. A larger page means the query layer
    /// stopped honouring the limit, which must surface rather than be rendered.
    /// </summary>
    private static void RejectOversizedPage(int count)
    {
        if (count > PageSize)
        {
            throw new InvalidOperationException(
                $"查询返回了 {count} 行，超过每页上限 {PageSize}。");
        }
    }

    private void SetRawXml(RawXmlDocument? document)
    {
        _rawXml = document;
        OnPropertyChanged(nameof(RawXml));
        OnPropertyChanged(nameof(HasRawXml));
        OnPropertyChanged(nameof(RawXmlPreview));
        OnPropertyChanged(nameof(RawXmlIsTruncated));
        OnPropertyChanged(nameof(RawXmlTruncationNotice));
        OnPropertyChanged(nameof(RawXmlLengthSummary));
    }

    private void ClearSessions()
    {
        _sessions.Clear();
        _sessionTotalCount = 0;
        _sessionOffset = 0;
        NotifySessionListChanged();
    }

    private void ClearRecords()
    {
        _records.Clear();
        _diagnostics.Clear();
        _recordTotalCount = 0;
        _recordOffset = 0;
        SetRawXml(null);
        // Analysis belongs to one capture session; changing session retires it rather
        // than leaving another session's conclusions on screen.
        SetAnalysis(null);
        NotifyRecordListChanged();
    }

    private void NotifySessionListChanged()
    {
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(IsSessionListEmpty));
        OnPropertyChanged(nameof(SessionPageStatus));
        OnPropertyChanged(nameof(HasPreviousSessionPage));
        OnPropertyChanged(nameof(HasNextSessionPage));
        NotifyCommands();
    }

    private void NotifyRecordListChanged()
    {
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(IsRecordListEmpty));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(RecordPageStatus));
        OnPropertyChanged(nameof(HasPreviousRecordPage));
        OnPropertyChanged(nameof(HasNextRecordPage));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyFiltersCommand.NotifyCanExecuteChanged();
        ApplyRecordFiltersCommand.NotifyCanExecuteChanged();
        PreviousSessionPageCommand.NotifyCanExecuteChanged();
        NextSessionPageCommand.NotifyCanExecuteChanged();
        PreviousRecordPageCommand.NotifyCanExecuteChanged();
        NextRecordPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One kind of load. Beginning a request cancels the one it replaces and stamps a
    /// generation; only the newest generation is allowed to publish. Retiring the slot
    /// invalidates every generation at once, which is how Dispose stops a late result
    /// from touching a closed page.
    /// </summary>
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

        public bool IsCurrent(long generation)
        {
            lock (_sync)
            {
                return !_disposed && generation == _generation;
            }
        }

        /// <summary>
        /// Releases one request's token source. The source is disposed by whoever
        /// created it, after its work has finished, so a cancelled predecessor never
        /// disposes a source another operation is still registering callbacks on.
        /// </summary>
        public void Retire(CancellationTokenSource source, long generation)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, source))
                {
                    _current = null;
                }

                _ = generation;
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
                // Bumping the generation invalidates everything already in flight.
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
                // The owning request already finished and disposed it; nothing to cancel.
            }
        }
    }

    private readonly struct RequestTicket(
        RequestSlot slot,
        CancellationTokenSource source,
        long generation)
    {
        public CancellationToken Token => source.Token;

        /// <summary>This is still the newest request of its kind, and may publish.</summary>
        public bool IsCurrent => slot.IsCurrent(generation);

        public void Complete() => slot.Retire(source, generation);
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
}
