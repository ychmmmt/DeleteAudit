using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public sealed class ImportHistoryViewModel : PagedViewModelBase<ImportHistoryRow>
{
    private readonly IViewerQueryService _queryService;
    private string? _status;
    private string? _fromUtcText;
    private string? _toUtcText;
    private string? _sourcePathContains;

    public ImportHistoryViewModel(IViewerQueryService queryService)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
    }

    public string? Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

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

    public string? SourcePathContains
    {
        get => _sourcePathContains;
        set => SetProperty(ref _sourcePathContains, value);
    }

    protected override Task<PageResult<ImportHistoryRow>> QueryPageAsync(
        PageRequest page)
    {
        var query = new ImportHistoryQuery(
            string.IsNullOrWhiteSpace(Status) ? null : Status.Trim(),
            FilterPresentation.ParseOptionalUtc(FromUtcText, nameof(FromUtcText)),
            FilterPresentation.ParseOptionalUtc(ToUtcText, nameof(ToUtcText)),
            string.IsNullOrWhiteSpace(SourcePathContains)
                ? null
                : SourcePathContains.Trim(),
            page);
        query.Validate();
        return _queryService.GetImportsAsync(query);
    }
}
