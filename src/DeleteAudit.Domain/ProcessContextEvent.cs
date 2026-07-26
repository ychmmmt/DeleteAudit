namespace DeleteAudit.Domain;

public sealed record ProcessContextEvent(
    string RawEventId,
    string? ComputerName,
    long? EventRecordId,
    DateTimeOffset StartedUtc,
    int? ProcessId,
    string? ProcessGuid,
    string? ProcessPath,
    string? CommandLine,
    int? ParentProcessId,
    string? ParentProcessPath,
    string? ParentProcessGuid,
    string? UserName,
    string? UserSid,
    IReadOnlyList<string> MissingFields);
