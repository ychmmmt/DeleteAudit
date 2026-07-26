namespace DeleteAudit.Domain;

public sealed record ImportRequest(
    string InputFilePath,
    long MaximumFileSizeBytes,
    string JsonlOutputDirectory,
    string ApplicationVersion,
    int SchemaVersion)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InputFilePath))
        {
            throw new ArgumentException("An explicit input file path is required.", nameof(InputFilePath));
        }

        if (!Path.IsPathFullyQualified(InputFilePath))
        {
            throw new ArgumentException("The input file path must be fully qualified.", nameof(InputFilePath));
        }

        if (MaximumFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileSizeBytes));
        }

        if (string.IsNullOrWhiteSpace(JsonlOutputDirectory))
        {
            throw new ArgumentException("A JSONL output directory is required.", nameof(JsonlOutputDirectory));
        }

        if (!Path.IsPathFullyQualified(JsonlOutputDirectory))
        {
            throw new ArgumentException(
                "The JSONL output directory must be fully qualified.",
                nameof(JsonlOutputDirectory));
        }

        if (string.IsNullOrWhiteSpace(ApplicationVersion))
        {
            throw new ArgumentException("An application version is required.", nameof(ApplicationVersion));
        }

        if (SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        }
    }
}

public sealed record OfflineInputFileSnapshot(
    string OriginalFileName,
    string NormalizedAbsolutePath,
    long FileSize,
    DateTimeOffset LastWriteUtc,
    string Sha256);

public sealed record ImportDiagnostic(
    string Code,
    string Message,
    ImportDiagnosticSeverity Severity,
    string Stage,
    long? RecordNumber = null);

public sealed record OfflineEventRecord(
    long RecordNumber,
    string? RawXml,
    OfflineRecordState State,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public sealed record OfflineEventSourceResult(
    OfflineInputFileSnapshot? InputFile,
    IReadOnlyList<OfflineEventRecord> Records,
    IReadOnlyList<ImportDiagnostic> Diagnostics,
    bool IsFatal);

public sealed record ImportSession(
    string ImportSessionId,
    string OriginalFileName,
    string NormalizedAbsolutePath,
    long FileSize,
    DateTimeOffset LastWriteUtc,
    string Sha256,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    int TotalRecordCount,
    int SuccessCount,
    int IgnoredCount,
    int ErrorCount,
    string ApplicationVersion,
    int SchemaVersion,
    ImportStatus Status);

public sealed record HighRiskPathSummary(
    string Path,
    AuditRiskLevel RiskLevel,
    int DeleteFactCount);

public sealed record ImportReport(
    OfflineInputFileSnapshot? InputFile,
    int ParsedSuccessCount,
    int ParsedFailureCount,
    IReadOnlyDictionary<int, int> EventIdCounts,
    int DeleteFactCount,
    IReadOnlyDictionary<CorrelationConfidence, int> CorrelationConfidenceCounts,
    int WarningSessionCount,
    int CriticalSessionCount,
    IReadOnlyList<HighRiskPathSummary> TopHighRiskPaths,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public sealed record ImportResult(
    ImportStatus Status,
    ImportSession? Session,
    ImportReport Report,
    bool DatabaseCommitted,
    string? JsonlFilePath,
    string? ManifestFilePath);
