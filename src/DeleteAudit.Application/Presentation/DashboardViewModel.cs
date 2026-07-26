using DeleteAudit.Application.Importing;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IViewerQueryService _queryService;
    private readonly IOfflineViewerImportService _importService;
    private readonly IOfflineFilePicker _filePicker;
    private readonly Func<Task> _refreshAfterImport;
    private DashboardSummary _summary = EmptySummary();
    private bool _databaseReady;
    private string _lastImportStatus = "尚未执行导入。";

    public DashboardViewModel(
        IViewerQueryService queryService,
        IOfflineViewerImportService importService,
        IOfflineFilePicker filePicker,
        Func<Task>? refreshAfterImport = null)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
        _importService = importService
            ?? throw new ArgumentNullException(nameof(importService));
        _filePicker = filePicker
            ?? throw new ArgumentNullException(nameof(filePicker));
        _refreshAfterImport = refreshAfterImport ?? (() => Task.CompletedTask);

        RefreshCommand = new AsyncCommand(
            LoadAsync,
            () => DatabaseReady && !IsBusy,
            ShowUnexpectedError);
        ImportCommand = new AsyncCommand(
            ImportSelectedFileAsync,
            () => DatabaseReady && !IsBusy,
            ShowUnexpectedError);
    }

    public DashboardSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool DatabaseReady
    {
        get => _databaseReady;
        private set => SetProperty(ref _databaseReady, value);
    }

    public string LastImportStatus
    {
        get => _lastImportStatus;
        private set => SetProperty(ref _lastImportStatus, value);
    }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand ImportCommand { get; }

    public void SetDatabaseReady(bool ready)
    {
        DatabaseReady = ready;
        RefreshCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    public Task LoadAsync() =>
        RunSafelyAsync(async () =>
        {
            Summary = await _queryService
                .GetDashboardAsync()
                .ConfigureAwait(true);
        });

    public Task ImportSelectedFileAsync() =>
        RunSafelyAsync(async () =>
        {
            var selectedPath = await _filePicker
                .PickSingleFileAsync()
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            var result = await _importService
                .ImportAsync(selectedPath)
                .ConfigureAwait(true);
            LastImportStatus = ImportStatusPresentation.Label(result.Status);
            if (result.Status == ImportStatus.Failed)
            {
                var diagnostic = result.Report.Diagnostics.FirstOrDefault(
                    item => item.Severity == ImportDiagnosticSeverity.Error);
                ErrorMessage = diagnostic is null
                    ? LastImportStatus
                    : $"{LastImportStatus}：{diagnostic.Message}";
            }

            Summary = await _queryService
                .GetDashboardAsync()
                .ConfigureAwait(true);
            await _refreshAfterImport().ConfigureAwait(true);
        });

    protected override void OnBusyStateChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        base.OnBusyStateChanged();
    }

    private static DashboardSummary EmptySummary() =>
        new(0, 0, 0, 0, 0, 0, 0, null);
}
