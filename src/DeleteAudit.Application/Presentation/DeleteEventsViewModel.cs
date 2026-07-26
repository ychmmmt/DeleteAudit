using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public sealed class DeleteEventsViewModel :
    AuditPagedViewModelBase<DeleteEventRow>
{
    private readonly IViewerQueryService _queryService;
    private readonly Func<string, Task> _openRawXml;
    private DeleteEventRow? _selectedItem;

    public DeleteEventsViewModel(
        IViewerQueryService queryService,
        Func<string, Task> openRawXml)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
        _openRawXml = openRawXml
            ?? throw new ArgumentNullException(nameof(openRawXml));
        OpenRawXmlCommand = new AsyncCommand(
            OpenRawXmlAsync,
            () => SelectedItem is not null && !IsBusy,
            ShowUnexpectedError);
    }

    public DeleteEventRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OpenRawXmlCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AsyncCommand OpenRawXmlCommand { get; }

    protected override Task<PageResult<DeleteEventRow>> QueryPageAsync(
        PageRequest page) =>
        _queryService.GetEventsAsync(CreateQuery(page));

    protected override void OnBusyStateChanged()
    {
        OpenRawXmlCommand.NotifyCanExecuteChanged();
        base.OnBusyStateChanged();
    }

    private Task OpenRawXmlAsync() =>
        SelectedItem is null
            ? Task.CompletedTask
            : _openRawXml(SelectedItem.DeleteEventId);
}
