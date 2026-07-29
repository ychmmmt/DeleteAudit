using DeleteAudit.Application.Importing;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public sealed class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// Always visible at the top of the window. It must describe what the application
    /// actually does right now, including the locally persisted live evidence boundary.
    /// </summary>
    public const string CapabilityBanner =
        "当前支持离线日志分析，以及用户手动开启的实时事件接入预览；接收到的受支持事件原始 XML、"
        + "解析与分类结果及相关实时证据会持久保存到本机查看器数据库，但尚无实时历史查看界面。";

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
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        RawXml = new RawXmlViewModel(queryService, rawXmlPreviewClipboard);
        LiveMonitoring = new LiveMonitoringViewModel(liveMonitoringService, uiDispatcher);
        LiveHistory = new LiveHistoryViewModel(liveHistoryQueryService);
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
