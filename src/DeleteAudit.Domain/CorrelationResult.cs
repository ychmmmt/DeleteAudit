namespace DeleteAudit.Domain;

public sealed record CorrelationResult(
    NormalizedDeleteEvent Event,
    CorrelationMethod Method,
    CorrelationConfidence Confidence,
    TimeSpan? TimeDelta,
    string? MatchedProcessRawEventId,
    string? MatchedSecurityRawEventId,
    bool IdentityFieldsEnriched,
    IReadOnlyList<string> Reasons);
