namespace DeleteAudit.Domain;

public enum ImportStatus
{
    Completed,
    PartialFailure,
    Failed,
    AlreadyImported
}

public enum ImportDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public enum ImportRecordOutcome
{
    Succeeded,
    Ignored,
    Error
}

public enum OfflineRecordState
{
    Available,
    Unavailable
}
