namespace DeleteAudit.Application.Viewing;

public interface IViewerQueryService
{
    Task<ViewerDatabaseStatus> GetDatabaseStatusAsync(
        CancellationToken cancellationToken = default);

    Task<DashboardSummary> GetDashboardAsync(
        CancellationToken cancellationToken = default);

    Task<PageResult<ImportHistoryRow>> GetImportsAsync(
        ImportHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<PageResult<DeleteSessionRow>> GetSessionsAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default);

    Task<PageResult<DeleteEventRow>> GetEventsAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default);

    Task<PageResult<DiagnosticRow>> GetDiagnosticsAsync(
        DiagnosticQuery query,
        CancellationToken cancellationToken = default);

    Task<RawXmlDocument?> GetDeleteEventRawXmlAsync(
        string deleteEventId,
        CancellationToken cancellationToken = default);
}
