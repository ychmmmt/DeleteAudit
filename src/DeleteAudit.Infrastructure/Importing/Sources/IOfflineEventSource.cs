using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Sources;

public interface IOfflineEventSource
{
    string SupportedFileExtension { get; }

    Task<OfflineEventSourceResult> ReadAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default);
}
