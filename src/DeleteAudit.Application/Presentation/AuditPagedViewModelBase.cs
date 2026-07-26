using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public abstract class AuditPagedViewModelBase<T> : PagedViewModelBase<T>
{
    private AuditRiskLevel? _selectedRisk;
    private string? _fromUtcText;
    private string? _toUtcText;
    private string? _pathContains;
    private string? _processContains;

    public IReadOnlyList<RiskFilterOption> RiskOptions =>
        FilterPresentation.RiskOptions;

    public AuditRiskLevel? SelectedRisk
    {
        get => _selectedRisk;
        set => SetProperty(ref _selectedRisk, value);
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

    public string? PathContains
    {
        get => _pathContains;
        set => SetProperty(ref _pathContains, value);
    }

    public string? ProcessContains
    {
        get => _processContains;
        set => SetProperty(ref _processContains, value);
    }

    protected AuditQuery CreateQuery(PageRequest page)
    {
        var query = new AuditQuery(
            SelectedRisk,
            FilterPresentation.ParseOptionalUtc(FromUtcText, nameof(FromUtcText)),
            FilterPresentation.ParseOptionalUtc(ToUtcText, nameof(ToUtcText)),
            string.IsNullOrWhiteSpace(PathContains) ? null : PathContains.Trim(),
            string.IsNullOrWhiteSpace(ProcessContains) ? null : ProcessContains.Trim(),
            page);
        query.Validate();
        return query;
    }
}
