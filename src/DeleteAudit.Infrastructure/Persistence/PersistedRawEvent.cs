using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Integrity;

namespace DeleteAudit.Infrastructure.Persistence;

public sealed record PersistedRawEvent(
    RawWindowsEvent Event,
    string ChannelEpochId,
    long IngestSequence,
    DateTimeOffset EventLocal,
    string WindowsTimeZoneId,
    HashChainEntry ChainEntry);

public sealed record RawEventSummary(
    string RawEventId,
    string Source,
    int EventId,
    long EventRecordId,
    DateTimeOffset EventUtc,
    string? ComputerName);

public sealed record SqliteWriteResult(int AttemptedCount, int InsertedCount);

public sealed record SqliteBusyRetryOptions(int MaxAttempts, TimeSpan Delay)
{
    public static SqliteBusyRetryOptions Default { get; } = new(3, TimeSpan.FromMilliseconds(25));

    public void Validate()
    {
        if (MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        if (Delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Delay));
        }
    }
}
