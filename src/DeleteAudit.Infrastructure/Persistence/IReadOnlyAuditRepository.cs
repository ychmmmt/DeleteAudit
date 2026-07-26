namespace DeleteAudit.Infrastructure.Persistence;

public interface IReadOnlyAuditRepository
{
    Task<IReadOnlyList<RawEventSummary>> ReadRawEventsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
