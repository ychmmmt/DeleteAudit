using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Domain;

namespace DeleteAudit.UnitTests.LiveMonitoring;

/// <summary>
/// Fakes for the live preview pipeline. They model delivery, faults and lifecycle so
/// the real production classes can be exercised; none of them reimplements production
/// logic, and none of them touches a real Windows event log.
/// </summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

internal sealed class FakeProbe : ILiveEventChannelProbe
{
    private readonly IReadOnlyList<LiveChannelStatus> _statuses;

    private FakeProbe(IReadOnlyList<LiveChannelStatus> statuses)
    {
        _statuses = statuses;
    }

    public int ProbeCount { get; private set; }

    public static FakeProbe With(params LiveChannelStatus[] statuses) =>
        new(statuses);

    public static FakeProbe AllAvailable() => new(BothAvailable);

    public static IReadOnlyList<LiveChannelStatus> BothAvailable { get; } =
    [
        new LiveChannelStatus(
            LiveMonitoringChannels.SysmonOperational,
            LiveChannelAvailability.Available),
        new LiveChannelStatus(
            LiveMonitoringChannels.Security,
            LiveChannelAvailability.Available)
    ];

    public Task<IReadOnlyList<LiveChannelStatus>> ProbeAsync(
        IReadOnlyList<string> channelNames,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCount++;
        return Task.FromResult(_statuses);
    }
}

/// <summary>A probe that only completes once the supplied handle is released.</summary>
internal sealed class BlockingProbe(ManualResetEventSlim gate, TimeSpan patience)
    : ILiveEventChannelProbe
{
    public Task<IReadOnlyList<LiveChannelStatus>> ProbeAsync(
        IReadOnlyList<string> channelNames,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<LiveChannelStatus>>(
            () =>
            {
                // Waits on a real signal; the timeout only guards against a hung test.
                gate.Wait(patience);
                cancellationToken.ThrowIfCancellationRequested();
                return FakeProbe.BothAvailable;
            },
            CancellationToken.None);
}

/// <summary>
/// Models one watcher per subscribed channel, including delivery after disposal so
/// late-callback behaviour can be tested.
/// </summary>
internal sealed class FakeLiveEventSource : ILiveEventSource
{
    private readonly List<FakeChannelWatcher> _watchers = [];
    private readonly object _sync = new();

    public bool IsRunning { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public Exception? StartException { get; init; }

    /// <summary>
    /// When set, the source faults from inside StartAsync — before the service has
    /// published its Running state. This reproduces, deterministically, the interleaving
    /// where a real fault is already recorded while the UI state says otherwise.
    /// </summary>
    public string? FaultDuringStart { get; init; }

    public LiveEventSubscription? LastSubscription { get; private set; }

    public IReadOnlyList<FakeChannelWatcher> Watchers
    {
        get
        {
            lock (_sync)
            {
                return [.. _watchers];
            }
        }
    }

    public int LiveWatcherCount
    {
        get
        {
            lock (_sync)
            {
                return _watchers.Count(watcher => !watcher.IsDisposed);
            }
        }
    }

    public Task StartAsync(
        LiveEventSubscription subscription,
        ILiveEventSink sink,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        subscription.Validate();
        if (StartException is not null)
        {
            return Task.FromException(StartException);
        }

        lock (_sync)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            StartCount++;
            LastSubscription = subscription;
            foreach (var channel in subscription.Channels)
            {
                _watchers.Add(new FakeChannelWatcher(channel.ChannelName, sink));
            }

            IsRunning = true;
        }

        if (FaultDuringStart is not null)
        {
            sink.Fault("live_watcher_failed", FaultDuringStart);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                StopCount++;
                foreach (var watcher in _watchers)
                {
                    watcher.Dispose();
                }

                IsRunning = false;
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            DisposeCount++;
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            IsRunning = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Delivers through the first live watcher, like a real channel would.</summary>
    public void Publish(LiveEventRecord record) => Watcher(0).Publish(record);

    public void Fault(string code, string message) => Watcher(0).Fault(code, message);

    /// <summary>
    /// The index-th watcher of the currently running subscription. Watchers from
    /// earlier sessions stay in <see cref="Watchers"/> so late-callback behaviour can
    /// still be exercised, but they are never returned here.
    /// </summary>
    public FakeChannelWatcher Watcher(int index)
    {
        lock (_sync)
        {
            return _watchers.Where(watcher => !watcher.IsDisposed).ElementAt(index);
        }
    }
}

/// <summary>
/// One fake watcher. It keeps publishing after Dispose so tests can prove that a late
/// in-flight callback cannot pollute a newer session.
/// </summary>
internal sealed class FakeChannelWatcher : IDisposable
{
    private readonly ILiveEventSink _sink;

    public FakeChannelWatcher(string channelName, ILiveEventSink sink)
    {
        ChannelName = channelName;
        _sink = sink;
    }

    public string ChannelName { get; }

    public bool IsDisposed { get; private set; }

    public void Publish(LiveEventRecord record) => _sink.Publish(record);

    public void Fault(string code, string message) => _sink.Fault(code, message);

    public void Report(LiveMonitoringDiagnostic diagnostic) => _sink.Report(diagnostic);

    public void Dispose() => IsDisposed = true;
}

internal sealed class FakeRepository : ILiveMonitoringRepository
{
    private readonly List<LiveMonitoringSession> _sessions = [];
    private readonly List<LiveCaptureSessionStart> _starts = [];
    private readonly List<LiveCaptureCompletion> _completions = [];
    private readonly List<LiveCaptureRecord> _records = [];
    private readonly List<int> _batchSizes = [];
    private readonly List<IReadOnlyList<LiveMonitoringDiagnostic>> _diagnostics = [];
    private readonly object _sync = new();

    public Exception? SaveException { get; init; }

    public Exception? ValidateException { get; init; }

    public Exception? StartException { get; init; }

    public Exception? AppendException { get; init; }

    /// <summary>Held open to park a save mid-flight and interleave a concurrent Stop.</summary>
    public ManualResetEventSlim? SaveGate { get; init; }

    /// <summary>Held open to park an append mid-flight.</summary>
    public ManualResetEventSlim? AppendGate { get; init; }

    public TimeSpan SaveGatePatience { get; init; } = TimeSpan.FromSeconds(10);

    public int SaveCount { get; private set; }

    public int ValidateCount { get; private set; }

    public int StartCount { get; private set; }

    public int AppendCount { get; private set; }

    public IReadOnlyList<LiveMonitoringSession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return [.. _sessions];
            }
        }
    }

    public IReadOnlyList<LiveCaptureSessionStart> Starts
    {
        get
        {
            lock (_sync)
            {
                return [.. _starts];
            }
        }
    }

    public IReadOnlyList<LiveCaptureCompletion> Completions
    {
        get
        {
            lock (_sync)
            {
                return [.. _completions];
            }
        }
    }

    public IReadOnlyList<LiveCaptureRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return [.. _records];
            }
        }
    }

    public IReadOnlyList<int> BatchSizes
    {
        get
        {
            lock (_sync)
            {
                return [.. _batchSizes];
            }
        }
    }

    public IReadOnlyList<LiveMonitoringDiagnostic> LastDiagnostics
    {
        get
        {
            lock (_sync)
            {
                return _diagnostics.Count == 0 ? [] : _diagnostics[^1];
            }
        }
    }

    public Task ValidateSchemaAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ValidateCount++;
        }

        return ValidateException is null
            ? Task.CompletedTask
            : Task.FromException(ValidateException);
    }

    public Task StartCaptureSessionAsync(
        LiveCaptureSessionStart start,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            StartCount++;
            if (StartException is not null)
            {
                return Task.FromException(StartException);
            }

            _starts.Add(start);
        }

        return Task.CompletedTask;
    }

    public Task AppendRecordsAsync(
        IReadOnlyList<LiveCaptureRecord> records,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            AppendCount++;
            _batchSizes.Add(records.Count);
            if (AppendException is not null)
            {
                return Task.FromException(AppendException);
            }

            _records.AddRange(records);
        }

        // Waits on a real signal; the timeout only guards against a hung test.
        AppendGate?.Wait(SaveGatePatience, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task CompleteSessionAsync(
        LiveCaptureCompletion completion,
        LiveMonitoringSession session,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SaveCount++;
            if (SaveException is not null)
            {
                return Task.FromException(SaveException);
            }

            _completions.Add(completion);
            _sessions.Add(session);
            _diagnostics.Add(diagnostics);
        }

        // Waits on a real signal; the timeout only guards against a hung test.
        SaveGate?.Wait(SaveGatePatience, CancellationToken.None);
        return Task.CompletedTask;
    }
}

internal static class LiveEventFixtures
{
    public static LiveEventRecord SysmonRecord(string rawXml) =>
        new(
            1,
            LiveMonitoringChannels.SysmonProvider,
            LiveMonitoringChannels.SysmonOperational,
            "LAB-PC",
            new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
            rawXml);

    public static LiveEventRecord SecurityRecord(string rawXml) =>
        new(
            2,
            LiveMonitoringChannels.SecurityProvider,
            LiveMonitoringChannels.Security,
            "LAB-PC",
            new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
            rawXml);

    public static string SysmonDelete(long recordId = 1) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Sysmon" />
             <EventID>26</EventID>
             <TimeCreated SystemTime="2026-07-25T09:00:01.0000000Z" />
             <EventRecordID>{recordId}</EventRecordID>
             <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="TargetFilename">C:\Work\live-{recordId}.txt</Data>
             <Data Name="Image">C:\Tools\cleanup.exe</Data>
             <Data Name="ProcessGuid">11111111-2222-3333-4444-555555555555</Data>
             <Data Name="UtcTime">2026-07-25 09:00:01.000</Data>
             <Data Name="ProcessId">4242</Data>
           </EventData>
         </Event>
         """;

    public static string SysmonProcessCreate() =>
        """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Sysmon" />
            <EventID>1</EventID>
            <TimeCreated SystemTime="2026-07-25T09:00:00.0000000Z" />
            <EventRecordID>7</EventRecordID>
            <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
            <Computer>LAB-PC</Computer>
          </System>
          <EventData>
            <Data Name="Image">C:\Tools\cleanup.exe</Data>
            <Data Name="ProcessGuid">11111111-2222-3333-4444-555555555555</Data>
            <Data Name="CommandLine">cleanup.exe --all</Data>
            <Data Name="UtcTime">2026-07-25 09:00:00.000</Data>
            <Data Name="ProcessId">4242</Data>
          </EventData>
        </Event>
        """;

    public static string Security4663(string accessMask, string accessList) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Security-Auditing" />
             <EventID>4663</EventID>
             <TimeCreated SystemTime="2026-07-25T09:00:02.0000000Z" />
             <EventRecordID>9</EventRecordID>
             <Channel>Security</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="ObjectName">C:\Work\live-1.txt</Data>
             <Data Name="AccessMask">{accessMask}</Data>
             <Data Name="AccessList">{accessList}</Data>
             <Data Name="ProcessId">4242</Data>
             <Data Name="ProcessName">C:\Tools\cleanup.exe</Data>
             <Data Name="SubjectUserName">Alice</Data>
             <Data Name="SubjectUserSid">S-1-5-21-1-2-3-1001</Data>
           </EventData>
         </Event>
         """;

    /// <summary>A well-formed Sysmon-channel event whose XML claims to be a 4663.</summary>
    public static string ForgedSecurityOnSysmonChannel() =>
        """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Security-Auditing" />
            <EventID>4663</EventID>
            <TimeCreated SystemTime="2026-07-25T09:00:03.0000000Z" />
            <EventRecordID>11</EventRecordID>
            <Channel>Security</Channel>
            <Computer>LAB-PC</Computer>
          </System>
          <EventData>
            <Data Name="ObjectName">C:\Work\forged.txt</Data>
            <Data Name="AccessMask">0x10000</Data>
            <Data Name="AccessList">%%1537</Data>
            <Data Name="ProcessId">4242</Data>
            <Data Name="ProcessName">C:\Tools\cleanup.exe</Data>
          </EventData>
        </Event>
        """;

    public static string OversizedSysmonDelete()
    {
        var padding = new string('p', LiveMonitoringLimits.MaxEventXmlCharacters);
        return $"""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-Sysmon" />
                <EventID>26</EventID>
                <TimeCreated SystemTime="2026-07-25T09:00:01.0000000Z" />
                <EventRecordID>99</EventRecordID>
                <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
                <Computer>LAB-PC</Computer>
              </System>
              <EventData>
                <Data Name="TargetFilename">C:\Work\{padding}.txt</Data>
                <Data Name="UtcTime">2026-07-25 09:00:01.000</Data>
                <Data Name="ProcessId">4242</Data>
              </EventData>
            </Event>
            """;
    }
}
