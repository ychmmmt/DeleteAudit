using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public sealed class DeleteSessionsViewModel :
    AuditPagedViewModelBase<DeleteSessionRow>
{
    private readonly IViewerQueryService _queryService;

    public DeleteSessionsViewModel(IViewerQueryService queryService)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
    }

    protected override Task<PageResult<DeleteSessionRow>> QueryPageAsync(
        PageRequest page) =>
        _queryService.GetSessionsAsync(CreateQuery(page));
}
