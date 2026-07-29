using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly ObservableCollection<LiveCaptureSessionRow> _sessions = [];
    private readonly ObservableCollection<LiveCaptureRecordRow> _records = [];
    private readonly ObservableCollection<LiveCaptureDiagnosticRow> _diagnostics = [];

    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _recordCts;
    private CancellationTokenSource? _rawXmlCts;
    private long _sessionGeneration;
    private long _recordGeneration;
    private long _rawXmlGeneration;
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

    public LiveHistoryViewModel(ILiveHistoryQueryService queryService)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));

        RefreshCommand = new AsyncCommand(
            () => LoadSessionsAsync(resetPage: false),
            () => !IsBusy,
            ShowUnexpectedError);
        ApplyFiltersCommand = new AsyncCommand(
            () => LoadSessionsAsync(resetPage: true),
            () => !IsBusy,
            ShowUnexpectedError);
        PreviousSessionPageCommand = new AsyncCommand(
            () => MoveSessionPageAsync(-PageSize),
            () => !IsBusy && HasPreviousSessionPage,
            ShowUnexpectedError);
        NextSessionPageCommand = new AsyncCommand(
            () => MoveSessionPageAsync(PageSize),
            () => !IsBusy && HasNextSessionPage,
            ShowUnexpectedError);
        PreviousRecordPageCommand = new AsyncCommand(
            () => MoveRecordPageAsync(-PageSize),
            () => !IsBusy && HasPreviousRecordPage,
            ShowUnexpectedError);
        NextRecordPageCommand = new AsyncCommand(
            () => MoveRecordPageAsync(PageSize),
            () => !IsBusy && HasNextRecordPage,
            ShowUnexpectedError);
        ApplyRecordFiltersCommand = new AsyncCommand(
            () => LoadRecordsAsync(resetPage: true),
            () => !IsBusy && SelectedSession is not null,
            ShowUnexpectedError);

        Sessions = new ReadOnlyObservableCollection<LiveCaptureSessionRow>(_sessions);
        Records = new ReadOnlyObservableCollection<LiveCaptureRecordRow>(_records);
        Diagnostics = new ReadOnlyObservableCollection<LiveCaptureDiagnosticRow>(_diagnostics);
    }

    public string Disclosure { get; } = HistoryDisclosure;

    public ReadOnlyObservableCollection<LiveCaptureSessionRow> Sessions { get; }

    public ReadOnlyObservableCollection<LiveCaptureRecordRow> Records { get; }

    public ReadOnlyObservableCollection<LiveCaptureDiagnosticRow> Diagnostics { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand ApplyFiltersCommand { get; }

    public AsyncCommand ApplyRecordFiltersCommand { get; }

    public AsyncCommand PreviousSessionPageCommand { get; }

    public AsyncCommand NextSessionPageCommand { get; }

    public AsyncCommand PreviousRecordPageCommand { get; }

    public AsyncCommand NextRecordPageCommand { get; }

    /// <summary>True once a load has run and the live capture tables were unusable.</summary>
    public bool IsUnavailable => _availability is not null && !_availability.IsReady;

    public string UnavailableMessage => _availability?.Message ?? string.Empty;

    public bool HasSessions => _sessions.Count > 0;

    public bool IsSessionListEmpty =>
        _availability is not null && _availability.IsReady && _sessions.Count == 0;

    public bool HasRecords => _records.Count > 0;

    public bool IsRecordListEmpty => SelectedSession is not null && _records.Count == 0;

    public bool HasDiagnostics => _diagnostics.Count > 0;

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
                OnPropertyChanged(nameof(SelectedSessionSummary));
                OnPropertyChanged(nameof(SelectedSessionIsIncomplete));
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
        RunSafelyAsync(async () =>
        {
            if (resetPage)
            {
                _sessionOffset = 0;
            }

            var generation = Interlocked.Increment(ref _sessionGeneration);
            using var cancellation = ReplaceCancellation(ref _sessionCts);
            var token = cancellation.Token;

            var availability = await _queryService
                .GetAvailabilityAsync(token)
                .ConfigureAwait(true);
            if (generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }

            _availability = availability;
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(UnavailableMessage));
            if (!availability.IsReady)
            {
                ClearSessions();
                ClearRecords();
                return;
            }

            var page = await _queryService
                .GetSessionsAsync(
                    new LiveHistorySessionQuery(
                        FilterPresentation.ParseOptionalUtc(FromUtcText, "开始时间"),
                        FilterPresentation.ParseOptionalUtc(ToUtcText, "结束时间"),
                        SessionState,
                        new PageRequest(_sessionOffset, PageSize)),
                    token)
                .ConfigureAwait(true);

            // A page produced for filters the user has already replaced must not land.
            if (generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }

            RejectOversizedPage(page.Items.Count);
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
        RunSafelyAsync(async () =>
        {
            var session = SelectedSession;
            if (session is null)
            {
                ClearRecords();
                return;
            }

            if (resetPage)
            {
                _recordOffset = 0;
            }

            var generation = Interlocked.Increment(ref _recordGeneration);
            using var cancellation = ReplaceCancellation(ref _recordCts);
            var token = cancellation.Token;

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
                    token)
                .ConfigureAwait(true);
            var diagnostics = await _queryService
                .GetSessionDiagnosticsAsync(session.LiveSessionId, token)
                .ConfigureAwait(true);

            if (generation != Interlocked.Read(ref _recordGeneration))
            {
                return;
            }

            RejectOversizedPage(page.Items.Count);
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
        RunSafelyAsync(async () =>
        {
            var record = SelectedRecord;
            if (record is null)
            {
                SetRawXml(null);
                return;
            }

            var generation = Interlocked.Increment(ref _rawXmlGeneration);
            using var cancellation = ReplaceCancellation(ref _rawXmlCts);

            var document = await _queryService
                .GetRecordRawXmlAsync(record.LiveEvidenceId, cancellation.Token)
                .ConfigureAwait(true);

            if (generation != Interlocked.Read(ref _rawXmlGeneration))
            {
                return;
            }

            SetRawXml(document);
            if (document is null)
            {
                ErrorMessage = "找不到该记录的原始 XML。";
            }
        });

    /// <summary>Cancels anything still running. Called when the window closes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAndDispose(ref _sessionCts);
        CancelAndDispose(ref _recordCts);
        CancelAndDispose(ref _rawXmlCts);
    }

    protected override void OnBusyStateChanged()
    {
        NotifyCommands();
        base.OnBusyStateChanged();
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
    /// Replaces the token source for one kind of load, cancelling whatever it superseded.
    /// The returned source is owned by the caller's <c>using</c>.
    /// </summary>
    private static CancellationTokenSource ReplaceCancellation(
        ref CancellationTokenSource? field)
    {
        var created = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref field, created);
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already retired by Dispose; nothing left to cancel.
            }
        }

        return created;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? field)
    {
        var existing = Interlocked.Exchange(ref field, null);
        if (existing is null)
        {
            return;
        }

        try
        {
            existing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        existing.Dispose();
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
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyFiltersCommand.NotifyCanExecuteChanged();
        ApplyRecordFiltersCommand.NotifyCanExecuteChanged();
        PreviousSessionPageCommand.NotifyCanExecuteChanged();
        NextSessionPageCommand.NotifyCanExecuteChanged();
        PreviousRecordPageCommand.NotifyCanExecuteChanged();
        NextRecordPageCommand.NotifyCanExecuteChanged();
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
