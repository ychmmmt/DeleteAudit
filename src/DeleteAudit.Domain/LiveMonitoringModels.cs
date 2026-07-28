namespace DeleteAudit.Domain;

public enum LiveChannelAvailability
{
    Available,
    Unavailable,
    AccessDenied,
    Disabled,
    UnknownError
}

public enum LiveMonitoringState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

public enum LiveEventOutcome
{
    DeleteFact,
    ProcessContext,
    SecurityEvidence,
    Ignored,
    Error
}

/// <summary>
/// Hard limits for the live preview pipeline. These are production constants: no UI
/// or caller may raise them.
/// </summary>
public static class LiveMonitoringLimits
{
    /// <summary>
    /// Maximum size of a single event's XML, in UTF-16 code units, that may enter the
    /// queue. Note that <c>EventRecord.ToXml()</c> has already materialised one string
    /// by the time this limit is applied: the limit bounds queue residency and later
    /// parsing memory, it does not eliminate that initial allocation.
    /// </summary>
    public const int MaxEventXmlCharacters = 1_048_576;

    /// <summary>Maximum diagnostics retained in memory (and persisted) per session.</summary>
    public const int MaxDiagnostics = 256;

    /// <summary>
    /// Maximum captured records held in memory before the consumer flushes them to the
    /// database. A hard cap, not a tuning knob: it bounds both the transaction size and
    /// how much a crash can lose.
    /// </summary>
    public const int MaxCaptureBatchRecords = 64;

    /// <summary>
    /// Age at which a non-empty partial capture batch is scheduled for persistence.
    /// The deadline starts when the first record enters an empty batch and is not
    /// extended by later records.
    /// </summary>
    public static readonly TimeSpan CaptureFlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>Maximum characters persisted from a captured record's detail text.</summary>
    public const int MaxCaptureDetailCharacters = 2_048;

    /// <summary>Maximum characters persisted from a captured record's error code.</summary>
    public const int MaxCaptureErrorCodeCharacters = 128;

    public static string? TruncateDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? null
            : Truncate(detail.Trim(), MaxCaptureDetailCharacters);

    public static string? TruncateErrorCode(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Truncate(code.Trim(), MaxCaptureErrorCodeCharacters);

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    /// <summary>Maximum characters retained from any diagnostic message.</summary>
    public const int MaxDiagnosticMessageCharacters = 2_048;

    public static string TruncateMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(no message)";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= MaxDiagnosticMessageCharacters
            ? trimmed
            : trimmed[..MaxDiagnosticMessageCharacters];
    }
}

public sealed record LiveChannelStatus(
    string ChannelName,
    LiveChannelAvailability Availability,
    string? Detail = null)
{
    public bool CanSubscribe => Availability == LiveChannelAvailability.Available;
}

/// <summary>
/// A read-only snapshot of one live Windows event log record. The raw XML is the
/// evidence; every other field is what the channel itself reported and stays null
/// when the record does not carry it. Nothing here is ever invented.
/// </summary>
public sealed record LiveEventRecord(
    long? RecordId,
    string? ProviderName,
    string ChannelName,
    string? MachineName,
    DateTimeOffset? TimeCreatedUtc,
    string RawXml);

public sealed record LiveMonitoringDiagnostic(
    string Code,
    string Message,
    ImportDiagnosticSeverity Severity,
    string Stage,
    DateTimeOffset OccurredUtc);

/// <summary>
/// Per-session counts. The three classification counts are kept separate on purpose:
/// only <see cref="DeleteFact"/> represents an observed delete, and neither process
/// context nor security evidence may ever be presented as one.
/// </summary>
public sealed record LiveMonitoringCounters(
    long Received = 0,
    long DeleteFact = 0,
    long ProcessContext = 0,
    long SecurityEvidence = 0,
    long Ignored = 0,
    long Error = 0,
    long Dropped = 0,
    long LateDiscarded = 0,
    long SuppressedDiagnostics = 0)
{
    public static LiveMonitoringCounters Empty { get; } = new();

    /// <summary>Classified events; equals the sum of the three classification counts.</summary>
    public long Parsed => DeleteFact + ProcessContext + SecurityEvidence;

    /// <summary>
    /// Every accepted record is accounted for exactly once. Records that arrived after
    /// the session stopped accepting are tracked in <see cref="LateDiscarded"/> and are
    /// deliberately outside this equation: they never belonged to this session.
    /// </summary>
    public bool IsBalanced =>
        Received == Parsed + Ignored + Error + Dropped;
}

public sealed record LiveMonitoringSession(
    string LiveSessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? StoppedUtc,
    IReadOnlyList<LiveChannelStatus> ChannelStatuses,
    LiveMonitoringCounters Counters,
    LiveMonitoringState FinalState,
    int QueueCapacity,
    string ApplicationVersion);

public sealed record LiveChannelSubscription(
    string ChannelName,
    IReadOnlyList<int> EventIds,
    string ExpectedProviderName)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ChannelName))
        {
            throw new ArgumentException(
                "A channel name is required.",
                nameof(ChannelName));
        }

        if (string.IsNullOrWhiteSpace(ExpectedProviderName))
        {
            throw new ArgumentException(
                "An expected provider name is required.",
                nameof(ExpectedProviderName));
        }

        if (EventIds is null || EventIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one event ID is required; unfiltered channel reads are not allowed.",
                nameof(EventIds));
        }

        if (EventIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(EventIds),
                "Event IDs must be positive.");
        }
    }

    public bool Accepts(string? providerName, int eventId) =>
        EventIds.Contains(eventId)
        && string.Equals(
            providerName,
            ExpectedProviderName,
            StringComparison.OrdinalIgnoreCase);
}

public sealed record LiveEventSubscription(
    IReadOnlyList<LiveChannelSubscription> Channels)
{
    /// <summary>
    /// Live monitoring always starts at "now". Replaying the whole channel history is
    /// not offered: it is the offline import path's job and would flood the queue.
    /// </summary>
    public bool ReadExistingEvents { get; private init; }

    public void Validate()
    {
        if (Channels is null || Channels.Count == 0)
        {
            throw new ArgumentException(
                "At least one channel subscription is required.",
                nameof(Channels));
        }

        foreach (var channel in Channels)
        {
            channel.Validate();
        }
    }

    public LiveChannelSubscription? Find(string? channelName) =>
        channelName is null
            ? null
            : Channels.FirstOrDefault(channel => string.Equals(
                channel.ChannelName,
                channelName,
                StringComparison.OrdinalIgnoreCase));
}

public static class LiveMonitoringChannels
{
    public const string SysmonOperational = "Microsoft-Windows-Sysmon/Operational";
    public const string Security = "Security";
    public const string SysmonProvider = "Microsoft-Windows-Sysmon";
    public const string SecurityProvider = "Microsoft-Windows-Security-Auditing";

    public static IReadOnlyList<int> SysmonEventIds { get; } = [1, 23, 26];

    public static IReadOnlyList<int> SecurityEventIds { get; } = [4663];

    public static IReadOnlyList<string> All { get; } = [SysmonOperational, Security];

    public static LiveEventSubscription CreateDefaultSubscription() =>
        new(
        [
            new LiveChannelSubscription(
                SysmonOperational,
                SysmonEventIds,
                SysmonProvider),
            new LiveChannelSubscription(
                Security,
                SecurityEventIds,
                SecurityProvider)
        ]);

    public static LiveEventSubscription CreateSubscription(
        IEnumerable<LiveChannelStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var available = statuses
            .Where(status => status.CanSubscribe)
            .Select(status => status.ChannelName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var channels = CreateDefaultSubscription()
            .Channels
            .Where(channel => available.Contains(channel.ChannelName))
            .ToArray();
        return new LiveEventSubscription(channels);
    }
}
