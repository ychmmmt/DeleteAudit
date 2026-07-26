using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Parsing;

public sealed record WindowsEventParseError(
    ParseErrorCode Code,
    string Message,
    string RawXml);

public sealed record WindowsEventParseResult(
    RawWindowsEvent? RawEvent,
    NormalizedDeleteEvent? DeleteEvent,
    ProcessContextEvent? ProcessContext,
    SecurityDeleteEvidence? SecurityEvidence,
    WindowsEventParseError? Error)
{
    public bool IsSuccess => Error is null;
}
