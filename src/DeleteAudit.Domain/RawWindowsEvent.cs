namespace DeleteAudit.Domain;

public sealed record RawWindowsEvent(
    string RawEventId,
    WindowsEventSource Source,
    string? ComputerName,
    string? ChannelName,
    string? ProviderName,
    int EventId,
    long? EventRecordId,
    DateTimeOffset EventTimeUtc,
    DateTimeOffset ObservedUtc,
    string RawXml,
    IReadOnlyDictionary<string, string?> EventData,
    IReadOnlyList<string> ParseWarnings);
