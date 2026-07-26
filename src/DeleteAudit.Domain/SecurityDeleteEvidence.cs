namespace DeleteAudit.Domain;

public sealed record SecurityDeleteEvidence(
    string RawEventId,
    string? ComputerName,
    long? EventRecordId,
    DateTimeOffset OccurredUtc,
    string? ObjectPath,
    int? ProcessId,
    string? ProcessPath,
    string? UserName,
    string? UserSid,
    DeletePermissionType DeletePermission,
    IReadOnlyList<string> MissingFields);
