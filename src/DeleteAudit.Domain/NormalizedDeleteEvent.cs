namespace DeleteAudit.Domain;

public sealed record NormalizedDeleteEvent(
    string DeleteEventId,
    string RawEventId,
    string? ComputerName,
    int SourceEventId,
    long? EventRecordId,
    DateTimeOffset OccurredUtc,
    string? FullPath,
    AuditObjectKind ObjectKind,
    int? ProcessId,
    string? ProcessPath,
    string? ProcessGuid,
    string? CommandLine,
    int? ParentProcessId,
    string? ParentProcessPath,
    string? ParentProcessGuid,
    string? UserName,
    string? UserSid,
    DeletePermissionType DeletePermission,
    bool ArchiveExpected,
    IReadOnlyList<string> MissingFields);
