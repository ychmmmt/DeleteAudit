namespace DeleteAudit.Application.Viewing;

/// <summary>
/// Read-only access to what a live capture session actually recorded.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a query. There is deliberately no member that deletes, edits, clears
/// or repairs anything: captured evidence is append-only, and the viewer is not a place
/// from which it can be changed.
/// </para>
/// <para>
/// Opening this page must never start monitoring. Reading history and subscribing to a
/// live event log are separate actions, and only the latter is user initiated from the
/// live monitoring page.
/// </para>
/// </remarks>
public interface ILiveHistoryQueryService
{
    /// <summary>
    /// Whether the live capture tables exist. Reported separately from the offline
    /// viewer status so a database without the live evidence migration keeps every other
    /// page working.
    /// </summary>
    Task<LiveHistoryAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<PageResult<LiveCaptureSessionRow>> GetSessionsAsync(
        LiveHistorySessionQuery query,
        CancellationToken cancellationToken = default);

    Task<PageResult<LiveCaptureRecordRow>> GetRecordsAsync(
        LiveHistoryRecordQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LiveCaptureDiagnosticRow>> GetSessionDiagnosticsAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one record's raw XML on demand, truncated inside SQLite to the shared
    /// preview limit. List queries never carry the XML itself.
    /// </summary>
    Task<RawXmlDocument?> GetRecordRawXmlAsync(
        string liveEvidenceId,
        CancellationToken cancellationToken = default);
}
