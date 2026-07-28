using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Parsing;

namespace DeleteAudit.Infrastructure.LiveMonitoring;

/// <summary>
/// Drives one user-initiated live capture session: validate schema, record that the
/// session started, probe, subscribe, hand records to a bounded queue, classify them on
/// a background worker, append the captured evidence in bounded batches, then record the
/// completion together with the session summary. Nothing starts by itself and nothing
/// restarts after a fault.
///
/// Scope (Phase 2B.1): raw XML and the classification of each received record are
/// persisted under a dedicated live evidence identity. Correlation, delete session
/// aggregation and risk assessment are still not wired into the live path, and nothing
/// here is written into the offline evidence tables.
/// </summary>
public sealed class LiveMonitoringService : ILiveMonitoringService
{
    public const string SchemaNotReadyMessage =
        "实时监控数据库结构尚未准备完成，请应用 0003 与 0004 migration。";

    public const string SessionStartNotPersistedMessage =
        "无法记录实时接入会话的开始事实，未创建任何订阅。";

    private readonly ILiveEventChannelProbe _probe;
    private readonly ILiveEventSource _source;
    private readonly ILiveMonitoringRepository _repository;
    private readonly LiveMonitoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly WindowsEventXmlParser _parser;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    /// <summary>
    /// Guards every mutable per-session fact: counts, diagnostics, the accepting flag,
    /// the fault flag, the last error, and the fault shutdown task. Lock order is always
    /// transition gate first, then this lock; never the reverse, and never an await
    /// while holding it.
    /// </summary>
    private readonly object _sessionLock = new();

    private readonly List<LiveMonitoringDiagnostic> _diagnostics = [];
    private Counters _counters = Counters.Empty;
    private bool _queueOverflowReported;

    /// <summary>
    /// The authoritative record of whether this session faulted. Set inside
    /// <see cref="_sessionLock"/> the moment a valid fault is observed, and it — not the
    /// asynchronously published UI state — decides the persisted final state.
    /// </summary>
    private bool _sessionFaulted;

    private LiveMonitoringState _state = LiveMonitoringState.Stopped;
    private IReadOnlyList<LiveChannelStatus> _channelStatuses = [];
    private string? _lastError;
    private string? _liveSessionId;
    private DateTimeOffset _startedUtc;
    private bool _disposed;

    /// <summary>
    /// Incremented on every Start. A sink carries the generation it was created for, so
    /// a late callback from a torn-down watcher can never touch a newer session. This is
    /// an in-memory lifecycle marker only — it is not, and must not be presented as, a
    /// forensic channel epoch.
    /// </summary>
    private int _generation;

    private bool _acceptingEvents;

    /// <summary>
    /// Receive position within this session, assigned on the delivery thread before the
    /// queue decision. A record that is dropped or refused for size still consumes one,
    /// so a gap in the persisted sequence is exactly the evidence that something was
    /// received but not stored.
    /// </summary>
    private long _receivedSequence;

    /// <summary>
    /// Whether the session-start row reached the database. Completion may only be written
    /// for a session that was actually started.
    /// </summary>
    private bool _captureStarted;

    /// <summary>Rows this session has committed to live_capture_records.</summary>
    private long _persistedRecordCount;

    /// <summary>
    /// Set once appending evidence has failed. The consumer keeps classifying so the
    /// balance invariant survives, but it stops writing: a faulted session must never
    /// look like a complete capture.
    /// </summary>
    private bool _persistenceFaulted;

    // Three distinct facts, deliberately not collapsed into one flag.
    private bool _completionStarted = true;
    private bool _lifecycleCompleted = true;
    private bool _persisted;

    private LiveEventSubscription? _subscription;
    private Channel<LiveQueuedRecord>? _queue;
    private Task? _consumer;
    private CancellationTokenSource? _consumerCts;
    private Task? _faultShutdown;
    private int _queueDepth;

    public LiveMonitoringService(
        ILiveEventChannelProbe probe,
        ILiveEventSource source,
        ILiveMonitoringRepository repository,
        LiveMonitoringOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? new LiveMonitoringOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _parser = new WindowsEventXmlParser(_timeProvider);
    }

    public event EventHandler<LiveMonitoringSnapshot>? SnapshotChanged;

    public event EventHandler<LiveEventClassification>? EventClassified;

    public LiveMonitoringSnapshot Snapshot => CreateSnapshot();

    /// <summary>Completion has begun; guards re-entry from Stop, fault and Dispose.</summary>
    internal bool CompletionStarted => _completionStarted;

    /// <summary>Teardown finished and the final state was published.</summary>
    internal bool LifecycleCompleted => _lifecycleCompleted;

    /// <summary>
    /// The session summary actually reached the repository. Distinct from
    /// <see cref="LifecycleCompleted"/>: a session can finish cleanly yet fail to persist.
    /// </summary>
    internal bool SessionPersisted => _persisted;

    internal IReadOnlyList<LiveMonitoringDiagnostic> SessionDiagnostics
    {
        get
        {
            lock (_sessionLock)
            {
                return [.. _diagnostics];
            }
        }
    }

    /// <summary>
    /// The queue options for a live session. Synchronous continuations are disabled
    /// explicitly: the producer calls <c>TryWrite</c> while holding
    /// <see cref="_sessionLock"/>, so a consumer continuation must never be inlined onto
    /// the producer's thread.
    /// </summary>
    internal static BoundedChannelOptions CreateQueueOptions(int capacity) =>
        new(capacity)
        {
            // Wait mode combined with TryWrite-only producers gives us an explicit,
            // countable drop instead of a blocked callback thread.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

    public async Task<IReadOnlyList<LiveChannelStatus>> ProbeChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var statuses = await _probe
            .ProbeAsync(LiveMonitoringChannels.All, cancellationToken)
            .ConfigureAwait(false);

        // A cancelled probe must not overwrite whatever the UI is showing now.
        cancellationToken.ThrowIfCancellationRequested();
        _channelStatuses = statuses;
        RaiseSnapshotChanged();
        return statuses;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is LiveMonitoringState.Running
                or LiveMonitoringState.Starting
                or LiveMonitoringState.Stopping)
            {
                // A second Start is a no-op: one session, one watcher set.
                return;
            }

            BeginSession();
            SetState(LiveMonitoringState.Starting);

            try
            {

            // Fail closed before anything subscribes: if the session summary cannot be
            // stored, no watcher is created and no live event is read.
            try
            {
                await _repository
                    .ValidateSchemaAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                AddDiagnostic(
                    "live_schema_not_ready",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "persist");
                await AbortStartAsync(SchemaNotReadyMessage).ConfigureAwait(false);
                return;
            }

            // The start fact is recorded before anything is probed or subscribed, so a
            // capture that later dies abruptly still leaves a row explaining what ran.
            try
            {
                await _repository
                    .StartCaptureSessionAsync(
                        new LiveCaptureSessionStart(
                            _liveSessionId!,
                            _startedUtc,
                            _options.QueueCapacity,
                            _options.ApplicationVersion),
                        cancellationToken)
                    .ConfigureAwait(false);
                _captureStarted = true;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                AddDiagnostic(
                    "live_capture_session_start_failed",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "persist");
                await AbortStartAsync(SessionStartNotPersistedMessage).ConfigureAwait(false);
                return;
            }

            var statuses = await _probe
                .ProbeAsync(LiveMonitoringChannels.All, cancellationToken)
                .ConfigureAwait(false);
            _channelStatuses = statuses;
            foreach (var status in statuses.Where(item => !item.CanSubscribe))
            {
                AddDiagnostic(
                    $"channel_{ToDiagnosticSuffix(status.Availability)}",
                    $"{status.ChannelName}: {status.Detail ?? status.Availability.ToString()}",
                    ImportDiagnosticSeverity.Warning,
                    "probe");
            }

            var subscription = LiveMonitoringChannels.CreateSubscription(statuses);
            if (subscription.Channels.Count == 0)
            {
                AddDiagnostic(
                    "no_subscribable_channel",
                    "No required event log channel is available on this machine; live monitoring cannot start.",
                    ImportDiagnosticSeverity.Error,
                    "subscribe");
                await AbortStartAsync("没有可订阅的事件日志通道。").ConfigureAwait(false);
                return;
            }

            _subscription = subscription;
            _queue = Channel.CreateBounded<LiveQueuedRecord>(
                CreateQueueOptions(_options.QueueCapacity));
            _consumerCts = new CancellationTokenSource();
            _consumer = Task.Run(
                () => ConsumeAsync(_queue.Reader, _consumerCts.Token),
                CancellationToken.None);

            lock (_sessionLock)
            {
                _acceptingEvents = true;
            }

            try
            {
                await _source
                    .StartAsync(subscription, new Sink(this, _generation), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                AddDiagnostic(
                    "live_source_start_failed",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "subscribe");
                await AbortStartAsync(exception.Message).ConfigureAwait(false);
                return;
            }

            // A watcher may already have faulted during StartAsync. The fault fact lives
            // in _sessionFaulted, so publishing Running here cannot mislabel the session;
            // the fault shutdown task will move it to Error.
            SetState(
                SessionFaulted()
                    ? LiveMonitoringState.Error
                    : LiveMonitoringState.Running);
            }
            catch (OperationCanceledException)
            {
                // A cancelled Start must never escape half-built: it could otherwise
                // leave a live watcher, an orphaned consumer, and a start row with no
                // completion, with the service still believing a session is running.
                await AbortCancelledStartAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>
    /// Cleans up a Start that was cancelled part-way. Cancellation before the start row
    /// was persisted is a plain user cancellation: nothing was recorded, so the session
    /// simply returns to Stopped. Once the start row exists the capture is on record, so
    /// the session is torn down and completed as an error instead.
    /// </summary>
    private async Task AbortCancelledStartAsync()
    {
        if (!_captureStarted)
        {
            StopAccepting();
            await ReleasePipelineAsync().ConfigureAwait(false);
            _completionStarted = true;
            _lifecycleCompleted = true;
            SetState(LiveMonitoringState.Stopped);
            return;
        }

        AddDiagnostic(
            "live_start_cancelled",
            "Starting the live capture was cancelled after the session start was recorded.",
            ImportDiagnosticSeverity.Error,
            "subscribe");
        MarkFaulted("实时接入启动被取消。");
        // CancellationToken.None: teardown and the error completion must still run even
        // though the caller's token is already cancelled.
        await CompleteSessionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        // A fault may already be tearing the session down; let it finish first so the
        // session is completed exactly once. Correctness no longer depends on winning
        // this read: CompleteSessionAsync derives the final state from _sessionFaulted.
        await ObserveFaultShutdownAsync().ConfigureAwait(false);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completionStarted)
            {
                // Stopping an already finished session is safe and does nothing.
                return;
            }

            SetState(LiveMonitoringState.Stopping);
            await CompleteSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ObserveFaultShutdownAsync().ConfigureAwait(false);

        await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_completionStarted)
            {
                await CompleteSessionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await ReleasePipelineAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }

        await _source.DisposeAsync().ConfigureAwait(false);
        _transitionGate.Dispose();
    }

    /// <summary>
    /// Tears a partially started session down and reports it as an error. Called with
    /// the transition gate held.
    /// </summary>
    private async Task AbortStartAsync(string message)
    {
        MarkFaulted(message);
        await CompleteSessionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops accepting, releases the pipeline, takes one stable snapshot and persists
    /// exactly one session summary. Must be called with the transition gate held.
    ///
    /// The final state comes from the lock-protected fault flag, never from the
    /// asynchronously published UI state.
    /// </summary>
    private async Task CompleteSessionAsync(CancellationToken cancellationToken)
    {
        if (_completionStarted)
        {
            return;
        }

        _completionStarted = true;
        StopAccepting();

        // ReleasePipelineAsync never throws: it runs every shutdown step and returns
        // whatever went wrong, so one failure can never skip a later step.
        var failures = await ReleasePipelineAsync().ConfigureAwait(false);
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                AddDiagnostic(
                    failure.Code,
                    failure.Exception.Message,
                    ImportDiagnosticSeverity.Error,
                    failure.Stage);
            }

            // Every failure is named; a second one never overwrites the first.
            MarkFaulted(
                "实时管线关闭时发生异常："
                + string.Join(
                    "；",
                    failures.Select(failure =>
                        $"{failure.Code}: {failure.Exception.Message}")));
        }

        // Source stopped, writer completed, consumer drained: the snapshot below is
        // taken under the session lock and can no longer move.
        Counters counters;
        LiveMonitoringDiagnostic[] diagnostics;
        bool faulted;
        long persistedRecords;
        lock (_sessionLock)
        {
            counters = _counters;
            diagnostics = [.. _diagnostics];
            faulted = _sessionFaulted;
            persistedRecords = _persistedRecordCount;
        }

        var finalState = faulted
            ? LiveMonitoringState.Error
            : LiveMonitoringState.Stopped;
        await PersistSessionAsync(
                finalState,
                counters,
                persistedRecords,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);

        _lifecycleCompleted = true;

        // A session that could not record its own completion has not stopped cleanly, so
        // the UI must not say "Stopped". The database rolled the whole completion back:
        // what remains is a start with no completion, i.e. an incomplete capture.
        // Completion is attempted exactly once — a repeated Stop does not retry it.
        SetState(_persisted ? finalState : LiveMonitoringState.Error);
    }

    private void StopAccepting()
    {
        lock (_sessionLock)
        {
            _acceptingEvents = false;
        }
    }

    /// <summary>
    /// Runs the whole shutdown sequence in a fixed order, with each step isolated so an
    /// earlier failure can never skip a later one:
    ///
    ///   stop source → complete writer → await consumer → dispose CTS → clear fields
    ///
    /// The consumer is always awaited before its cancellation source is disposed, so no
    /// background task is ever orphaned and no task exception is left unobserved. This
    /// method does not throw; it returns every failure it collected.
    /// </summary>
    private async Task<IReadOnlyList<PipelineFailure>> ReleasePipelineAsync()
    {
        var failures = new List<PipelineFailure>(3);
        var consumer = _consumer;
        var queue = _queue;

        try
        {
            try
            {
                await _source.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new PipelineFailure(
                    "live_source_stop_failed",
                    "subscribe",
                    exception));
            }

            // Unconditional: the writer must be completed even if stopping the source
            // failed, otherwise the consumer would never observe the end of the stream.
            try
            {
                queue?.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                failures.Add(new PipelineFailure(
                    "live_queue_complete_failed",
                    "queue",
                    exception));
            }

            // Unconditional: awaiting the consumer both drains the queue and observes
            // any exception the consumer produced.
            if (consumer is not null)
            {
                try
                {
                    await consumer.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    failures.Add(new PipelineFailure(
                        "live_consumer_failed",
                        "parse",
                        exception));
                }
            }
        }
        finally
        {
            // Only now, with the consumer finished, is it safe to dispose the token
            // source it was using.
            _consumerCts?.Dispose();
            _consumerCts = null;
            _consumer = null;
            _queue = null;
            _subscription = null;
        }

        return failures;
    }

    /// <summary>
    /// The single background consumer. Each record is parsed exactly once; that one parse
    /// feeds both the counters and the persisted evidence row. Records accumulate in a
    /// bounded batch that is flushed when it fills, when its fixed age deadline expires,
    /// or unconditionally when the stream ends.
    /// </summary>
    /// <remarks>
    /// An abrupt process termination can still lose up to
    /// <see cref="LiveMonitoringLimits.MaxCaptureBatchRecords"/> - 1 classified records
    /// that have not committed yet. The deadline bounds normal partial-batch residency;
    /// it is not a durability guarantee.
    /// </remarks>
    private async Task ConsumeAsync(
        ChannelReader<LiveQueuedRecord> reader,
        CancellationToken cancellationToken)
    {
        var batch = new List<LiveCaptureRecord>(
            LiveMonitoringLimits.MaxCaptureBatchRecords);
        Task<bool>? readinessTask = null;
        Task? deadlineTask = null;
        CancellationTokenSource? deadlineCancellation = null;

        async Task EndDeadlineAsync(bool cancel)
        {
            if (deadlineTask is null || deadlineCancellation is null)
            {
                return;
            }

            var task = deadlineTask;
            var cancellation = deadlineCancellation;
            deadlineTask = null;
            deadlineCancellation = null;
            try
            {
                if (cancel)
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                }

                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Expected when a full batch or channel completion retires its deadline.
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        try
        {
            while (true)
            {
                readinessTask ??= reader
                    .WaitToReadAsync(cancellationToken)
                    .AsTask();

                if (batch.Count == 0)
                {
                    if (!await readinessTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    readinessTask = null;
                }
                else
                {
                    var completed = await Task
                        .WhenAny(deadlineTask!, readinessTask)
                        .ConfigureAwait(false);

                    // If both became ready together, the fixed deadline wins. The
                    // channel readiness task is retained and observed on the next loop.
                    if (deadlineTask!.IsCompleted || ReferenceEquals(completed, deadlineTask))
                    {
                        await EndDeadlineAsync(cancel: false).ConfigureAwait(false);
                        await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!await readinessTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    readinessTask = null;
                }

                if (!reader.TryRead(out var queued))
                {
                    continue;
                }

                Interlocked.Decrement(ref _queueDepth);

                // Two failure domains, deliberately kept apart. Classifying one record can
                // fail on its own and must not stop the session; persisting a batch is a
                // storage fault and must fault the session. Sharing one catch would let a
                // storage failure be filed as a parse error and silently lose the batch.
                ClassifiedCapture processed;
                try
                {
                    processed = Classify(queued);
                }
                catch (Exception exception)
                {
                    // Defensive net only: Classify already counts every record exactly
                    // once and is not expected to throw. Counting again here would break
                    // the balance invariant, so this records the anomaly and moves on.
                    AddDiagnostic(
                        "live_event_processing_failed",
                        exception.Message,
                        ImportDiagnosticSeverity.Error,
                        "parse");
                    continue;
                }

                if (!PersistenceFaulted())
                {
                    var wasEmpty = batch.Count == 0;
                    batch.Add(processed.Record);
                    if (wasEmpty)
                    {
                        deadlineCancellation = new CancellationTokenSource();
                        deadlineTask = Task.Delay(
                            LiveMonitoringLimits.CaptureFlushInterval,
                            _timeProvider,
                            deadlineCancellation.Token);
                    }
                }

                // The observer is notified only after the record has entered the batch.
                // Tests can therefore use the notification as a deterministic boundary.
                NotifyClassified(processed.Classification);

                if (batch.Count >= LiveMonitoringLimits.MaxCaptureBatchRecords)
                {
                    await EndDeadlineAsync(cancel: true).ConfigureAwait(false);
                    await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Channel completion, Stop, source fault, cancellation and Dispose all retire
            // the timer immediately; none waits out the remaining interval.
            await EndDeadlineAsync(cancel: true).ConfigureAwait(false);

            // Unconditional final flush: the stream has ended, so whatever is still
            // pending is everything this session has left to record.
            await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends one batch and clears it. A failure faults the session and switches the
    /// consumer to classify-only: counts stay balanced and honest, but nothing further is
    /// claimed to be stored.
    /// </summary>
    private async Task FlushAsync(
        List<LiveCaptureRecord> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        if (PersistenceFaulted())
        {
            // Already faulted: drop the buffer rather than pretend it was written.
            batch.Clear();
            return;
        }

        // The buffer is kept intact until the append has actually succeeded, so a failure
        // can never make records disappear before the fault is recorded.
        var pending = batch.ToArray();
        try
        {
            await _repository
                .AppendRecordsAsync(pending, cancellationToken)
                .ConfigureAwait(false);
            batch.Clear();
            lock (_sessionLock)
            {
                _persistedRecordCount += pending.Length;
            }
        }
        catch (Exception exception)
        {
            // Anything thrown by AppendRecordsAsync is a persistence fault, whatever its
            // type. Matching on a fixed allow-list let TimeoutException,
            // ObjectDisposedException and OperationCanceledException escape and be filed
            // as parse errors, silently dropping the batch — never again.
            batch.Clear();
            OnPersistenceFault(DescribeAppendFailure(exception, cancellationToken));
        }
    }

    /// <summary>
    /// Describes an append failure for the diagnostic record. A genuinely requested
    /// cancellation is named as such; everything else keeps its real root cause. The
    /// message shown to the user is truncated by the caller.
    /// </summary>
    private static string DescribeAppendFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? $"The live evidence append was cancelled: {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";

    private bool PersistenceFaulted()
    {
        lock (_sessionLock)
        {
            return _persistenceFaulted;
        }
    }

    /// <summary>
    /// Classifies one record from a single parse of its XML. Guarantees exactly one count
    /// on every path and never throws, so a single bad record can never stop the consumer
    /// or unbalance the counters. The parse result is returned as a persistable row so
    /// the XML is never parsed twice. Observer notifications stay outside the counting
    /// scope.
    /// </summary>
    private ClassifiedCapture Classify(LiveQueuedRecord queued)
    {
        var record = queued.Record;
        LiveEventOutcome outcome;
        string? detail = null;
        string? errorCode = null;
        RawWindowsEvent? rawEvent = null;
        try
        {
            var result = _parser.Parse(record.RawXml);
            rawEvent = result.RawEvent;
            if (result.Error is not null)
            {
                errorCode = $"parse_{result.Error.Code.ToString().ToLowerInvariant()}";
                if (result.Error.Code == ParseErrorCode.UnsupportedEvent)
                {
                    outcome = LiveEventOutcome.Ignored;
                    detail = result.Error.Message;
                    Count(counters => counters with { Ignored = counters.Ignored + 1 });
                }
                else
                {
                    outcome = LiveEventOutcome.Error;
                    detail = result.Error.Message;
                    Count(counters => counters with { Error = counters.Error + 1 });
                    AddDiagnostic(
                        $"live_parse_{result.Error.Code.ToString().ToLowerInvariant()}",
                        result.Error.Message,
                        ImportDiagnosticSeverity.Error,
                        "parse");
                }
            }
            else if (!MatchesSubscribedSource(
                _subscription,
                record,
                result.RawEvent,
                out var mismatch))
            {
                // The XML disagrees with the channel it arrived on. Fail closed: no
                // classification is produced and the record is counted as an error.
                outcome = LiveEventOutcome.Error;
                detail = mismatch;
                errorCode = "event_source_mismatch";
                Count(counters => counters with { Error = counters.Error + 1 });
                AddDiagnostic(
                    "live_event_source_mismatch",
                    mismatch,
                    ImportDiagnosticSeverity.Error,
                    "parse");
            }
            else if (result.DeleteEvent is not null)
            {
                outcome = LiveEventOutcome.DeleteFact;
                Count(counters => counters with { DeleteFact = counters.DeleteFact + 1 });
            }
            else if (result.ProcessContext is not null)
            {
                // Sysmon 1 is enrichment only; it never establishes a delete fact.
                outcome = LiveEventOutcome.ProcessContext;
                Count(counters => counters with
                {
                    ProcessContext = counters.ProcessContext + 1
                });
            }
            else if (result.SecurityEvidence is not null)
            {
                outcome = LiveEventOutcome.SecurityEvidence;
                Count(counters => counters with
                {
                    SecurityEvidence = counters.SecurityEvidence + 1
                });
            }
            else
            {
                // Parsed cleanly but establishes nothing, e.g. a 4663 without
                // DELETE / DELETE_CHILD access.
                outcome = LiveEventOutcome.Ignored;
                Count(counters => counters with { Ignored = counters.Ignored + 1 });
            }
        }
        catch (Exception exception)
        {
            outcome = LiveEventOutcome.Error;
            detail = exception.Message;
            errorCode = "event_processing_failed";
            Count(counters => counters with { Error = counters.Error + 1 });
            AddDiagnostic(
                "live_event_processing_failed",
                exception.Message,
                ImportDiagnosticSeverity.Error,
                "parse");
        }

        var classification = new LiveEventClassification(
            record,
            outcome,
            outcome == LiveEventOutcome.DeleteFact,
            detail is null ? null : LiveMonitoringLimits.TruncateMessage(detail));

        var capture = new LiveCaptureRecord(
            LiveEvidenceIdentity.Create(_liveSessionId!, queued.ReceivedSequence),
            _liveSessionId!,
            queued.ReceivedSequence,
            // Only what the channel or the parser actually reported; nothing is inferred.
            record.RecordId ?? rawEvent?.EventRecordId,
            record.ProviderName ?? rawEvent?.ProviderName,
            record.ChannelName,
            record.MachineName ?? rawEvent?.ComputerName,
            record.TimeCreatedUtc ?? rawEvent?.EventTimeUtc,
            _timeProvider.GetUtcNow(),
            record.RawXml,
            SHA256.HashData(Encoding.UTF8.GetBytes(record.RawXml)),
            rawEvent?.RawEventId,
            rawEvent?.EventId,
            outcome,
            LiveMonitoringLimits.TruncateErrorCode(errorCode),
            LiveMonitoringLimits.TruncateDetail(detail));
        return new ClassifiedCapture(capture, classification);
    }

    /// <summary>
    /// Appending evidence failed. The session stops accepting and moves to Error through
    /// the same single-shutdown path a source fault uses; it is never retried in a loop
    /// and never quietly downgraded to counting only.
    /// </summary>
    private void OnPersistenceFault(string message)
    {
        lock (_sessionLock)
        {
            if (_persistenceFaulted)
            {
                return;
            }

            _persistenceFaulted = true;
            _acceptingEvents = false;
            MarkFaultedCore($"实时证据未能写入数据库：{message}");
            AddDiagnosticCore(
                "live_evidence_persist_failed",
                message,
                ImportDiagnosticSeverity.Error,
                "persist");

            // Teardown runs off this thread: the consumer must not await its own
            // shutdown, and the watcher must not be disposed from a callback.
            _faultShutdown ??= Task.Run(HandleFaultAsync, CancellationToken.None);
        }

        SetState(LiveMonitoringState.Error);
    }

    /// <summary>
    /// Publishes a classification to observers. Each subscriber is invoked separately so
    /// a throwing one cannot suppress the subscribers registered after it, and no
    /// observer failure changes a count — the balance invariant survives it.
    /// </summary>
    private void NotifyClassified(LiveEventClassification classification)
    {
        var handlers = EventClassified;
        if (handlers is not null)
        {
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<LiveEventClassification>)handler)(this, classification);
                }
                catch (Exception exception)
                {
                    AddDiagnostic(
                        "live_classification_observer_failed",
                        exception.Message,
                        ImportDiagnosticSeverity.Warning,
                        "parse");
                }
            }
        }

        RaiseSnapshotChanged();
    }

    /// <summary>
    /// Cross-checks the channel the record arrived on against the provider and event ID
    /// the XML itself claims. A forged EventID inside the XML cannot smuggle an event
    /// past the subscription's constraints. Fails closed on missing inputs.
    /// </summary>
    internal static bool MatchesSubscribedSource(
        LiveEventSubscription? subscription,
        LiveEventRecord record,
        RawWindowsEvent? rawEvent,
        out string mismatch)
    {
        ArgumentNullException.ThrowIfNull(record);
        mismatch = string.Empty;

        if (subscription is null)
        {
            mismatch =
                "No active subscription is available to validate the record's origin.";
            return false;
        }

        if (rawEvent is null)
        {
            mismatch = "The parsed event carries no identifiable origin.";
            return false;
        }

        var channel = subscription.Find(record.ChannelName);
        if (channel is null)
        {
            mismatch =
                $"Record arrived on unsubscribed channel '{record.ChannelName}'.";
            return false;
        }

        if (!channel.Accepts(rawEvent.ProviderName, rawEvent.EventId))
        {
            mismatch =
                $"Channel '{channel.ChannelName}' does not accept provider "
                + $"'{rawEvent.ProviderName ?? "(none)"}' with event ID {rawEvent.EventId}.";
            return false;
        }

        if (rawEvent.ChannelName is not null
            && !string.Equals(
                rawEvent.ChannelName,
                channel.ChannelName,
                StringComparison.OrdinalIgnoreCase))
        {
            mismatch =
                $"XML channel '{rawEvent.ChannelName}' does not match delivery channel "
                + $"'{channel.ChannelName}'.";
            return false;
        }

        if (record.ProviderName is not null
            && !string.Equals(
                record.ProviderName,
                channel.ExpectedProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            mismatch =
                $"Record provider '{record.ProviderName}' does not match the expected "
                + $"provider for '{channel.ChannelName}'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs on the event delivery thread; must never block. Accepting a record and
    /// enqueuing it stay atomic under the session lock so that Received and the
    /// classification buckets can never disagree; see CreateQueueOptions for why that is
    /// safe with respect to consumer continuations.
    /// </summary>
    private void OnRecordReceived(int generation, LiveEventRecord record)
    {
        var length = record.RawXml.Length;
        var oversized = length > LiveMonitoringLimits.MaxEventXmlCharacters;

        lock (_sessionLock)
        {
            if (generation != _generation)
            {
                // A callback from a watcher that belonged to an earlier session. It is not
                // this session's event, so it must not touch a single one of this
                // session's counters — counting it here would attribute another session's
                // record to whichever session happens to be running now, and that number
                // is persisted. It consumes no sequence and is never stored.
                return;
            }

            if (!_acceptingEvents)
            {
                // This session's own event, arriving after it stopped accepting. It is
                // genuinely late for this session, so LateDiscarded is the right counter.
                // It stays outside the balance equation, consumes no sequence and is
                // never stored.
                _counters = _counters with
                {
                    LateDiscarded = _counters.LateDiscarded + 1
                };
                return;
            }

            _counters = _counters with { Received = _counters.Received + 1 };

            // Assigned before the queue decision and exactly once per accepted callback,
            // so an oversized or dropped record still consumes a sequence and the gap it
            // leaves is itself evidence that something was received but not stored.
            var sequence = ++_receivedSequence;

            if (oversized)
            {
                _counters = _counters with { Error = _counters.Error + 1 };
                AddDiagnosticCore(
                    "live_event_xml_too_large",
                    $"An event's XML is {length} UTF-16 code units, above the "
                    + $"{LiveMonitoringLimits.MaxEventXmlCharacters} limit; it was not queued or parsed.",
                    ImportDiagnosticSeverity.Error,
                    "queue");
                return;
            }

            // Accepting is still true here, so the writer is still open: a false result
            // means the bounded queue is genuinely full.
            if (_queue is not null
                && _queue.Writer.TryWrite(new LiveQueuedRecord(sequence, record)))
            {
                Interlocked.Increment(ref _queueDepth);
            }
            else
            {
                _counters = _counters with { Dropped = _counters.Dropped + 1 };
                if (!_queueOverflowReported)
                {
                    _queueOverflowReported = true;
                    _lastError =
                        $"事件队列已满（容量 {_options.QueueCapacity}），部分事件已被丢弃。";
                    AddDiagnosticCore(
                        "live_queue_overflow",
                        $"The bounded queue reached its capacity of {_options.QueueCapacity}; records are being dropped.",
                        ImportDiagnosticSeverity.Warning,
                        "queue");
                }
            }
        }

        RaiseSnapshotChanged();
    }

    /// <summary>
    /// A source fault stops the session for good. Everything that decides the persisted
    /// outcome happens inside the session lock; the teardown runs off the delivery thread
    /// so the watcher never disposes itself from its own callback, and the task is
    /// tracked so its exceptions are observed by Stop/Dispose.
    /// </summary>
    private void OnSourceFault(int generation, string code, string message)
    {
        lock (_sessionLock)
        {
            if (generation != _generation)
            {
                // A stale watcher: it cannot mark a newer session as faulted.
                return;
            }

            if (_faultShutdown is not null)
            {
                // A second channel faulting: record it, but the session is already
                // being completed exactly once.
                AddDiagnosticCore(code, message, ImportDiagnosticSeverity.Error, "receive");
                return;
            }

            _acceptingEvents = false;
            MarkFaultedCore(message);
            AddDiagnosticCore(code, message, ImportDiagnosticSeverity.Error, "receive");
            _faultShutdown = Task.Run(HandleFaultAsync, CancellationToken.None);
        }

        // UI only; the persisted final state never reads this field.
        SetState(LiveMonitoringState.Error);
    }

    private async Task HandleFaultAsync()
    {
        await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_completionStarted)
            {
                return;
            }

            await CompleteSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task ObserveFaultShutdownAsync()
    {
        Task? faultShutdown;
        lock (_sessionLock)
        {
            faultShutdown = _faultShutdown;
        }

        if (faultShutdown is null)
        {
            return;
        }

        try
        {
            await faultShutdown.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
        }
    }

    private async Task PersistSessionAsync(
        LiveMonitoringState finalState,
        Counters counters,
        long persistedRecordCount,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (_liveSessionId is null)
        {
            return;
        }

        if (!_captureStarted)
        {
            // The start row never reached the database, so there is nothing a completion
            // could belong to. Say so instead of writing an orphaned summary.
            AddDiagnostic(
                "live_capture_session_not_recorded",
                "The capture session start was never persisted; no completion or summary was written.",
                ImportDiagnosticSeverity.Error,
                "persist");
            return;
        }

        var value = counters.ToDomain();
        if (!value.IsBalanced)
        {
            // Never lose a session silently: surface the inconsistency instead.
            var message =
                $"监控会话计数不一致（接收 {value.Received}，已分类 {value.Parsed}，"
                + $"忽略 {value.Ignored}，错误 {value.Error}，丢弃 {value.Dropped}），未能保存会话摘要。";
            MarkFaulted(message);
            AddDiagnostic(
                "live_session_counters_unbalanced",
                message,
                ImportDiagnosticSeverity.Error,
                "persist");
            return;
        }

        var stoppedUtc = _timeProvider.GetUtcNow();
        var session = new LiveMonitoringSession(
            _liveSessionId,
            _startedUtc,
            stoppedUtc,
            _channelStatuses,
            value,
            finalState,
            _options.QueueCapacity,
            _options.ApplicationVersion);
        var completion = new LiveCaptureCompletion(
            _liveSessionId,
            stoppedUtc,
            finalState,
            value,
            persistedRecordCount);

        try
        {
            // One attempt only, and one transaction: the evidence completion and the
            // Phase 2A summary commit together or not at all, so they can never disagree
            // about how this session ended.
            await _repository
                .CompleteSessionAsync(completion, session, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            _persisted = true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            // The completion and the summary rolled back together, so nothing was stored.
            // Mark the session faulted so the published state cannot claim a clean stop.
            MarkFaulted(
                $"监控会话未能写入数据库：{LiveMonitoringLimits.TruncateMessage(exception.Message)}");
            AddDiagnostic(
                "live_session_persist_failed",
                exception.Message,
                ImportDiagnosticSeverity.Error,
                "persist");
        }
    }

    private void BeginSession()
    {
        lock (_sessionLock)
        {
            _generation++;
            _counters = Counters.Empty;
            _diagnostics.Clear();
            _queueOverflowReported = false;
            _acceptingEvents = false;
            _sessionFaulted = false;
            _lastError = null;
            _faultShutdown = null;
            _receivedSequence = 0;
            _persistedRecordCount = 0;
            _persistenceFaulted = false;
        }

        _completionStarted = false;
        _lifecycleCompleted = false;
        _persisted = false;
        _captureStarted = false;
        _liveSessionId = Guid.NewGuid().ToString("D");
        _startedUtc = _timeProvider.GetUtcNow();
        Interlocked.Exchange(ref _queueDepth, 0);
    }

    private bool SessionFaulted()
    {
        lock (_sessionLock)
        {
            return _sessionFaulted;
        }
    }

    private void MarkFaulted(string message)
    {
        lock (_sessionLock)
        {
            MarkFaultedCore(message);
        }
    }

    /// <summary>
    /// Records the first causal session fault. Later failures still receive their own
    /// diagnostics, but cannot replace the more useful root cause already shown to the
    /// user. Must be called with <see cref="_sessionLock"/> held.
    /// </summary>
    private void MarkFaultedCore(string message)
    {
        var preserveFirstCause =
            _sessionFaulted && !string.IsNullOrWhiteSpace(_lastError);
        _sessionFaulted = true;
        if (!preserveFirstCause)
        {
            _lastError = LiveMonitoringLimits.TruncateMessage(message);
        }
    }

    private void Count(Func<Counters, Counters> update)
    {
        lock (_sessionLock)
        {
            _counters = update(_counters);
        }
    }

    private void AddDiagnostic(
        string code,
        string message,
        ImportDiagnosticSeverity severity,
        string stage)
    {
        lock (_sessionLock)
        {
            AddDiagnosticCore(code, message, severity, stage);
        }
    }

    /// <summary>
    /// Retains the first <see cref="LiveMonitoringLimits.MaxDiagnostics"/> real
    /// diagnostics untouched. Beyond that, nothing is added and nothing already retained
    /// is overwritten; the surplus is only counted. Must be called with
    /// <see cref="_sessionLock"/> held.
    /// </summary>
    private void AddDiagnosticCore(
        string code,
        string message,
        ImportDiagnosticSeverity severity,
        string stage)
    {
        if (_diagnostics.Count >= LiveMonitoringLimits.MaxDiagnostics)
        {
            _counters = _counters with
            {
                SuppressedDiagnostics = _counters.SuppressedDiagnostics + 1
            };
            return;
        }

        _diagnostics.Add(new LiveMonitoringDiagnostic(
            code,
            LiveMonitoringLimits.TruncateMessage(message),
            severity,
            stage,
            _timeProvider.GetUtcNow()));
    }

    private void SetState(LiveMonitoringState state)
    {
        _state = state;
        RaiseSnapshotChanged();
    }

    private LiveMonitoringSnapshot CreateSnapshot()
    {
        Counters counters;
        string? lastError;
        lock (_sessionLock)
        {
            counters = _counters;
            lastError = _lastError;
        }

        return new LiveMonitoringSnapshot(
            _state,
            _channelStatuses,
            counters.ToDomain(),
            _options.QueueCapacity,
            Volatile.Read(ref _queueDepth),
            lastError,
            _liveSessionId);
    }

    /// <summary>
    /// Publishes a snapshot to observers, each isolated from the others. This also runs
    /// on the watcher delivery thread, so a throwing subscriber must never be allowed to
    /// escape into the event log callback.
    /// </summary>
    private void RaiseSnapshotChanged()
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        var snapshot = CreateSnapshot();
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<LiveMonitoringSnapshot>)handler)(this, snapshot);
            }
            catch (Exception exception)
            {
                AddDiagnostic(
                    "live_snapshot_observer_failed",
                    exception.Message,
                    ImportDiagnosticSeverity.Warning,
                    "receive");
            }
        }
    }

    private static string ToDiagnosticSuffix(LiveChannelAvailability availability) =>
        availability switch
        {
            LiveChannelAvailability.Unavailable => "unavailable",
            LiveChannelAvailability.AccessDenied => "access_denied",
            LiveChannelAvailability.Disabled => "disabled",
            _ => "unknown_error"
        };

    private static bool IsExpectedFailure(Exception exception) =>
        exception is InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or System.Data.Common.DbException;

    /// <summary>One named shutdown-step failure; failures never overwrite each other.</summary>
    private readonly record struct PipelineFailure(
        string Code,
        string Stage,
        Exception Exception);

    /// <summary>
    /// A queued record and the receive position it was assigned on the delivery thread.
    /// Carrying the sequence through the queue is what lets the consumer write evidence
    /// with a stable identity without ever calling back into the callback thread.
    /// </summary>
    private readonly record struct LiveQueuedRecord(
        long ReceivedSequence,
        LiveEventRecord Record);

    private readonly record struct ClassifiedCapture(
        LiveCaptureRecord Record,
        LiveEventClassification Classification);

    /// <summary>Immutable count set; only ever replaced under <see cref="_sessionLock"/>.</summary>
    private readonly record struct Counters(
        long Received,
        long DeleteFact,
        long ProcessContext,
        long SecurityEvidence,
        long Ignored,
        long Error,
        long Dropped,
        long LateDiscarded,
        long SuppressedDiagnostics)
    {
        public static Counters Empty { get; }

        public LiveMonitoringCounters ToDomain() =>
            new(
                Received,
                DeleteFact,
                ProcessContext,
                SecurityEvidence,
                Ignored,
                Error,
                Dropped,
                LateDiscarded,
                SuppressedDiagnostics);
    }

    private sealed class Sink(LiveMonitoringService owner, int generation) : ILiveEventSink
    {
        public void Publish(LiveEventRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            owner.OnRecordReceived(generation, record);
        }

        public void Report(LiveMonitoringDiagnostic diagnostic)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);
            lock (owner._sessionLock)
            {
                if (generation != owner._generation)
                {
                    return;
                }

                owner.AddDiagnosticCore(
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Severity,
                    diagnostic.Stage);
            }
        }

        public void Fault(string code, string message) =>
            owner.OnSourceFault(generation, code, message);
    }
}
