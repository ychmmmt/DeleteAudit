using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Persistence;

public sealed record PreparedImportRecord(
    OfflineEventRecord SourceRecord,
    ImportRecordOutcome Outcome,
    RawWindowsEvent? RawEvent,
    ProcessContextEvent? ProcessContext);

public sealed record PreparedDeleteProjection(
    CorrelationResult CorrelationResult,
    SessionAggregationResult SessionAggregationResult);

public sealed record PreparedImport(
    string SourceKind,
    ImportSession ImportSession,
    IReadOnlyList<PreparedImportRecord> Records,
    IReadOnlyList<PreparedDeleteProjection> DeleteProjections,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public sealed record OfflineImportCommitResult(
    ImportStatus Status,
    ImportSession Session,
    bool DatabaseCommitted,
    int InsertedRawEventCount,
    int InsertedDeleteEventCount);

public sealed record ImportOutputUpdate(
    ImportStatus Status,
    string? OutputStatus,
    string? JsonlOutputPath,
    string? JsonlOutputSha256,
    string? ManifestOutputPath,
    ImportDiagnostic? Diagnostic = null);
