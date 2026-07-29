using DeleteAudit.Application.Analysis;
using DeleteAudit.Application.Importing;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Application.Projection;
using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public sealed class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// Always visible at the top of the window. It must describe what the application
    /// actually does right now, including the locally persisted live evidence boundary.
    /// </summary>
    public const string CapabilityBanner =
        "当前支持离线日志分析、用户手动开启的实时事件接入、已保存的实时历史、派生分析和"
        + "独立 live-owned 规范投影。实时原始 XML、解析/分类结果与相关证据保存在本机查看器"
        + "数据库；实时接入仍须由用户手动开始，本应用不是完整或防篡改的取证系统。";

    private readonly IViewerQueryService _queryService;
    private readonly string _bannerMessage = CapabilityBanner;
    private string _databaseStatusMessage = "正在检查离线数据库…";
    private bool _databaseReady;
    private int _selectedPageIndex;

    public MainWindowViewModel(
        IViewerQueryService queryService,
        IOfflineViewerImportService importService,
        IOfflineFilePicker filePicker,
        IRawXmlPreviewClipboard rawXmlPreviewClipboard,
        ILiveMonitoringService liveMonitoringService,
        ILiveHistoryQueryService liveHistoryQueryService,
        ILiveAnalysisService liveAnalysisService,
        ILiveProjectionService liveProjectionService,
        IUiDispatcher uiDispatcher,
        INetworkPathImportConfirmation? networkPathConfirmation = null)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
        ArgumentNullException.ThrowIfNull(importService);
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(rawXmlPreviewClipboard);
        ArgumentNullException.ThrowIfNull(liveMonitoringService);
        ArgumentNullException.ThrowIfNull(liveHistoryQueryService);
        ArgumentNullException.ThrowIfNull(liveAnalysisService);
        ArgumentNullException.ThrowIfNull(liveProjectionService);
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        RawXml = new RawXmlViewModel(queryService, rawXmlPreviewClipboard);
        LiveMonitoring = new LiveMonitoringViewModel(liveMonitoringService, uiDispatcher);
        LiveProjection = new LiveProjectionViewModel(liveProjectionService);
        LiveHistory = new LiveHistoryViewModel(
            liveHistoryQueryService,
            liveAnalysisService,
            session => LiveProjection.SetSession(
                session?.LiveSessionId,
                session?.IsComplete ?? false));
        ImportHistory = new ImportHistoryViewModel(queryService);
        DeleteSessions = new DeleteSessionsViewModel(queryService);
        DeleteEvents = new DeleteEventsViewModel(queryService, OpenRawXmlAsync);
        Diagnostics = new DiagnosticsViewModel(queryService);
        Dashboard = new DashboardViewModel(
            queryService,
            importService,
            filePicker,
            RefreshListsAfterImportAsync,
            networkPathConfirmation);
    }

    public string BannerMessage => _bannerMessage;

    public string DatabaseStatusMessage
    {
        get => _databaseStatusMessage;
        private set => SetProperty(ref _databaseStatusMessage, value);
    }

    public bool DatabaseReady
    {
        get => _databaseReady;
        private set => SetProperty(ref _databaseReady, value);
    }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set => SetProperty(ref _selectedPageIndex, value);
    }

    public DashboardViewModel Dashboard { get; }

    public ImportHistoryViewModel ImportHistory { get; }

    public DeleteSessionsViewModel DeleteSessions { get; }

    public DeleteEventsViewModel DeleteEvents { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public RawXmlViewModel RawXml { get; }

    public LiveMonitoringViewModel LiveMonitoring { get; }

    /// <summary>
    /// Read-only history of past live captures. It is not loaded by
    /// <see cref="InitializeAsync"/>: reading it is an explicit user action, and the live
    /// evidence tables may legitimately be absent on an older database.
    /// </summary>
    public LiveHistoryViewModel LiveHistory { get; }

    /// <summary>
    /// Explicit live-owned projection over the capture selected in
    /// <see cref="LiveHistory"/>. It is not loaded or run by
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public LiveProjectionViewModel LiveProjection { get; }

    public Task InitializeAsync() =>
        RunSafelyAsync(async () =>
        {
            var databaseStatus = await _queryService
                .GetDatabaseStatusAsync()
                .ConfigureAwait(true);
            DatabaseReady = databaseStatus.IsReady;
            DatabaseStatusMessage = databaseStatus.Message;
            Dashboard.SetDatabaseReady(databaseStatus.IsReady);
            if (!databaseStatus.IsReady)
            {
                ErrorMessage = databaseStatus.Message;
                return;
            }

            await Task.WhenAll(
                    Dashboard.LoadAsync(),
                    ImportHistory.LoadAsync(resetPage: true),
                    DeleteSessions.LoadAsync(resetPage: true),
                    DeleteEvents.LoadAsync(resetPage: true),
                    Diagnostics.LoadAsync(resetPage: true))
                .ConfigureAwait(true);
        });

    private async Task OpenRawXmlAsync(string deleteEventId)
    {
        await RawXml.LoadAsync(deleteEventId).ConfigureAwait(true);
        SelectedPageIndex = 5;
    }

    private Task RefreshListsAfterImportAsync() =>
        Task.WhenAll(
            ImportHistory.LoadAsync(resetPage: true),
            DeleteSessions.LoadAsync(resetPage: true),
            DeleteEvents.LoadAsync(resetPage: true),
            Diagnostics.LoadAsync(resetPage: true));
}
