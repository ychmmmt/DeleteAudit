using System.Windows;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Infrastructure.Analysis;
using DeleteAudit.Application.Presentation;
using DeleteAudit.Infrastructure.LiveMonitoring;
using DeleteAudit.Infrastructure.Viewing;
using DeleteAudit.Infrastructure.ViewingImport;

namespace DeleteAudit.Viewer;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var location = ViewerDataLocation.Default;
        var liveMonitoring = new LiveMonitoringService(
            new WindowsLiveEventChannelProbe(),
            new WindowsEventLogWatcherSource(),
            new SqliteLiveMonitoringRepository(location));
        _viewModel = new MainWindowViewModel(
            new SqliteViewerQueryService(location),
            new OfflineViewerImportService(location),
            new OpenFileDialogOfflineFilePicker(),
            new WpfRawXmlPreviewClipboard(),
            liveMonitoring,
            new SqliteLiveHistoryQueryService(location),
            new SqliteLiveAnalysisService(location),
            new WpfUiDispatcher(),
            new WpfNetworkPathImportConfirmation(this));
        DataContext = _viewModel;
        Loaded += OnLoaded;

        // Closing the window releases every live watcher and unsubscribes the page, and
        // cancels any history query still in flight. Neither monitoring nor a query
        // outlives the window, and nothing continues in the background.
        var liveMonitoringPage = _viewModel.LiveMonitoring;
        var liveHistoryPage = _viewModel.LiveHistory;
        Closed += async (_, _) =>
        {
            liveMonitoringPage.Dispose();
            liveHistoryPage.Dispose();
            await liveMonitoring.DisposeAsync().ConfigureAwait(true);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }
}
