using DeleteAudit.Domain;

namespace DeleteAudit.Application.Importing;

public interface IOfflineViewerImportService
{
    Task<ImportResult> ImportAsync(
        string inputFilePath,
        CancellationToken cancellationToken = default);
}

public interface IOfflineFilePicker
{
    Task<string?> PickSingleFileAsync(
        CancellationToken cancellationToken = default);
}
