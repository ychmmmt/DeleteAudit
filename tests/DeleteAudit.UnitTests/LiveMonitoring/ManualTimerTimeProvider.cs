namespace DeleteAudit.UnitTests.LiveMonitoring;

/// <summary>
/// A manually advanced clock whose one-shot timers run asynchronously and in a
/// deterministic due-time/registration order. It is test-only and never consults the
/// system clock.
/// </summary>
internal sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly Queue<Action> _callbacks = [];
    private DateTimeOffset _utcNow;
    private long _nextRegistrationOrder;
    private bool _dispatchScheduled;
    private int _createdTimerCount;

    /// <summary>
    /// Failures raised by callbacks of the advancement currently being drained. The
    /// marker enqueued behind those callbacks consumes and clears this, so a failure is
    /// reported to exactly the <see cref="AdvanceAsync"/> that caused it.
    /// </summary>
    private List<Exception>? _pendingFailures;

    internal Action? BeforeTimerCallback { get; set; }

    internal Action? BeforeTimerDisposeWait { get; set; }

    public ManualTimerTimeProvider(DateTimeOffset start)
    {
        _utcNow = start;
    }

    public int CreatedTimerCount
    {
        get
        {
            lock (_sync)
            {
                return _createdTimerCount;
            }
        }
    }

    public int ActiveTimerCount
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(timer => timer.IsScheduled);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateOneShotPeriod(period);
        ValidateDueTime(dueTime);

        lock (_sync)
        {
            var timer = new ManualTimer(
                this,
                callback,
                state,
                ++_nextRegistrationOrder);
            _timers.Add(timer);
            _createdTimerCount++;
            timer.ChangeCore(dueTime);
            return timer;
        }
    }

    public Task AdvanceAsync(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _utcNow += amount;
            var due = _timers
                .Where(timer => timer.IsDue(_utcNow))
                .OrderBy(timer => timer.DueUtc)
                .ThenBy(timer => timer.RegistrationOrder)
                .ToArray();
            foreach (var timer in due)
            {
                var generation = timer.TakeDueGeneration();
                _callbacks.Enqueue(() => timer.InvokeIfCurrent(generation));
            }

            // The marker is behind every callback caused by this advancement, so it
            // observes exactly their failures.
            _callbacks.Enqueue(() => CompleteAdvance(completed));
            EnsureDispatcherRunning();
        }

        return completed.Task;
    }

    /// <summary>
    /// Starts the single dispatcher if it is not already running. Claiming the flag and
    /// queueing the work item happen together under <c>_sync</c>, so exactly one drainer
    /// can exist at a time; if queueing fails the claim is released again rather than
    /// left latched, which would strand every later advancement. Must be called with
    /// <c>_sync</c> held.
    /// </summary>
    private void EnsureDispatcherRunning()
    {
        if (_dispatchScheduled)
        {
            return;
        }

        _dispatchScheduled = true;
        try
        {
            if (!ThreadPool.UnsafeQueueUserWorkItem(
                    static provider => provider.DrainCallbacks(),
                    this,
                    preferLocal: false))
            {
                _dispatchScheduled = false;
                throw new InvalidOperationException(
                    "The manual test clock could not queue its dispatcher.");
            }
        }
        catch
        {
            _dispatchScheduled = false;
            throw;
        }
    }

    private static void ValidateDueTime(TimeSpan dueTime)
    {
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        }
    }

    private static void ValidateOneShotPeriod(TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException(
                "The manual test clock supports one-shot timers only.");
        }
    }

    /// <summary>
    /// Reports one advancement. A callback that threw is surfaced through the advancement
    /// it belonged to — never as an unhandled thread-pool exception — and the failure
    /// state is cleared so the next advancement starts clean. When several callbacks of
    /// one advancement fail, all of them are reported together.
    /// </summary>
    private void CompleteAdvance(TaskCompletionSource completed)
    {
        List<Exception>? failures;
        lock (_sync)
        {
            failures = _pendingFailures;
            _pendingFailures = null;
        }

        if (failures is null || failures.Count == 0)
        {
            completed.TrySetResult();
            return;
        }

        completed.TrySetException(
            failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    /// <summary>
    /// The single dispatcher. Every remaining due callback of an advancement still runs
    /// after one of them fails; the failures travel to that advancement's marker.
    /// </summary>
    /// <remarks>
    /// There is exactly one owner of <c>_dispatchScheduled</c> at any time: it is claimed
    /// under <c>_sync</c> by <see cref="EnsureDispatcherRunning"/> and released under the
    /// same lock at the one exit below, in the same critical section that observes an
    /// empty queue. No second-guessing after the fact, so a concurrent
    /// <see cref="AdvanceAsync"/> can never cause a second drainer to be queued.
    /// Everything a work item can throw is caught in the loop, so the loop cannot unwind
    /// and no exception reaches the thread pool.
    /// </remarks>
    private void DrainCallbacks()
    {
        while (true)
        {
            Action callback;
            lock (_sync)
            {
                if (_callbacks.Count == 0)
                {
                    _dispatchScheduled = false;
                    return;
                }

                callback = _callbacks.Dequeue();
            }

            try
            {
                callback();
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    (_pendingFailures ??= []).Add(exception);
                }
            }
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimerTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;
        private bool _callbackRunning;
        private int _callbackThreadId;
        private long _generation;

        public ManualTimer(
            ManualTimerTimeProvider owner,
            TimerCallback callback,
            object? state,
            long registrationOrder)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            RegistrationOrder = registrationOrder;
        }

        public long RegistrationOrder { get; }

        public DateTimeOffset? DueUtc { get; private set; }

        public bool IsScheduled => !_disposed && DueUtc is not null;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateOneShotPeriod(period);
            ValidateDueTime(dueTime);
            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return false;
                }

                ChangeCore(dueTime);
                return true;
            }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _generation++;
                    DueUtc = null;
                    _owner._timers.Remove(this);
                }

                // Once Dispose returns, no callback may start afterwards. If another
                // thread already committed to the callback, wait for it to finish.
                // A callback may legally dispose its own timer, so that thread must
                // never wait on itself.
                if (_callbackRunning
                    && _callbackThreadId != Environment.CurrentManagedThreadId)
                {
                    _owner.BeforeTimerDisposeWait?.Invoke();
                }

                while (_callbackRunning
                       && _callbackThreadId != Environment.CurrentManagedThreadId)
                {
                    Monitor.Wait(_owner._sync);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeCore(TimeSpan dueTime)
        {
            _generation++;
            DueUtc = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : _owner._utcNow + dueTime;
        }

        public bool IsDue(DateTimeOffset now) =>
            IsScheduled && DueUtc <= now;

        public long TakeDueGeneration()
        {
            DueUtc = null;
            return _generation;
        }

        public void InvokeIfCurrent(long generation)
        {
            Action? beforeCallback;
            lock (_owner._sync)
            {
                if (_disposed || generation != _generation)
                {
                    return;
                }

                _callbackRunning = true;
                _callbackThreadId = Environment.CurrentManagedThreadId;
                beforeCallback = _owner.BeforeTimerCallback;
            }

            try
            {
                beforeCallback?.Invoke();
                _callback(_state);
            }
            finally
            {
                lock (_owner._sync)
                {
                    _callbackRunning = false;
                    _callbackThreadId = 0;
                    Monitor.PulseAll(_owner._sync);
                }
            }
        }
    }
}
