using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public sealed class DiagnosticsViewModel : PagedViewModelBase<DiagnosticRow>
{
    private readonly IViewerQueryService _queryService;
    private readonly IReadOnlyList<SeverityFilterOption> _severityOptions =
        FilterPresentation.SeverityOptions;
    private ImportDiagnosticSeverity? _selectedSeverity;
    private string? _fromUtcText;
    private string? _toUtcText;
    private string? _textContains;

    public DiagnosticsViewModel(IViewerQueryService queryService)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
    }

    public IReadOnlyList<SeverityFilterOption> SeverityOptions =>
        _severityOptions;

    public ImportDiagnosticSeverity? SelectedSeverity
    {
        get => _selectedSeverity;
        set => SetProperty(ref _selectedSeverity, value);
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

    public string? TextContains
    {
        get => _textContains;
        set => SetProperty(ref _textContains, value);
    }

    protected override Task<PageResult<DiagnosticRow>> QueryPageAsync(
        PageRequest page)
    {
        var query = new DiagnosticQuery(
            SelectedSeverity,
            FilterPresentation.ParseOptionalUtc(FromUtcText, nameof(FromUtcText)),
            FilterPresentation.ParseOptionalUtc(ToUtcText, nameof(ToUtcText)),
            string.IsNullOrWhiteSpace(TextContains) ? null : TextContains.Trim(),
            page);
        query.Validate();
        return _queryService.GetDiagnosticsAsync(query);
    }
}
