using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Output;

public sealed record ImportJsonlRecord(
    long RecordNumber,
    ImportRecordOutcome Outcome,
    int? EventId,
    string? RawEventId,
    string? RawXml,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public sealed record ImportJsonlWriteResult(
    bool Success,
    string? JsonlPath,
    string? ManifestPath,
    int EntryCount,
    string? FirstHash,
    string? LastHash,
    string? JsonlSha256,
    ImportDiagnostic? Diagnostic);

public interface IImportJsonlWriter
{
    // Call only after the import's controlled database transaction has committed.
    Task<ImportJsonlWriteResult> WriteAsync(
        ImportSession importSession,
        IReadOnlyCollection<ImportJsonlRecord> records,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
