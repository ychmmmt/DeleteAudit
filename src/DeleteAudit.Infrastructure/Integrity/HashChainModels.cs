namespace DeleteAudit.Infrastructure.Integrity;

public sealed record HashChainEntry(
    string JsonLine,
    string ContentHash,
    string PreviousEntryHash,
    string EntryHash);

public sealed record HashChainVerificationResult(
    bool IsValid,
    int VerifiedEntryCount,
    int? FailedEntryIndex,
    string? FailureReason,
    string? LastEntryHash);
