using DeleteAudit.Application.Importing;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IViewerQueryService _queryService;
    private readonly IOfflineViewerImportService _importService;
    private readonly IOfflineFilePicker _filePicker;
    private readonly INetworkPathImportConfirmation _networkPathConfirmation;
    private readonly Func<Task> _refreshAfterImport;
    private DashboardSummary _summary = EmptySummary();
    private bool _databaseReady;
    private string _lastImportStatus = "尚未执行导入。";

    public DashboardViewModel(
        IViewerQueryService queryService,
        IOfflineViewerImportService importService,
        IOfflineFilePicker filePicker,
        Func<Task>? refreshAfterImport = null,
        INetworkPathImportConfirmation? networkPathConfirmation = null)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
        _importService = importService
            ?? throw new ArgumentNullException(nameof(importService));
        _filePicker = filePicker
            ?? throw new ArgumentNullException(nameof(filePicker));
        // Fail closed: with no interactive surface wired up, a network share is
        // simply never authorised rather than silently allowed.
        _networkPathConfirmation = networkPathConfirmation
            ?? new DeniedNetworkPathImportConfirmation();
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

            // Classification is pure string analysis, so deciding that the
            // selection is remote does not itself touch the share. The answer
            // lives only in this local: it is bound to this one import of this
            // one path, is never stored, and the next selection asks again.
            var networkPathConfirmed = false;
            if (InputPathClassifier.Classify(selectedPath) == InputPathKind.NetworkShare)
            {
                networkPathConfirmed = await _networkPathConfirmation
                    .ConfirmAsync(selectedPath)
                    .ConfigureAwait(true);
                if (!networkPathConfirmed)
                {
                    // Declining is a cancellation, not a failure: exactly the same
                    // quiet semantics as cancelling the file picker. Nothing is
                    // opened, nothing is written, and no error is shown.
                    return;
                }
            }

            var result = await _importService
                .ImportAsync(selectedPath, networkPathConfirmed)
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
