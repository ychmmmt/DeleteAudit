using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Persistence;

public interface IOfflineImportRepository
{
    Task ValidateSchemaAsync(CancellationToken cancellationToken = default);

    Task<ImportSession?> FindBySha256Async(
        string sha256,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> FindExistingDeleteEventIdsAsync(
        IReadOnlyCollection<string> deleteEventIds,
        CancellationToken cancellationToken = default);

    Task<OfflineImportCommitResult> CommitAsync(
        PreparedImport preparedImport,
        CancellationToken cancellationToken = default);

    Task UpdateOutputAsync(
        string importSessionId,
        ImportOutputUpdate update,
        CancellationToken cancellationToken = default);
}
