using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.LiveMonitoring;

/// <summary>
/// Subscribes, in this process only and only after the user asked for it, to already
/// existing event log channels. One watcher per channel, every query filtered
/// server-side by event ID, no bookmark created or persisted, and a fault never
/// triggers a restart.
/// </summary>
public sealed class WindowsEventLogWatcherSource : ILiveEventSource
{
    /// <summary>
    /// Creates the per-channel subscription. Held as a seam so the lifecycle
    /// (one watcher per channel, disposal, repeated start) can be exercised without a
    /// real Windows event log; production always uses <see cref="CreateSubscription"/>.
    /// </summary>
    private readonly Func<LiveChannelSubscription, ILiveEventSink, IDisposable> _factory;

    // Held as IDisposable so the teardown path stays platform-neutral; the concrete
    // subscription type is Windows-only and is only named inside guarded code.
    private readonly List<IDisposable> _subscriptions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WindowsEventLogWatcherSource()
        : this(null)
    {
    }

    internal WindowsEventLogWatcherSource(
        Func<LiveChannelSubscription, ILiveEventSink, IDisposable>? factory)
    {
        _factory = factory ?? ((channel, sink) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "Live event log monitoring is supported only on Windows.");
            }

            return CreateSubscription(channel, sink);
        });
    }

    public bool IsRunning { get; private set; }

    /// <summary>Number of live per-channel subscriptions; one watcher each.</summary>
    public int SubscriptionCount
    {
        get
        {
            lock (_subscriptions)
            {
                return _subscriptions.Count;
            }
        }
    }

    public async Task StartAsync(
        LiveEventSubscription subscription,
        ILiveEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(sink);
        subscription.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                // One running subscription set per source; a second start is ignored.
                return;
            }

            foreach (var channel in subscription.Channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var created = _factory(channel, sink);
                lock (_subscriptions)
                {
                    _subscriptions.Add(created);
                }
            }

            IsRunning = true;
        }
        catch
        {
            DisposeSubscriptions();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeSubscriptions();
            IsRunning = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// Builds a server-side event ID filter so the channel never streams unrelated
    /// events into this process. Input is a validated list of positive integers.
    /// </summary>
    public static string BuildEventIdXPath(IReadOnlyList<int> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        if (eventIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one event ID is required.",
                nameof(eventIds));
        }

        var clauses = eventIds.Select(id =>
            $"EventID={id.ToString(CultureInfo.InvariantCulture)}");
        return $"*[System[({string.Join(" or ", clauses)})]]";
    }

    [SupportedOSPlatform("windows")]
    private static Subscription CreateSubscription(
        LiveChannelSubscription channel,
        ILiveEventSink sink)
    {
        var query = new EventLogQuery(
            channel.ChannelName,
            PathType.LogName,
            BuildEventIdXPath(channel.EventIds))
        {
            TolerateQueryErrors = false,
            ReverseDirection = false
        };

        // readExistingEvents: false — monitoring starts at "now" and never replays
        // the channel's history, and no bookmark is supplied or stored.
        var watcher = new EventLogWatcher(query, null, false);
        var subscription = new Subscription(channel.ChannelName, watcher);
        watcher.EventRecordWritten += subscription.Handler(sink);
        try
        {
            watcher.Enabled = true;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }

        return subscription;
    }

    private void DisposeSubscriptions()
    {
        IDisposable[] pending;
        lock (_subscriptions)
        {
            pending = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in pending)
        {
            subscription.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class Subscription : IDisposable
    {
        private readonly string _channelName;
        private readonly EventLogWatcher _watcher;
        private EventHandler<EventRecordWrittenEventArgs>? _handler;

        public Subscription(string channelName, EventLogWatcher watcher)
        {
            _channelName = channelName;
            _watcher = watcher;
        }

        public EventHandler<EventRecordWrittenEventArgs> Handler(ILiveEventSink sink)
        {
            _handler = (_, args) => OnEventRecordWritten(sink, args);
            return _handler;
        }

        public void Dispose()
        {
            try
            {
                _watcher.Enabled = false;
            }
            catch (EventLogException)
            {
                // The channel may already be gone; disposal continues regardless.
            }

            if (_handler is not null)
            {
                _watcher.EventRecordWritten -= _handler;
                _handler = null;
            }

            _watcher.Dispose();
        }

        /// <summary>
        /// Runs on the event delivery thread, so it only converts the record and hands
        /// it to the sink; the sink never blocks and never disposes this watcher from
        /// inside this callback.
        /// </summary>
        private void OnEventRecordWritten(
            ILiveEventSink sink,
            EventRecordWrittenEventArgs args)
        {
            EventRecord? record = null;
            try
            {
                if (args.EventException is not null)
                {
                    sink.Fault(
                        "live_watcher_failed",
                        LiveMonitoringLimits.TruncateMessage(args.EventException.Message));
                    return;
                }

                record = args.EventRecord;
                if (record is null)
                {
                    return;
                }

                sink.Publish(new LiveEventRecord(
                    record.RecordId,
                    record.ProviderName,
                    record.LogName ?? _channelName,
                    record.MachineName,
                    record.TimeCreated is null
                        ? null
                        : new DateTimeOffset(record.TimeCreated.Value.ToUniversalTime(), TimeSpan.Zero),
                    record.ToXml()));
            }
            catch (Exception exception) when (
                exception is EventLogException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
            {
                // A single unreadable record must not tear down the subscription.
                sink.Report(new LiveMonitoringDiagnostic(
                    "live_record_read_failed",
                    LiveMonitoringLimits.TruncateMessage(exception.Message),
                    ImportDiagnosticSeverity.Error,
                    "receive",
                    DateTimeOffset.UtcNow));
            }
            finally
            {
                // Disposed on every path: success, publish failure, and read failure.
                record?.Dispose();
            }
        }
    }
}
