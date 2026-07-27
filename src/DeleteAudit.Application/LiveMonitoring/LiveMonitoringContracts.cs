using DeleteAudit.Domain;

namespace DeleteAudit.Application.LiveMonitoring;

/// <summary>
/// Read-only detection of whether a Windows event log channel already exists on this
/// machine. Probing never creates, enables, or reconfigures a channel.
/// </summary>
public interface ILiveEventChannelProbe
{
    Task<IReadOnlyList<LiveChannelStatus>> ProbeAsync(
        IReadOnlyList<string> channelNames,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives records and diagnostics from a running live source. Implementations must
/// never block: a source calls these from the event delivery thread.
/// </summary>
public interface ILiveEventSink
{
    void Publish(LiveEventRecord record);

    void Report(LiveMonitoringDiagnostic diagnostic);

    /// <summary>
    /// Reports an unrecoverable source failure. The session moves to Error and stays
    /// stopped; a source must never restart itself.
    /// </summary>
    void Fault(string code, string message);
}

/// <summary>
/// An in-process, user-started, read-only subscription to already-existing event log
/// channels. Disposing must release every watcher and subscription.
/// </summary>
public interface ILiveEventSource : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync(
        LiveEventSubscription subscription,
        ILiveEventSink sink,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of classifying one live record through the Phase 1A parser.
/// </summary>
public sealed record LiveEventClassification(
    LiveEventRecord Record,
    LiveEventOutcome Outcome,
    bool EstablishesDeleteFact,
    string? Detail);

public sealed record LiveMonitoringSnapshot(
    LiveMonitoringState State,
    IReadOnlyList<LiveChannelStatus> ChannelStatuses,
    LiveMonitoringCounters Counters,
    int QueueCapacity,
    int QueueDepth,
    string? LastError,
    string? LiveSessionId)
{
    public static LiveMonitoringSnapshot Initial(int queueCapacity) =>
        new(
            LiveMonitoringState.Stopped,
            [],
            LiveMonitoringCounters.Empty,
            queueCapacity,
            0,
            null,
            null);
}

public sealed record LiveMonitoringOptions(
    int QueueCapacity = 2048,
    string ApplicationVersion = ApplicationVersionInfo.Current)
{
    public void Validate()
    {
        if (QueueCapacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueueCapacity),
                "The bounded queue capacity must be between 1 and 65536.");
        }

        if (string.IsNullOrWhiteSpace(ApplicationVersion))
        {
            throw new ArgumentException(
                "An application version is required.",
                nameof(ApplicationVersion));
        }
    }
}

/// <summary>
/// Orchestrates one user-initiated live monitoring session. Never starts on its own.
/// </summary>
public interface ILiveMonitoringService : IAsyncDisposable
{
    LiveMonitoringSnapshot Snapshot { get; }

    event EventHandler<LiveMonitoringSnapshot>? SnapshotChanged;

    event EventHandler<LiveEventClassification>? EventClassified;

    Task<IReadOnlyList<LiveChannelStatus>> ProbeChannelsAsync(
        CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence boundary for live monitoring sessions. Writes are additive only:
/// no schema creation, no migration, no destructive statements.
/// </summary>
public interface ILiveMonitoringRepository
{
    Task ValidateSchemaAsync(CancellationToken cancellationToken = default);

    Task SaveSessionAsync(
        LiveMonitoringSession session,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Marshals callbacks onto the UI thread. Keeps the Application layer free of WPF.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
