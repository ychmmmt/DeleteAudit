using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Application.Presentation;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.LiveMonitoring;

namespace DeleteAudit.UnitTests.LiveMonitoring;

/// <summary>
/// Every test drives the production service through injected fakes. Nothing here
/// reads, writes, or subscribes to a real Windows event log.
/// </summary>
public sealed class LiveMonitoringServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ---------- deterministic timer support ----------

    [Fact]
    public void CaptureFlushIntervalIsFixedAndPositive()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LiveMonitoringLimits.CaptureFlushInterval);
        Assert.True(LiveMonitoringLimits.CaptureFlushInterval > TimeSpan.Zero);
    }

    [Fact]
    public async Task ManualTimerDoesNotFireBeforeItsDueTimeAndFiresAtTheDeadline()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var callbackCount = 0;
        using var timer = time.CreateTimer(
            _ => Interlocked.Increment(ref callbackCount),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        await time.AdvanceAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(0, callbackCount);
        Assert.Equal(1, time.ActiveTimerCount);

        await time.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, callbackCount);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task DisposedManualTimerNeverFires()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var callbackCount = 0;
        var timer = time.CreateTimer(
            _ => Interlocked.Increment(ref callbackCount),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        timer.Dispose();
        await time.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, callbackCount);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task ConcurrentDisposeWaitsForACommittedCallbackBeforeReturning()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        using var callbackCommitted = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        using var disposeWaiting = new ManualResetEventSlim(false);
        var callbackCount = 0;
        var callbackWaitTimedOut = 0;
        time.BeforeTimerCallback = () =>
        {
            callbackCommitted.Set();
            if (!releaseCallback.Wait(Patience))
            {
                Interlocked.Exchange(ref callbackWaitTimedOut, 1);
            }
        };
        time.BeforeTimerDisposeWait = disposeWaiting.Set;
        var timer = time.CreateTimer(
            _ => Interlocked.Increment(ref callbackCount),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        var advancing = time.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(callbackCommitted.Wait(Patience));
        var disposing = Task.Run(() =>
        {
            timer.Dispose();
        });
        Assert.True(disposeWaiting.Wait(Patience));
        Assert.False(disposing.IsCompleted);

        releaseCallback.Set();
        await Task.WhenAll(advancing, disposing);
        await time.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(1, callbackCount);
        Assert.Equal(0, callbackWaitTimedOut);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task CancellingProviderBackedDelayRemovesItsTimer()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();
        var delay = Task.Delay(
            TimeSpan.FromSeconds(5),
            time,
            cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
        await time.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.True(delay.IsCanceled);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task ManualTimersFireInDueTimeThenRegistrationOrder()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var order = new List<string>();
        using var laterFirst = time.CreateTimer(
            _ => order.Add("five-first"),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);
        using var earlier = time.CreateTimer(
            _ => order.Add("three"),
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);
        using var laterSecond = time.CreateTimer(
            _ => order.Add("five-second"),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        await time.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["three", "five-first", "five-second"], order);
    }

    [Fact]
    public async Task ManualTimerCallbackDoesNotReenterTheAdvancingThread()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var advancingThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;
        using var timer = time.CreateTimer(
            _ => callbackThread = Environment.CurrentManagedThreadId,
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        await time.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.NotEqual(0, callbackThread);
        Assert.NotEqual(advancingThread, callbackThread);
    }

    [Fact]
    public async Task ManualOneShotTimerFiresOnlyOnce()
    {
        var time = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var callbackCount = 0;
        using var timer = time.CreateTimer(
            _ => Interlocked.Increment(ref callbackCount),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        await time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await time.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, callbackCount);
    }

    // ---------- manual clock failure handling ----------

    [Fact]
    public async Task ThrowingTimerCallbackFaultsItsOwnAdvance()
    {
        var time = CaptureTime();
        var failure = new InvalidOperationException("fixture callback failure");
        using var timer = time.CreateTimer(
            _ => throw failure,
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        var advancing = time.AdvanceAsync(TimeSpan.FromSeconds(1));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => advancing.WaitAsync(Patience));

        // Faulted, not hung, and carrying the original failure.
        Assert.Same(failure, thrown);
        Assert.True(advancing.IsFaulted);
    }

    [Fact]
    public async Task ThrowingBeforeTimerCallbackHookIsObservable()
    {
        var time = CaptureTime();
        var failure = new InvalidOperationException("fixture hook failure");
        time.BeforeTimerCallback = () => throw failure;
        var fired = 0;
        using var timer = time.CreateTimer(
            _ => Interlocked.Increment(ref fired),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => time.AdvanceAsync(TimeSpan.FromSeconds(1)).WaitAsync(Patience));

        Assert.Same(failure, thrown);
        // The hook threw before the callback body, so the timer never ran.
        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task DispatcherRecoversAndLaterAdvancesAreUnaffected()
    {
        var time = CaptureTime();
        using (var failing = time.CreateTimer(
            _ => throw new InvalidOperationException("fixture callback failure"),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => time.AdvanceAsync(TimeSpan.FromSeconds(1)).WaitAsync(Patience));
        }

        // An advance with nothing due must still complete, proving the dispatcher was
        // released rather than latched on by the earlier failure.
        await time.AdvanceAsync(TimeSpan.FromSeconds(1)).WaitAsync(Patience);

        var fired = 0;
        using var healthy = time.CreateTimer(
            _ => Interlocked.Increment(ref fired),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        // A brand new timer still fires, and its advance does not inherit the old failure.
        await time.AdvanceAsync(TimeSpan.FromSeconds(1)).WaitAsync(Patience);

        Assert.Equal(1, fired);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task EveryDueCallbackRunsAndAllFailuresReachTheSameAdvance()
    {
        var time = CaptureTime();
        var firstFailure = new InvalidOperationException("fixture failure one");
        var secondFailure = new InvalidOperationException("fixture failure two");
        var survivorRan = 0;
        using var failingFirst = time.CreateTimer(
            _ => throw firstFailure,
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);
        using var survivor = time.CreateTimer(
            _ => Interlocked.Increment(ref survivorRan),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);
        using var failingLast = time.CreateTimer(
            _ => throw secondFailure,
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        var thrown = await Assert.ThrowsAsync<AggregateException>(
            () => time.AdvanceAsync(TimeSpan.FromSeconds(1)).WaitAsync(Patience));

        // One failure never cancels the rest of the advancement.
        Assert.Equal(1, survivorRan);
        Assert.Equal([firstFailure, secondFailure], thrown.InnerExceptions);
    }

    [Fact]
    public async Task QueuedAdvancesStillCompleteAfterAnEarlierCallbackFails()
    {
        var time = CaptureTime();
        var reachedCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        using var failing = time.CreateTimer(
            _ =>
            {
                reachedCallback.TrySetResult();
                release.Wait(Patience);
                throw new InvalidOperationException("fixture callback failure");
            },
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        var failingAdvance = time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await reachedCallback.Task.WaitAsync(Patience);

        // Queued behind the failing callback while the dispatcher is still busy.
        var queuedAdvance = time.AdvanceAsync(TimeSpan.FromSeconds(1));
        release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingAdvance.WaitAsync(Patience));
        // The later marker is neither lost nor poisoned by the earlier failure.
        await queuedAdvance.WaitAsync(Patience);

        Assert.True(queuedAdvance.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeRacingAThrowingCallbackNeitherDeadlocksNorLosesTheMarker()
    {
        var time = CaptureTime();
        var reachedCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        time.BeforeTimerDisposeWait = () => disposeWaiting.TrySetResult();
        var timer = time.CreateTimer(
            _ =>
            {
                reachedCallback.TrySetResult();
                release.Wait(Patience);
                throw new InvalidOperationException("fixture callback failure");
            },
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        var advancing = time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await reachedCallback.Task.WaitAsync(Patience);
        var disposing = Task.Run(timer.Dispose);
        await disposeWaiting.Task.WaitAsync(Patience);
        release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => advancing.WaitAsync(Patience));
        await disposing.WaitAsync(Patience);

        Assert.Equal(0, time.ActiveTimerCount);
        await time.AdvanceAsync(TimeSpan.FromSeconds(1)).WaitAsync(Patience);
    }

    // ---------- channel detection ----------

    [Fact]
    public async Task ExistingChannelIsReportedAvailable()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out _, out _);

        var statuses = await service.ProbeChannelsAsync();

        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, status => Assert.True(status.CanSubscribe));
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
    }

    [Fact]
    public async Task MissingSysmonChannelIsAStatusNotAProductError()
    {
        var probe = FakeProbe.With(
            Status(LiveMonitoringChannels.SysmonOperational, LiveChannelAvailability.Unavailable),
            Status(LiveMonitoringChannels.Security, LiveChannelAvailability.Available));
        await using var service = CreateService(probe, out var source, out _);
        using var viewModel = CreateViewModel(service);

        await service.ProbeChannelsAsync();

        Assert.Equal("未检测到 Sysmon", Head(viewModel.SysmonChannelStatus));
        Assert.False(viewModel.HasError);
        Assert.Equal(0, source.StartCount);
    }

    [Fact]
    public async Task AccessDeniedChannelIsSurfacedAndOtherChannelsStillSubscribe()
    {
        var probe = FakeProbe.With(
            Status(LiveMonitoringChannels.SysmonOperational, LiveChannelAvailability.Available),
            Status(
                LiveMonitoringChannels.Security,
                LiveChannelAvailability.AccessDenied,
                "当前用户无权读取 Security 通道。"));
        await using var service = CreateService(probe, out var source, out _);
        using var viewModel = CreateViewModel(service);

        await service.StartAsync();

        Assert.Contains("access_denied", viewModel.SecurityChannelStatus, StringComparison.Ordinal);
        Assert.Equal(LiveMonitoringState.Running, service.Snapshot.State);
        var channel = Assert.Single(source.LastSubscription!.Channels);
        Assert.Equal(LiveMonitoringChannels.SysmonOperational, channel.ChannelName);
        Assert.Equal(1, probe.ProbeCount);
    }

    [Fact]
    public async Task ProbeRunsOffTheCallingThread()
    {
        var probe = new ThreadRecordingProbe();
        await using var service = CreateService(probe, out _, out _);
        using var viewModel = CreateViewModel(service);
        var callerThreadId = Environment.CurrentManagedThreadId;

        await viewModel.ProbeChannelsAsync();

        Assert.NotEqual(0, probe.ProbeThreadId);
        Assert.NotEqual(callerThreadId, probe.ProbeThreadId);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task CancelledProbeDoesNotOverwriteCurrentChannelState()
    {
        using var gate = new ManualResetEventSlim(false);
        var probe = new BlockingProbe(gate, Patience);
        await using var service = CreateService(probe, out _, out _);
        using var viewModel = CreateViewModel(service);

        var probing = viewModel.ProbeChannelsAsync();
        viewModel.CancelProbe();
        gate.Set();
        await probing;

        Assert.Empty(service.Snapshot.ChannelStatuses);
        Assert.Equal("尚未检测", viewModel.SysmonChannelStatus);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasError);
    }

    // ---------- schema fail-closed ----------

    [Fact]
    public async Task MissingLiveSchemaFailsBeforeAnyWatcherIsCreated()
    {
        var repository = new FakeRepository
        {
            ValidateException = new InvalidOperationException(
                "missing live_monitoring_sessions")
        };
        var source = new FakeLiveEventSource();
        var probe = FakeProbe.AllAvailable();
        await using var service = new LiveMonitoringService(probe, source, repository);
        using var viewModel = CreateViewModel(service);

        await viewModel.StartCommand.ExecuteAsync();

        Assert.Equal(1, repository.ValidateCount);
        Assert.Equal(0, source.StartCount);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(0, probe.ProbeCount);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(
            LiveMonitoringService.SchemaNotReadyMessage,
            service.Snapshot.LastError);
        Assert.Equal(0, service.Snapshot.Counters.Received);
    }

    [Fact]
    public async Task SchemaIsValidatedBeforeProbing()
    {
        var probe = FakeProbe.AllAvailable();
        await using var service = CreateService(probe, out _, out var repository);

        await service.StartAsync();

        Assert.Equal(1, repository.ValidateCount);
        Assert.Equal(1, probe.ProbeCount);
    }

    // ---------- lifecycle ----------

    [Fact]
    public async Task MonitoringDoesNotStartUntilTheUserAsksForIt()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var viewModel = CreateViewModel(service);

        await viewModel.ProbeChannelsAsync();

        Assert.Equal(0, source.StartCount);
        Assert.False(source.IsRunning);
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.CanStop);
    }

    [Fact]
    public async Task BothChannelsAvailableCreatesExactlyTwoWatchers()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);

        await service.StartAsync();

        Assert.Equal(2, source.LiveWatcherCount);
        Assert.Equal(
            [LiveMonitoringChannels.SysmonOperational, LiveMonitoringChannels.Security],
            source.Watchers.Select(watcher => watcher.ChannelName));
    }

    [Fact]
    public async Task ProductionSourceCreatesOneWatcherPerAvailableChannel()
    {
        // Exercises the real WindowsEventLogWatcherSource lifecycle through its
        // subscription seam, without touching a Windows event log.
        var created = new List<string>();
        var disposed = new List<string>();
        await using var source = new WindowsEventLogWatcherSource(
            (channel, _) =>
            {
                created.Add(channel.ChannelName);
                return new TrackingSubscription(
                    () => disposed.Add(channel.ChannelName));
            });

        await source.StartAsync(
            LiveMonitoringChannels.CreateDefaultSubscription(),
            new NullSink());
        var afterStart = source.SubscriptionCount;
        await source.StartAsync(
            LiveMonitoringChannels.CreateDefaultSubscription(),
            new NullSink());
        var afterSecondStart = source.SubscriptionCount;
        await source.StopAsync();

        Assert.Equal(2, afterStart);
        Assert.Equal(2, afterSecondStart);
        Assert.Equal(
            [LiveMonitoringChannels.SysmonOperational, LiveMonitoringChannels.Security],
            created);
        Assert.Equal(
            [LiveMonitoringChannels.SysmonOperational, LiveMonitoringChannels.Security],
            disposed);
        Assert.Equal(0, source.SubscriptionCount);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task ProductionSourceDisposesAlreadyCreatedWatchersWhenOneFails()
    {
        var disposed = new List<string>();
        var index = 0;
        await using var source = new WindowsEventLogWatcherSource(
            (channel, _) =>
            {
                if (index++ == 1)
                {
                    throw new InvalidOperationException("fixture subscribe failure");
                }

                return new TrackingSubscription(
                    () => disposed.Add(channel.ChannelName));
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.StartAsync(
                LiveMonitoringChannels.CreateDefaultSubscription(),
                new NullSink()));

        Assert.Equal([LiveMonitoringChannels.SysmonOperational], disposed);
        Assert.Equal(0, source.SubscriptionCount);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task RepeatedStartDoesNotCreateASecondSubscription()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);

        await service.StartAsync();
        await service.StartAsync();
        await service.StartAsync();

        Assert.Equal(1, source.StartCount);
        Assert.Equal(2, source.LiveWatcherCount);
    }

    [Fact]
    public async Task StopReleasesEveryWatcher()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);

        await service.StartAsync();
        await service.StopAsync();

        Assert.Equal(1, source.StopCount);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.All(source.Watchers, watcher => Assert.True(watcher.IsDisposed));
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
    }

    [Fact]
    public async Task RepeatedStopIsSafeAndPersistsOneSession()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out _, out var repository);

        await service.StartAsync();
        await service.StopAsync();
        await service.StopAsync();
        await service.StopAsync();

        Assert.Equal(1, repository.SaveCount);
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
    }

    [Fact]
    public async Task ConcurrentStartAndStopSaveExactlyOncePerStartedSession()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index => index % 2 == 0
                ? service.StartAsync()
                : service.StopAsync()));
        await service.StopAsync();

        // Every session that actually started must be persisted exactly once — an
        // inequality would also pass with zero saves, which would hide a lost session.
        Assert.Equal(source.StartCount, repository.SaveCount);
        Assert.Equal(source.StartCount, repository.Sessions.Count);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
        Assert.True(service.Snapshot.Counters.IsBalanced);
        Assert.All(
            repository.Sessions,
            session => Assert.Equal(LiveMonitoringState.Stopped, session.FinalState));
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeStaySafe()
    {
        var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        var starting = service.StartAsync();
        var disposing = service.DisposeAsync().AsTask();
        try
        {
            await starting;
        }
        catch (ObjectDisposedException)
        {
            // Racing a Start against Dispose may observe the disposed source; that is a
            // visible, non-corrupting outcome.
        }

        await disposing;

        Assert.Equal(0, source.LiveWatcherCount);
        Assert.True(repository.SaveCount <= 1);
        Assert.All(
            repository.Sessions,
            session => Assert.True(session.Counters.IsBalanced));
    }

    [Fact]
    public async Task DisposeAfterStartReleasesEverythingAndSavesOnce()
    {
        var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await service.StartAsync();
        await service.DisposeAsync();

        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, repository.SaveCount);
    }

    // ---------- classification ----------

    [Fact]
    public async Task ClassificationCountsAreTrackedSeparately()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(2)));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonProcessCreate()));
        source.Watcher(1).Publish(LiveEventFixtures.SecurityRecord(
            LiveEventFixtures.Security4663("0x10000", "%%1537")));
        source.Watcher(1).Publish(LiveEventFixtures.SecurityRecord(
            LiveEventFixtures.Security4663("0x1", "%%4416")));
        observer.WaitForCount(5, Patience);

        var counters = service.Snapshot.Counters;
        Assert.Equal(2, counters.DeleteFact);
        Assert.Equal(1, counters.ProcessContext);
        Assert.Equal(1, counters.SecurityEvidence);
        Assert.Equal(1, counters.Ignored);
        Assert.Equal(0, counters.Error);
        Assert.True(counters.IsBalanced);
    }

    [Fact]
    public async Task ClassifiedCountEqualsSumOfTheThreeClassifications()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var observer = new ClassificationObserver(service);
        using var viewModel = CreateViewModel(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonProcessCreate()));
        source.Watcher(1).Publish(LiveEventFixtures.SecurityRecord(
            LiveEventFixtures.Security4663("0x40", "%%4424")));
        observer.WaitForCount(3, Patience);

        Assert.Equal(3, viewModel.ClassifiedCount);
        Assert.Equal(
            viewModel.DeleteFactCount
            + viewModel.ProcessContextCount
            + viewModel.SecurityEvidenceCount,
            viewModel.ClassifiedCount);
        Assert.Equal(1, viewModel.DeleteFactCount);
        Assert.Contains("删除事实（Sysmon 23/26）1", viewModel.ClassificationSummary, StringComparison.Ordinal);
        Assert.Contains("已分类事件 3", viewModel.CountersSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("已记录删除", viewModel.CountersSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SysmonProcessStartNeverEstablishesADeleteFact()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonProcessCreate()));
        observer.WaitForCount(1, Patience);

        var classification = Assert.Single(observer.Classifications);
        Assert.Equal(LiveEventOutcome.ProcessContext, classification.Outcome);
        Assert.False(classification.EstablishesDeleteFact);
        Assert.Equal(0, service.Snapshot.Counters.DeleteFact);
    }

    [Fact]
    public async Task OneMalformedRecordDoesNotStopTheSession()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord("<Event"));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));
        observer.WaitForCount(2, Patience);

        Assert.Equal(LiveMonitoringState.Running, service.Snapshot.State);
        Assert.Equal(1, service.Snapshot.Counters.Error);
        Assert.Equal(1, service.Snapshot.Counters.DeleteFact);
    }

    [Fact]
    public async Task ForgedEventIdOnTheWrongChannelFailsClosed()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        // A 4663 payload delivered on the Sysmon channel must not become evidence.
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.ForgedSecurityOnSysmonChannel()));
        observer.WaitForCount(1, Patience);

        var classification = Assert.Single(observer.Classifications);
        Assert.Equal(LiveEventOutcome.Error, classification.Outcome);
        Assert.False(classification.EstablishesDeleteFact);
        Assert.Equal(1, service.Snapshot.Counters.Error);
        Assert.Equal(0, service.Snapshot.Counters.SecurityEvidence);
        Assert.Equal(0, service.Snapshot.Counters.DeleteFact);
    }

    [Fact]
    public async Task RecordFromAnUnsubscribedChannelFailsClosed()
    {
        var probe = FakeProbe.With(
            Status(LiveMonitoringChannels.SysmonOperational, LiveChannelAvailability.Available),
            Status(LiveMonitoringChannels.Security, LiveChannelAvailability.AccessDenied));
        await using var service = CreateService(probe, out var source, out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SecurityRecord(
            LiveEventFixtures.Security4663("0x10000", "%%1537")));
        observer.WaitForCount(1, Patience);

        Assert.Equal(LiveEventOutcome.Error, Assert.Single(observer.Classifications).Outcome);
        Assert.Equal(0, service.Snapshot.Counters.SecurityEvidence);
    }

    // ---------- bounded memory ----------

    [Fact]
    public async Task OversizedXmlNeverEntersTheQueue()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.OversizedSysmonDelete()));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));
        // Only the second record reaches the consumer; the oversized one is rejected
        // before the queue, so exactly one classification ever happens.
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        var counters = service.Snapshot.Counters;
        Assert.Equal(2, counters.Received);
        Assert.Equal(1, counters.Error);
        Assert.Equal(1, counters.DeleteFact);
        Assert.Equal(0, counters.Dropped);
        Assert.True(counters.IsBalanced);
        Assert.Single(observer.Classifications);
    }

    [Fact]
    public async Task OversizedXmlDiagnosticDoesNotContainTheXml()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.OversizedSysmonDelete()));
        await service.StopAsync();

        var diagnostic = Assert.Single(
            repository.LastDiagnostics,
            item => item.Code == "live_event_xml_too_large");
        Assert.DoesNotContain("ppppp", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(
            diagnostic.Message.Length <= LiveMonitoringLimits.MaxDiagnosticMessageCharacters);
    }

    [Fact]
    public async Task FullQueueDropsRecordsAndKeepsMemoryBounded()
    {
        const int capacity = 1;
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out _,
            new LiveMonitoringOptions(capacity));
        using var blocked = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        service.EventClassified += (_, _) =>
        {
            entered.Set();
            blocked.Wait(Patience);
        };

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(1)));
        Assert.True(entered.Wait(Patience));

        for (var index = 2; index <= 5; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        var whileFull = service.Snapshot;
        blocked.Set();
        await service.StopAsync();

        Assert.Equal(5, whileFull.Counters.Received);
        Assert.Equal(3, whileFull.Counters.Dropped);
        Assert.True(whileFull.QueueDepth <= capacity);
        Assert.True(service.Snapshot.Counters.IsBalanced);
        Assert.Equal(2, service.Snapshot.Counters.DeleteFact);
    }

    [Fact]
    public async Task DiagnosticsAreCappedAndSurplusIsCounted()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);
        using var observer = new ClassificationObserver(service);
        const int malformed = LiveMonitoringLimits.MaxDiagnostics + 120;

        await service.StartAsync();
        for (var index = 0; index < malformed; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord("<Event"));
        }

        observer.WaitForCount(malformed, Patience);
        await service.StopAsync();

        Assert.Equal(LiveMonitoringLimits.MaxDiagnostics, repository.LastDiagnostics.Count);
        Assert.Equal(
            malformed - LiveMonitoringLimits.MaxDiagnostics,
            service.Snapshot.Counters.SuppressedDiagnostics);
        Assert.Equal(malformed, service.Snapshot.Counters.Error);
        // Every retained diagnostic is a real one; nothing was overwritten by a summary.
        Assert.All(
            repository.LastDiagnostics,
            item => Assert.Equal("live_parse_malformedxml", item.Code));
        Assert.DoesNotContain(
            repository.LastDiagnostics,
            item => item.Code == "diagnostics_suppressed");
    }

    [Fact]
    public async Task TheTwoHundredFiftySixthDiagnosticIsRetainedNotOverwritten()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await service.StartAsync();
        var watcher = source.Watcher(0);
        for (var index = 1; index <= LiveMonitoringLimits.MaxDiagnostics; index++)
        {
            watcher.Report(Diagnostic($"real_{index}"));
        }

        // The 257th must not enter the list and must not replace the 256th.
        watcher.Report(Diagnostic("overflow_257"));
        await service.StopAsync();

        var stored = repository.LastDiagnostics;
        Assert.Equal(LiveMonitoringLimits.MaxDiagnostics, stored.Count);
        Assert.Equal($"real_{LiveMonitoringLimits.MaxDiagnostics}", stored[^1].Code);
        Assert.DoesNotContain(stored, item => item.Code == "overflow_257");
        Assert.Equal(1, service.Snapshot.Counters.SuppressedDiagnostics);
    }

    [Fact]
    public async Task ConcurrentDiagnosticsNeverExceedTheCapAndCountAccurately()
    {
        const int writers = 8;
        const int perWriter = 100;
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);
        using var release = new ManualResetEventSlim(false);

        await service.StartAsync();
        var watcher = source.Watcher(0);
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            release.Wait(Patience);
            for (var index = 0; index < perWriter; index++)
            {
                watcher.Report(Diagnostic($"w{writer}_{index}"));
            }
        })).ToArray();

        release.Set();
        await Task.WhenAll(tasks);
        await service.StopAsync();

        const int total = writers * perWriter;
        Assert.Equal(LiveMonitoringLimits.MaxDiagnostics, repository.LastDiagnostics.Count);
        Assert.Equal(
            total - LiveMonitoringLimits.MaxDiagnostics,
            service.Snapshot.Counters.SuppressedDiagnostics);
        Assert.DoesNotContain(
            repository.LastDiagnostics,
            item => item.Code == "diagnostics_suppressed");
    }

    // ---------- fail-closed source validation ----------

    [Fact]
    public void SourceValidationFailsClosedWithoutASubscription()
    {
        var matched = LiveMonitoringService.MatchesSubscribedSource(
            null,
            LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()),
            RawEvent(LiveMonitoringChannels.SysmonProvider, 26, LiveMonitoringChannels.SysmonOperational),
            out var mismatch);

        Assert.False(matched);
        Assert.NotEqual(string.Empty, mismatch);
    }

    [Fact]
    public void SourceValidationFailsClosedWithoutAParsedEvent()
    {
        var matched = LiveMonitoringService.MatchesSubscribedSource(
            LiveMonitoringChannels.CreateDefaultSubscription(),
            LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()),
            null,
            out var mismatch);

        Assert.False(matched);
        Assert.NotEqual(string.Empty, mismatch);
    }

    [Fact]
    public void SourceValidationAcceptsAMatchingOrigin()
    {
        var matched = LiveMonitoringService.MatchesSubscribedSource(
            LiveMonitoringChannels.CreateDefaultSubscription(),
            LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()),
            RawEvent(LiveMonitoringChannels.SysmonProvider, 26, LiveMonitoringChannels.SysmonOperational),
            out var mismatch);

        Assert.True(matched);
        Assert.Equal(string.Empty, mismatch);
    }

    // ---------- consumer resilience ----------

    [Fact]
    public async Task UnexpectedObserverExceptionDoesNotStopTheConsumerOrUnbalanceCounts()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);
        // The throwing subscriber is registered FIRST: with per-subscriber isolation the
        // observer registered after it must still receive every classification.
        var seen = 0;
        service.EventClassified += (_, _) =>
        {
            if (Interlocked.Increment(ref seen) == 1)
            {
                throw new InvalidCastException("fixture observer failure");
            }
        };
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(1)));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(2)));
        observer.WaitForCount(2, Patience);
        await service.StopAsync();

        var session = Assert.Single(repository.Sessions);
        Assert.Equal(2, session.Counters.Received);
        Assert.Equal(2, session.Counters.DeleteFact);
        Assert.True(session.Counters.IsBalanced);
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_classification_observer_failed");
    }

    [Fact]
    public async Task SourceStopFailureStillCompletesTheWriterAndDrainsTheConsumer()
    {
        // The queue holds a record while the consumer is parked, then the source throws
        // on stop. Shutdown must still complete the writer and await the consumer, so
        // every queued record is classified before the final snapshot is taken.
        var source = new ThrowingStopSource();
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var parked = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var first = true;
        service.EventClassified += (_, _) =>
        {
            if (!first)
            {
                return;
            }

            first = false;
            entered.Set();
            parked.Wait(Patience);
        };

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(1)));
        Assert.True(entered.Wait(Patience));
        // The second record is sitting in the queue, unclassified.
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(2)));

        var stopping = service.StopAsync();
        parked.Set();
        await stopping;

        var session = Assert.Single(repository.Sessions);
        Assert.Equal(2, session.Counters.Received);
        Assert.Equal(2, session.Counters.DeleteFact);
        Assert.True(session.Counters.IsBalanced);
        Assert.Equal(LiveMonitoringState.Error, session.FinalState);
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_source_stop_failed");
        Assert.True(service.SessionPersisted);
    }

    [Fact]
    public async Task ShutdownFailureNamesEveryFailedStepWithoutOverwriting()
    {
        var source = new ThrowingStopSource();
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);

        await service.StartAsync();
        await service.StopAsync();

        // The aggregate error names the failing step by code rather than replacing it.
        Assert.Contains(
            "live_source_stop_failed",
            service.Snapshot.LastError,
            StringComparison.Ordinal);
        Assert.Contains(
            "fixture unexpected stop failure",
            service.Snapshot.LastError,
            StringComparison.Ordinal);
        var diagnostic = Assert.Single(
            repository.LastDiagnostics,
            item => item.Code == "live_source_stop_failed");
        Assert.Equal(ImportDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("subscribe", diagnostic.Stage);
    }

    [Fact]
    public async Task PipelineShutdownFailureStillReleasesResourcesAndSavesAnErrorSession()
    {
        var source = new ThrowingStopSource();
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);

        await service.StartAsync();
        await service.StopAsync();

        Assert.Equal(1, repository.SaveCount);
        var session = Assert.Single(repository.Sessions);
        Assert.Equal(LiveMonitoringState.Error, session.FinalState);
        Assert.True(session.Counters.IsBalanced);
        // The failing step is named precisely rather than as one opaque shutdown error.
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_source_stop_failed");
        Assert.Contains("实时管线关闭", service.Snapshot.LastError, StringComparison.Ordinal);

        // The service must still be usable afterwards, with no stale pipeline state.
        source.ThrowOnStop = false;
        await service.StartAsync();
        await service.StopAsync();
        Assert.Equal(2, repository.SaveCount);
        Assert.Equal(
            LiveMonitoringState.Stopped,
            repository.Sessions[1].FinalState);
        Assert.Equal(0, source.LiveWatcherCount);
    }

    [Fact]
    public async Task AThrowingClassificationObserverDoesNotSuppressLaterObservers()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        var throwingCalls = 0;
        var secondCalls = 0;
        var thirdCalls = 0;

        service.EventClassified += (_, _) =>
        {
            Interlocked.Increment(ref throwingCalls);
            throw new InvalidCastException("fixture first observer failure");
        };
        service.EventClassified += (_, _) => Interlocked.Increment(ref secondCalls);
        service.EventClassified += (_, _) => Interlocked.Increment(ref thirdCalls);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(1)));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(2)));
        observer.WaitForCount(2, Patience);
        await service.StopAsync();

        Assert.Equal(2, throwingCalls);
        Assert.Equal(2, secondCalls);
        Assert.Equal(2, thirdCalls);
        Assert.Equal(2, service.Snapshot.Counters.DeleteFact);
        Assert.True(service.Snapshot.Counters.IsBalanced);
    }

    [Fact]
    public async Task AThrowingSnapshotObserverDoesNotSuppressLaterObserversOrEscape()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);
        var secondCalls = 0;
        service.SnapshotChanged += (_, _) =>
            throw new InvalidCastException("fixture snapshot observer failure");
        service.SnapshotChanged += (_, _) => Interlocked.Increment(ref secondCalls);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        // Publishing runs the snapshot observers on the delivery thread; a throwing one
        // must not escape into the watcher callback.
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        Assert.True(secondCalls > 0);
        Assert.Equal(1, service.Snapshot.Counters.DeleteFact);
        Assert.True(service.Snapshot.Counters.IsBalanced);
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_snapshot_observer_failed");
    }

    // ---------- Phase 2B.1 live evidence ----------

    [Fact]
    public async Task SchemaAndSessionStartHappenBeforeAnyProbeOrWatcher()
    {
        var probe = FakeProbe.AllAvailable();
        var repository = new FakeRepository();
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(probe, source, repository);

        await service.StartAsync();

        // Ordering is what makes this fail closed: nothing is subscribed until the
        // database has accepted that this session exists.
        Assert.Equal(1, repository.ValidateCount);
        Assert.Equal(1, repository.StartCount);
        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(2048, Assert.Single(repository.Starts).QueueCapacity);
        Assert.Equal(2, source.LiveWatcherCount);

        await service.StopAsync();
    }

    [Fact]
    public async Task AFailedSessionStartCreatesNoWatcherAndReportsError()
    {
        var probe = FakeProbe.AllAvailable();
        var repository = new FakeRepository
        {
            StartException = new InvalidOperationException("fixture start failure")
        };
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(probe, source, repository);

        await service.StartAsync();

        Assert.Equal(0, source.StartCount);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        // Nothing was started, so nothing may claim to have completed — and with no
        // start row to hang it on, no summary is written either.
        Assert.Empty(repository.Completions);
        Assert.Empty(repository.Sessions);
        Assert.Equal(
            LiveMonitoringService.SessionStartNotPersistedMessage,
            service.Snapshot.LastError);
    }

    [Fact]
    public async Task NoSubscribableChannelStillLeavesAStartAndAnErrorCompletion()
    {
        var probe = FakeProbe.With(
            Status(LiveMonitoringChannels.SysmonOperational, LiveChannelAvailability.Unavailable),
            Status(LiveMonitoringChannels.Security, LiveChannelAvailability.AccessDenied));
        var repository = new FakeRepository();
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(probe, source, repository);

        await service.StartAsync();

        Assert.Single(repository.Starts);
        var completion = Assert.Single(repository.Completions);
        Assert.Equal(LiveMonitoringState.Error, completion.FinalState);
        Assert.Equal(0, completion.PersistedRecordCount);
        Assert.Equal(0, source.LiveWatcherCount);
    }

    [Fact]
    public async Task ReceivedSequenceStartsAtOneAndIncreasesStrictly()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        for (var index = 1; index <= 3; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(3, Patience);
        await service.StopAsync();

        Assert.Equal(
            new long[] { 1, 2, 3 },
            repository.Records.Select(record => record.ReceivedSequence).ToArray());
        Assert.All(
            repository.Records,
            record => Assert.Equal(
                $"{record.LiveSessionId}:{record.ReceivedSequence}",
                record.LiveEvidenceId));
    }

    [Fact]
    public async Task AnOversizedRecordConsumesASequenceAndLeavesAGap()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        // One code unit above the ceiling: received and counted, but never queued,
        // never parsed and never stored.
        source.Publish(LiveEventFixtures.SysmonRecord(
            new string('x', LiveMonitoringLimits.MaxEventXmlCharacters + 1)));
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(3)));
        observer.WaitForCount(2, Patience);
        await service.StopAsync();

        Assert.Equal(
            new long[] { 1, 3 },
            repository.Records.Select(record => record.ReceivedSequence).ToArray());
        var completion = Assert.Single(repository.Completions);
        Assert.Equal(3, completion.Counters.Received);
        Assert.Equal(1, completion.Counters.Error);
        Assert.Equal(2, completion.PersistedRecordCount);
        Assert.True(completion.Counters.IsBalanced);
    }

    [Fact]
    public async Task AFullQueueConsumesASequenceAndLeavesAGap()
    {
        var source = new FakeLiveEventSource();
        // Capacity 1 plus a parked consumer guarantees the second record finds the queue
        // full without any timing guess.
        using var appendGate = new ManualResetEventSlim(false);
        var parked = new FakeRepository { AppendGate = appendGate };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            parked,
            new LiveMonitoringOptions(QueueCapacity: 1));
        await service.StartAsync();

        for (var index = 1; index <= 200; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        appendGate.Set();
        await service.StopAsync();

        var completion = Assert.Single(parked.Completions);
        Assert.Equal(200, completion.Counters.Received);
        Assert.True(completion.Counters.Dropped > 0);
        Assert.True(completion.Counters.IsBalanced);
        // Dropped records consumed a sequence, so the stored ones are not contiguous.
        Assert.Equal(
            completion.PersistedRecordCount,
            parked.Records.Count);
        Assert.True(
            parked.Records.Count < 200,
            "a full queue must drop records rather than block the callback");
    }

    [Fact]
    public async Task ABatchIsFlushedWhenItFillsAndAgainOnStop()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        const int Total = LiveMonitoringLimits.MaxCaptureBatchRecords + 3;
        for (var index = 1; index <= Total; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(Total, Patience);
        await service.StopAsync();

        // One full batch plus the remainder flushed by Stop.
        Assert.Equal(
            [LiveMonitoringLimits.MaxCaptureBatchRecords, 3],
            repository.BatchSizes);
        Assert.Equal(Total, repository.Records.Count);
        Assert.Equal(Total, Assert.Single(repository.Completions).PersistedRecordCount);
    }

    [Fact]
    public async Task SingleRecordFlushesWhenItsDeadlineExpiresWithoutStop()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        Assert.Equal(0, repository.AppendCount);
        Assert.Equal(1, time.CreatedTimerCount);
        await time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        await WaitForAsync(() => repository.AppendCount == 1);

        Assert.Equal([1], repository.BatchSizes);
        Assert.Single(repository.Records);
        Assert.Equal(LiveMonitoringState.Running, service.Snapshot.State);
        Assert.Equal(0, time.ActiveTimerCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task SixtyThreeRecordsFlushTogetherAtTheDeadlineWithoutStop()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        for (var index = 1;
             index < LiveMonitoringLimits.MaxCaptureBatchRecords;
             index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(
            LiveMonitoringLimits.MaxCaptureBatchRecords - 1,
            Patience);
        Assert.Equal(0, repository.AppendCount);
        Assert.Equal(1, time.CreatedTimerCount);

        await time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        await WaitForAsync(() => repository.AppendCount == 1);

        Assert.Equal([LiveMonitoringLimits.MaxCaptureBatchRecords - 1], repository.BatchSizes);
        Assert.Equal(
            LiveMonitoringLimits.MaxCaptureBatchRecords - 1,
            repository.Records.Count);
        await service.StopAsync();
    }

    [Fact]
    public async Task FullBatchFlushesImmediatelyWithoutAdvancingTime()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        for (var index = 1;
             index <= LiveMonitoringLimits.MaxCaptureBatchRecords;
             index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(LiveMonitoringLimits.MaxCaptureBatchRecords, Patience);
        await WaitForAsync(() => repository.AppendCount == 1);

        Assert.Equal([LiveMonitoringLimits.MaxCaptureBatchRecords], repository.BatchSizes);
        Assert.Equal(1, time.CreatedTimerCount);
        Assert.Equal(0, time.ActiveTimerCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task LaterRecordsDoNotResetTheFirstRecordDeadline()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        for (var second = 1; second <= 4; second++)
        {
            await time.AdvanceAsync(TimeSpan.FromSeconds(1));
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(second + 1)));
            observer.WaitForCount(second + 1, Patience);
            Assert.Equal(1, time.CreatedTimerCount);
            Assert.Equal(0, repository.AppendCount);
        }

        await time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => repository.AppendCount == 1);

        Assert.Equal([5], repository.BatchSizes);
        await service.StopAsync();
    }

    [Fact]
    public async Task FullBatchAndDeadlineRaceNeverDuplicatesEvidence()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        for (var index = 1;
             index < LiveMonitoringLimits.MaxCaptureBatchRecords;
             index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(
            LiveMonitoringLimits.MaxCaptureBatchRecords - 1,
            Patience);
        var advancing = time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(
                LiveMonitoringLimits.MaxCaptureBatchRecords)));
        observer.WaitForCount(LiveMonitoringLimits.MaxCaptureBatchRecords, Patience);
        await advancing;
        await service.StopAsync();

        Assert.Equal(
            LiveMonitoringLimits.MaxCaptureBatchRecords,
            repository.Records.Count);
        Assert.Equal(
            LiveMonitoringLimits.MaxCaptureBatchRecords,
            repository.Records.Select(record => record.ReceivedSequence).Distinct().Count());
        Assert.Equal(
            LiveMonitoringLimits.MaxCaptureBatchRecords,
            repository.BatchSizes.Sum());
        Assert.Single(repository.Completions);
    }

    [Fact]
    public async Task AdvancingTimeWithAnEmptyBatchNeverCallsAppend()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out _,
            out var repository,
            timeProvider: time);
        await service.StartAsync();

        await time.AdvanceAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, time.CreatedTimerCount);
        Assert.Equal(0, repository.AppendCount);
        await service.StopAsync();
        Assert.Equal(0, repository.AppendCount);
    }

    [Fact]
    public async Task StopAndDeadlineRaceFlushAndCompleteExactlyOnce()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        var watcher = source.Watcher(0);
        watcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        var advancing = time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        var stopping = service.StopAsync();
        await Task.WhenAll(advancing, stopping);

        Assert.Single(repository.Records);
        Assert.Equal(1, repository.AppendCount);
        Assert.Single(repository.Completions);
        Assert.Single(repository.Sessions);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task SourceFaultBeforeDeadlineFlushesWithoutAdvancingTime()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        source.Fault("live_watcher_failed", "fixture fault before deadline");
        await service.StopAsync();

        Assert.Single(repository.Records);
        Assert.Single(repository.Completions);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(1, time.CreatedTimerCount);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task SourceFaultAndDeadlineRaceEndsInErrorWithoutDuplicateEvidence()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        var watcher = source.Watcher(0);
        watcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        var advancing = time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        source.Fault("live_watcher_failed", "fixture deadline race");
        await advancing;
        await service.StopAsync();

        Assert.Single(repository.Records);
        Assert.Single(repository.Completions);
        Assert.Equal(
            LiveMonitoringState.Error,
            Assert.Single(repository.Sessions).FinalState);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task PersistenceFaultAtDeadlineCreatesNoFurtherAppendOrDeadline()
    {
        var time = CaptureTime();
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository
        {
            AppendException = new InvalidOperationException(
                "fixture timed append failure")
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        var watcher = source.Watcher(0);
        watcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        await time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        await WaitForAsync(() => repository.AppendCount == 1);
        await service.StopAsync();
        var timersAfterFault = time.CreatedTimerCount;
        watcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(2)));

        Assert.Equal(1, repository.AppendCount);
        Assert.Equal(timersAfterFault, time.CreatedTimerCount);
        Assert.Empty(repository.Records);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task DisposeAndDeadlineRaceLeavesNoWatcherConsumerOrTimer()
    {
        var time = CaptureTime();
        var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        var advancing = time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        var disposing = service.DisposeAsync().AsTask();
        await Task.WhenAll(advancing, disposing);

        Assert.Single(repository.Records);
        Assert.Single(repository.Completions);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(0, time.ActiveTimerCount);
        Assert.True(service.LifecycleCompleted);
    }

    [Fact]
    public async Task ChannelCompletionBeforeDeadlineFlushesImmediately()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        await service.StopAsync();

        Assert.Single(repository.Records);
        Assert.Equal([1], repository.BatchSizes);
        Assert.Equal(1, time.CreatedTimerCount);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task NextPartialBatchGetsANewDeadlineFromItsFirstRecord()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        await WaitForAsync(() => repository.AppendCount == 1);

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(2)));
        observer.WaitForCount(2, Patience);
        Assert.Equal(2, time.CreatedTimerCount);
        await time.AdvanceAsync(LiveMonitoringLimits.CaptureFlushInterval);
        await WaitForAsync(() => repository.AppendCount == 2);

        Assert.Equal([1, 1], repository.BatchSizes);
        await service.StopAsync();
    }

    [Fact]
    public async Task StableLowTrafficProducesTwoFixedDeadlineCycles()
    {
        var time = CaptureTime();
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository,
            timeProvider: time);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        for (var second = 1; second <= 4; second++)
        {
            await time.AdvanceAsync(TimeSpan.FromSeconds(1));
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(second + 1)));
            observer.WaitForCount(second + 1, Patience);
        }

        await time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => repository.AppendCount == 1);

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(6)));
        observer.WaitForCount(6, Patience);
        for (var second = 6; second <= 9; second++)
        {
            await time.AdvanceAsync(TimeSpan.FromSeconds(1));
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(second + 1)));
            observer.WaitForCount(second + 1, Patience);
        }

        await time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => repository.AppendCount == 2);

        Assert.Equal([5, 5], repository.BatchSizes);
        Assert.Equal(2, time.CreatedTimerCount);
        Assert.Equal(10, repository.Records.Count);
        await service.StopAsync();
    }

    [Fact]
    public async Task EachRecordIsParsedOnceAndCarriesItsParserIdentity()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(7)));
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        var stored = Assert.Single(repository.Records);
        var classified = Assert.Single(observer.Classifications);
        // The same single parse fed both the observer and the stored row.
        Assert.Equal(LiveEventOutcome.DeleteFact, stored.Outcome);
        Assert.Equal(classified.Outcome, stored.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(stored.ParserRawEventId));
        Assert.Equal(26, stored.ParsedEventId);
        Assert.Equal(32, stored.RawXmlSha256.Length);
        Assert.Equal(classified.Record.RawXml, stored.RawXml);
    }

    [Fact]
    public async Task AFailedAppendFaultsTheSessionAndStopsEveryWatcher()
    {
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository
        {
            AppendException = new InvalidOperationException("fixture append failure")
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(0, source.LiveWatcherCount);
        var completion = Assert.Single(repository.Completions);
        Assert.Equal(LiveMonitoringState.Error, completion.FinalState);
        // Nothing was stored, and the completion says so instead of claiming success.
        Assert.Equal(0, completion.PersistedRecordCount);
        Assert.True(completion.Counters.IsBalanced);
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_evidence_persist_failed");
    }

    [Fact]
    public async Task RepeatedStopWritesExactlyOneCompletionAndOneSummary()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out _,
            out var repository);

        await service.StartAsync();
        await service.StopAsync();
        await service.StopAsync();
        await service.DisposeAsync();

        Assert.Single(repository.Completions);
        Assert.Single(repository.Sessions);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task CompletionIsWrittenAfterTheConsumerHasDrained()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository);
        await service.StartAsync();

        for (var index = 1; index <= 5; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        // No wait on the observer: Stop itself must drain and flush before completing.
        await service.StopAsync();

        var completion = Assert.Single(repository.Completions);
        Assert.Equal(5, completion.Counters.Received);
        Assert.Equal(5, repository.Records.Count);
        Assert.Equal(5, completion.PersistedRecordCount);
        Assert.True(completion.IsConsistent);
    }

    [Fact]
    public async Task ALateCallbackNeitherConsumesASequenceNorIsPersisted()
    {
        await using var service = CreateService(
            FakeProbe.AllAvailable(),
            out var source,
            out var repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        var watcher = source.Watcher(0);
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        // A watcher that was torn down still calls back; it belongs to no session.
        watcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(2)));

        Assert.Single(repository.Records);
        Assert.Equal(1, Assert.Single(repository.Completions).PersistedRecordCount);
    }

    // ---------- Phase 2B.1 fail-closed persistence ----------

    public static TheoryData<Exception> NonWhitelistedAppendFailures() =>
    [
        new TimeoutException("fixture append timeout"),
        new ObjectDisposedException("fixture disposed connection"),
        // A cancellation that nobody requested: still a storage failure, not a stop.
        new OperationCanceledException("fixture unrequested cancellation")
    ];

    [Theory]
    [MemberData(nameof(NonWhitelistedAppendFailures))]
    public async Task AnyAppendFailureFaultsTheSessionWhateverItsType(Exception failure)
    {
        // Regression guard for the defect where an exception outside the old allow-list
        // escaped FlushAsync, was filed as a parse error, and silently dropped the batch.
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository { AppendException = failure };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        const int Batch = LiveMonitoringLimits.MaxCaptureBatchRecords;
        for (var index = 1; index <= Batch; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(Batch, Patience);
        await service.StopAsync();

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Empty(repository.Records);
        var completion = Assert.Single(repository.Completions);
        Assert.Equal(LiveMonitoringState.Error, completion.FinalState);
        Assert.Equal(0, completion.PersistedRecordCount);
        Assert.Equal(Batch, completion.Counters.DeleteFact);
        Assert.True(completion.Counters.IsBalanced);
        Assert.Equal(
            LiveMonitoringState.Error,
            Assert.Single(repository.Sessions).FinalState);
        // Named as a storage fault, never as a parse failure.
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_evidence_persist_failed");
        Assert.DoesNotContain(
            repository.LastDiagnostics,
            item => item.Code == "live_event_processing_failed");
    }

    [Fact]
    public async Task AFaultedSessionStopsAttemptingFurtherAppends()
    {
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository
        {
            AppendException = new TimeoutException("fixture append timeout")
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();

        const int Total = (LiveMonitoringLimits.MaxCaptureBatchRecords * 2) + 5;
        for (var index = 1; index <= Total; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(LiveMonitoringLimits.MaxCaptureBatchRecords, Patience);
        await service.StopAsync();

        // Exactly one append was attempted; the fault stopped every later one.
        Assert.Equal(1, repository.AppendCount);
        Assert.Equal(0, Assert.Single(repository.Completions).PersistedRecordCount);
    }

    [Fact]
    public async Task AFailedCompletionIsReportedAsErrorAndNotRetried()
    {
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository
        {
            SaveException = new InvalidOperationException("fixture completion failure")
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        await service.StartAsync();

        await service.StopAsync();
        await service.StopAsync();

        // Nothing was stored, so the session did not stop cleanly and must not say it did.
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.False(string.IsNullOrWhiteSpace(service.Snapshot.LastError));
        Assert.False(service.SessionPersisted);
        Assert.Empty(repository.Completions);
        Assert.Empty(repository.Sessions);
        // Completion is attempted exactly once; a repeated Stop does not retry it.
        Assert.Equal(1, repository.SaveCount);
        // The start row and any committed evidence stay: an incomplete capture.
        Assert.Single(repository.Starts);
    }

    [Fact]
    public async Task AppendFaultRemainsLastErrorWhenCompletionAlsoFails()
    {
        const string AppendFailure = "specific append failure A";
        const string CompletionFailure = "generic completion failure B";
        var repository = new FakeRepository
        {
            AppendException = new InvalidOperationException(AppendFailure),
            SaveException = new InvalidOperationException(CompletionFailure)
        };
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);

        await service.StopAsync();

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Contains(AppendFailure, service.Snapshot.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CompletionFailure,
            service.Snapshot.LastError,
            StringComparison.Ordinal);
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_evidence_persist_failed"
                && item.Message.Contains(AppendFailure, StringComparison.Ordinal));
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_session_persist_failed"
                && item.Message.Contains(CompletionFailure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletionFaultIsLastErrorWhenThereIsNoEarlierCause()
    {
        const string CompletionFailure = "completion-only failure B";
        var repository = new FakeRepository
        {
            SaveException = new InvalidOperationException(CompletionFailure)
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            new FakeLiveEventSource(),
            repository);
        await service.StartAsync();

        await service.StopAsync();

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Contains(
            CompletionFailure,
            service.Snapshot.LastError,
            StringComparison.Ordinal);
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_session_persist_failed"
                && item.Message.Contains(CompletionFailure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceFaultRemainsLastErrorWhenCompletionAlsoFails()
    {
        const string SourceFailure = "specific source failure A";
        const string CompletionFailure = "generic completion failure B";
        var repository = new FakeRepository
        {
            SaveException = new InvalidOperationException(CompletionFailure)
        };
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        await service.StartAsync();

        source.Fault("live_watcher_failed", SourceFailure);
        await service.StopAsync();

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Contains(SourceFailure, service.Snapshot.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CompletionFailure,
            service.Snapshot.LastError,
            StringComparison.Ordinal);
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_watcher_failed"
                && item.Message.Contains(SourceFailure, StringComparison.Ordinal));
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_session_persist_failed"
                && item.Message.Contains(CompletionFailure, StringComparison.Ordinal));
    }

    /// <summary>
    /// Queue overflow is a condition, not a fault. It may claim the user-visible error
    /// while nothing else has, but the first real fault replaces it and is then never
    /// displaced again — including by traffic that keeps arriving afterwards.
    /// </summary>
    [Fact]
    public async Task QueueOverflowNoticeYieldsToTheFirstRealFaultAndNeverReturns()
    {
        const string SourceFailure = "specific source failure A";
        var source = new FakeLiveEventSource();
        // Capacity 1 plus a parked consumer makes the overflow deterministic.
        using var appendGate = new ManualResetEventSlim(false);
        var repository = new FakeRepository { AppendGate = appendGate };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository,
            new LiveMonitoringOptions(QueueCapacity: 1));
        await service.StartAsync();

        for (var index = 1; index <= 200; index++)
        {
            source.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        // The overflow notice is shown while nothing more causal exists.
        Assert.Contains(
            "队列已满",
            service.Snapshot.LastError ?? string.Empty,
            StringComparison.Ordinal);

        // OnSourceFault records the root cause synchronously before it returns.
        source.Fault("live_watcher_failed", SourceFailure);

        Assert.Contains(SourceFailure, service.Snapshot.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "队列已满",
            service.Snapshot.LastError ?? string.Empty,
            StringComparison.Ordinal);

        appendGate.Set();
        await service.StopAsync();

        // Late traffic after the fault must not restore the overflow notice.
        source.Watchers[0].Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(999)));

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Contains(SourceFailure, service.Snapshot.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "队列已满",
            service.Snapshot.LastError ?? string.Empty,
            StringComparison.Ordinal);
        // Both remain separately diagnosable.
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_queue_overflow");
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_watcher_failed"
                && item.Message.Contains(SourceFailure, StringComparison.Ordinal));
        var completion = Assert.Single(repository.Completions);
        Assert.Equal(LiveMonitoringState.Error, completion.FinalState);
        Assert.True(completion.Counters.Dropped > 0);
        Assert.True(completion.Counters.IsBalanced);
    }

    /// <summary>
    /// The precedence rules above only hold if no branch writes the user-visible error
    /// directly. The one window where a fault is recorded while the session still accepts
    /// events — a cancelled start, between marking the fault and stopping acceptance — is
    /// a synchronous gap that cannot be entered deterministically from outside, so the
    /// invariant is pinned at its source instead: exactly three assignments exist, and
    /// the queue-overflow branch goes through the shared condition entry point.
    /// </summary>
    [Fact]
    public void LastErrorIsOnlyAssignedThroughTheSharedEntryPoints()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "DeleteAudit.Infrastructure",
            "LiveMonitoring",
            "LiveMonitoringService.cs"));

        // BeginSession resets it, MarkFaultedCore records a fault, ReportConditionCore
        // records a non-fault condition. Any fourth assignment is a bypass.
        Assert.Equal(3, CountOccurrences(source, "_lastError ="));

        var overflowBranch = source.IndexOf(
            "live_queue_overflow",
            StringComparison.Ordinal);
        Assert.True(overflowBranch > 0);
        var precedingText = source[..overflowBranch];
        var lastConditionCall = precedingText.LastIndexOf(
            "ReportConditionCore(",
            StringComparison.Ordinal);
        var lastDirectAssignment = precedingText.LastIndexOf(
            "_lastError =",
            StringComparison.Ordinal);
        Assert.True(
            lastConditionCall > lastDirectAssignment,
            "the queue overflow branch must report through ReportConditionCore");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// A cancelled start records its root cause while the session is still accepting
    /// events. Whatever arrives afterwards must not overwrite it.
    /// </summary>
    [Fact]
    public async Task CancelledStartRootCauseSurvivesLaterTraffic()
    {
        using var startGate = new TestGate();
        var source = new FakeLiveEventSource { StartGate = startGate };
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository,
            new LiveMonitoringOptions(QueueCapacity: 1));
        using var cancellation = new CancellationTokenSource();

        var starting = Task.Run(() => service.StartAsync(cancellation.Token));
        await startGate.Entered.WaitAsync(Patience);
        await cancellation.CancelAsync();
        startGate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        var rootCause = service.Snapshot.LastError;
        Assert.Contains("取消", rootCause ?? string.Empty, StringComparison.Ordinal);

        // Records arriving after the aborted start are late; none of them may claim the
        // user-visible error, and a full queue must not produce an overflow notice here.
        for (var index = 1; index <= 200; index++)
        {
            source.Watchers[0].Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        Assert.Equal(rootCause, service.Snapshot.LastError);
        Assert.DoesNotContain(
            "队列已满",
            service.Snapshot.LastError ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(200, service.Snapshot.Counters.LateDiscarded);
        Assert.Equal(0, service.Snapshot.Counters.Dropped);
        var completion = Assert.Single(repository.Completions);
        Assert.Equal(LiveMonitoringState.Error, completion.FinalState);
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_start_cancelled");
    }

    // ---------- Phase 2B.1 start cancellation lifecycle ----------

    [Fact]
    public async Task CancellingBeforeTheStartIsRecordedLeavesNothingBehind()
    {
        using var gate = new TestGate();
        var repository = new FakeRepository { StartCaptureGate = gate };
        var source = new FakeLiveEventSource();
        var probe = FakeProbe.AllAvailable();
        await using var service = new LiveMonitoringService(probe, source, repository);
        using var cts = new CancellationTokenSource();

        // Run on the pool: StartAsync executes synchronously up to its first real
        // await, so a gated fake would otherwise block the test's own thread.
        var starting = Task.Run(() => service.StartAsync(cts.Token));
        // Execution is parked inside StartCaptureSessionAsync, before it records anything.
        await gate.Entered;
        await cts.CancelAsync();
        gate.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        // Nothing was recorded, so there is nothing to complete and nothing to explain.
        Assert.Empty(repository.Starts);
        Assert.Empty(repository.Completions);
        Assert.Empty(repository.Sessions);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(0, source.StartCount);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(0, probe.ProbeCount);
        // A user cancellation is not a database failure and not a faulted capture.
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
        // Adjacent facts: nothing was blamed on anything, no counter moved, and the
        // lifecycle really did finish rather than merely look finished.
        Assert.Null(service.Snapshot.LastError);
        Assert.Empty(service.SessionDiagnostics);
        Assert.Equal(LiveMonitoringCounters.Empty, service.Snapshot.Counters);
        Assert.True(service.CompletionStarted);
        Assert.True(service.LifecycleCompleted);
        Assert.False(service.SessionPersisted);
        // Repeated shutdown stays a no-op: still nothing recorded, still Stopped.
        await service.StopAsync();
        Assert.Empty(repository.Starts);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(LiveMonitoringState.Stopped, service.Snapshot.State);
    }

    [Fact]
    public async Task CancellingAfterTheStartIsRecordedCompletesTheSessionAsError()
    {
        using var probeGate = new ManualResetEventSlim(false);
        var repository = new FakeRepository();
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(
            new BlockingProbe(probeGate, TimeSpan.FromSeconds(10)),
            source,
            repository);
        using var cts = new CancellationTokenSource();

        var starting = service.StartAsync(cts.Token);
        // The start row is already written; the probe has not returned yet.
        await WaitForAsync(() => repository.Starts.Count == 1);
        await cts.CancelAsync();
        probeGate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        Assert.Single(repository.Starts);
        var completion = Assert.Single(repository.Completions);
        var summary = Assert.Single(repository.Sessions);
        Assert.Equal(LiveMonitoringState.Error, completion.FinalState);
        Assert.Equal(LiveMonitoringState.Error, summary.FinalState);
        Assert.Equal(completion.Counters, summary.Counters);
        Assert.Equal(0, completion.PersistedRecordCount);
        Assert.Empty(repository.Records);
        Assert.Equal(0, source.StartCount);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_start_cancelled");
        // Adjacent facts: the cancellation is the recorded root cause, the summary was
        // persisted exactly once, and a later Stop neither retries nor duplicates it.
        Assert.Contains("取消", service.Snapshot.LastError, StringComparison.Ordinal);
        Assert.True(service.SessionPersisted);
        Assert.Equal(1, repository.SaveCount);
        await service.StopAsync();
        await service.StopAsync();
        Assert.Equal(1, repository.SaveCount);
        Assert.Single(repository.Completions);
        Assert.Single(repository.Sessions);
    }

    [Fact]
    public async Task CancellingAfterTheSourcePartiallyStartsReleasesEveryWatcher()
    {
        using var gate = new TestGate();
        var repository = new FakeRepository();
        var source = new FakeLiveEventSource { StartGate = gate };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var cts = new CancellationTokenSource();

        var starting = Task.Run(() => service.StartAsync(cts.Token));
        // Watchers already exist at the gate; the cancellation lands on a live source.
        await gate.Entered;
        Assert.Equal(2, source.LiveWatcherCount);
        await cts.CancelAsync();
        gate.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        // Everything the half-started source created is released.
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.All(source.Watchers, watcher => Assert.True(watcher.IsDisposed));
        Assert.Equal(1, source.StopCount);
        Assert.Single(repository.Starts);
        Assert.Equal(
            LiveMonitoringState.Error,
            Assert.Single(repository.Completions).FinalState);
        Assert.Equal(
            LiveMonitoringState.Error,
            Assert.Single(repository.Sessions).FinalState);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);

        // Completing again must not produce a second completion or summary.
        await service.StopAsync();
        await service.DisposeAsync();
        Assert.Single(repository.Completions);
        Assert.Single(repository.Sessions);
    }

    [Fact]
    public async Task ACancelledStartWhoseCompletionAlsoFailsStaysIncompleteAndError()
    {
        using var gate = new TestGate();
        var repository = new FakeRepository
        {
            SaveException = new InvalidOperationException("fixture completion failure")
        };
        var source = new FakeLiveEventSource { StartGate = gate };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var cts = new CancellationTokenSource();

        var starting = Task.Run(() => service.StartAsync(cts.Token));
        await gate.Entered;
        await cts.CancelAsync();
        gate.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        // The start stands on its own with no completion: an incomplete capture.
        Assert.Single(repository.Starts);
        Assert.Empty(repository.Completions);
        Assert.Empty(repository.Sessions);
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.False(string.IsNullOrWhiteSpace(service.Snapshot.LastError));
        Assert.False(service.SessionPersisted);
        Assert.Equal(0, source.LiveWatcherCount);
        // Exactly one attempt; a later Stop does not retry it.
        await service.StopAsync();
        Assert.Equal(1, repository.SaveCount);
        // Adjacent facts: the completion failure never reached the database, so the
        // cancellation root cause survives only in memory — and it must still be there,
        // alongside the persistence failure, rather than one having replaced the other.
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_start_cancelled");
        Assert.Contains(
            service.SessionDiagnostics,
            item => item.Code == "live_session_persist_failed");
        Assert.Contains("取消", service.Snapshot.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "fixture completion failure",
            service.Snapshot.LastError,
            StringComparison.Ordinal);
        Assert.True(service.LifecycleCompleted);
    }

    [Fact]
    public async Task ACancelledStartDoesNotContaminateTheNextSession()
    {
        using var gate = new TestGate();
        var repository = new FakeRepository();
        var source = new FakeLiveEventSource { StartGate = gate };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var cts = new CancellationTokenSource();

        var starting = Task.Run(() => service.StartAsync(cts.Token));
        await gate.Entered;
        await cts.CancelAsync();
        gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        var cancelledSessionId = Assert.Single(repository.Starts).LiveSessionId;

        // Session B runs normally on a source that no longer gates.
        var sourceB = new FakeLiveEventSource();
        await using var serviceB = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            sourceB,
            repository);
        using var observer = new ClassificationObserver(serviceB);
        await serviceB.StartAsync();
        sourceB.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await serviceB.StopAsync();

        var sessionB = repository.Completions[^1];
        Assert.NotEqual(cancelledSessionId, sessionB.LiveSessionId);
        Assert.Equal(LiveMonitoringState.Stopped, sessionB.FinalState);
        Assert.Equal(1, sessionB.Counters.Received);
        Assert.Equal(1, sessionB.Counters.DeleteFact);
        Assert.Equal(0, sessionB.Counters.LateDiscarded);
        Assert.Equal(0, sessionB.Counters.Error);
        Assert.Equal(1, sessionB.PersistedRecordCount);

        // B numbers its own evidence from 1 and shares no identity with the cancelled one.
        var recordsB = repository.Records
            .Where(record => record.LiveSessionId == sessionB.LiveSessionId)
            .ToArray();
        Assert.Equal(new long[] { 1 }, recordsB.Select(r => r.ReceivedSequence).ToArray());
        Assert.All(
            repository.Records,
            record => Assert.NotEqual(cancelledSessionId, record.LiveSessionId));
        // Adjacent facts: B inherits none of A's explanation. Neither the cancellation
        // diagnostic nor A's error text may appear anywhere in B's session.
        Assert.DoesNotContain(
            serviceB.SessionDiagnostics,
            item => item.Code == "live_start_cancelled");
        Assert.Null(serviceB.Snapshot.LastError);
        Assert.DoesNotContain(
            repository.LastDiagnostics,
            item => item.Code == "live_start_cancelled");
        Assert.Equal(LiveMonitoringState.Stopped, serviceB.Snapshot.State);
        Assert.True(serviceB.SessionPersisted);
    }

    // ---------- Phase 2B.1 callback identity ----------

    [Fact]
    public async Task AnOldSessionCallbackChangesNoCounterOfTheNewSession()
    {
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        var sessionAWatcher = source.Watcher(0);
        await service.StopAsync();

        await service.StartAsync();
        // The torn-down session A watcher fires while session B is running.
        sessionAWatcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(99)));
        source.Watcher(0).Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        var sessionB = repository.Completions[^1];
        Assert.Equal(1, sessionB.Counters.Received);
        Assert.Equal(1, sessionB.Counters.DeleteFact);
        Assert.Equal(0, sessionB.Counters.ProcessContext);
        Assert.Equal(0, sessionB.Counters.SecurityEvidence);
        Assert.Equal(0, sessionB.Counters.Ignored);
        Assert.Equal(0, sessionB.Counters.Error);
        Assert.Equal(0, sessionB.Counters.Dropped);
        // The heart of the fix: another session's record must not land here.
        Assert.Equal(0, sessionB.Counters.LateDiscarded);
        Assert.Equal(1, sessionB.PersistedRecordCount);
        Assert.Equal(0, repository.Sessions[^1].Counters.LateDiscarded);

        // Session B numbers its own records from 1, and stored exactly one.
        var sessionBRecords = repository.Records
            .Where(record => record.LiveSessionId == sessionB.LiveSessionId)
            .ToArray();
        Assert.Equal(new long[] { 1 }, sessionBRecords.Select(r => r.ReceivedSequence).ToArray());
    }

    [Fact]
    public async Task ThisSessionsOwnLateCallbackStillCountsAsLateDiscarded()
    {
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        using var observer = new ClassificationObserver(service);
        await service.StartAsync();
        var watcher = source.Watcher(0);
        source.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(1)));
        observer.WaitForCount(1, Patience);
        await service.StopAsync();

        // Same generation, but the session has stopped accepting.
        watcher.Publish(LiveEventFixtures.SysmonRecord(
            LiveEventFixtures.SysmonDelete(2)));

        var counters = service.Snapshot.Counters;
        Assert.Equal(1, counters.Received);
        Assert.Equal(1, counters.LateDiscarded);
        Assert.True(counters.IsBalanced);
        // It consumed no sequence and was never stored.
        Assert.Equal(new long[] { 1 }, repository.Records.Select(r => r.ReceivedSequence).ToArray());
    }

    // ---------- queue options ----------

    [Fact]
    public void QueueDisablesSynchronousContinuations()
    {
        // The producer calls TryWrite while holding the session lock, so a consumer
        // continuation must never be inlined onto the delivery thread.
        var options = LiveMonitoringService.CreateQueueOptions(2048);

        Assert.False(options.AllowSynchronousContinuations);
        Assert.Equal(System.Threading.Channels.BoundedChannelFullMode.Wait, options.FullMode);
        Assert.True(options.SingleReader);
        Assert.False(options.SingleWriter);
        Assert.Equal(2048, options.Capacity);
    }

    [Fact]
    public async Task WatcherPublishThreadNeverRunsAppendOrCompletion()
    {
        using var appendGate = new ManualResetEventSlim(false);
        using var completionGate = new ManualResetEventSlim(false);
        using var keepPublisherAlive = new ManualResetEventSlim(false);
        var repository = new FakeRepository
        {
            AppendGate = appendGate,
            SaveGate = completionGate
        };
        var source = new FakeLiveEventSource();
        var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);
        var publishReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publisherThreadId = 0;
        var publisher = new Thread(() =>
        {
            try
            {
                publisherThreadId = Environment.CurrentManagedThreadId;
                for (var index = 1;
                     index <= LiveMonitoringLimits.MaxCaptureBatchRecords;
                     index++)
                {
                    source.Publish(LiveEventFixtures.SysmonRecord(
                        LiveEventFixtures.SysmonDelete(index)));
                }

                publishReturned.TrySetResult();
                keepPublisherAlive.Wait(Patience);
            }
            catch (Exception exception)
            {
                publishReturned.TrySetException(exception);
            }
        })
        {
            IsBackground = false,
            Name = "DeleteAudit fixture watcher publisher"
        };

        try
        {
            await service.StartAsync();
            publisher.Start();

            await publishReturned.Task.WaitAsync(Patience);
            await repository.FirstAppendEntered.WaitAsync(Patience);

            Assert.True(publishReturned.Task.IsCompletedSuccessfully);
            Assert.NotEqual(0, publisherThreadId);
            Assert.DoesNotContain(publisherThreadId, repository.AppendThreadIds);

            var stopping = service.StopAsync();
            Assert.False(stopping.IsCompleted);
            appendGate.Set();
            await repository.FirstCompletionEntered.WaitAsync(Patience);

            Assert.DoesNotContain(
                publisherThreadId,
                repository.CompletionThreadIds);
            completionGate.Set();
            await stopping;

            Assert.Equal(1, repository.AppendCount);
            Assert.Equal(1, repository.SaveCount);
            Assert.Single(repository.Completions);
            Assert.Equal(0, source.LiveWatcherCount);
        }
        finally
        {
            appendGate.Set();
            completionGate.Set();
            keepPublisherAlive.Set();
            if (publisher.IsAlive)
            {
                Assert.True(publisher.Join(Patience));
            }

            await service.DisposeAsync();
        }
    }

    // ---------- XAML structure ----------

    [Fact]
    public void LivePreviewTabPinsTheDisclosureOutsideTheScrollViewer()
    {
        var document = System.Xml.Linq.XDocument.Load(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "DeleteAudit.Viewer",
                "MainWindow.xaml"));
        var tab = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TabItem"
                && (string?)element.Attribute("Header") == "实时接入预览");

        var disclosure = tab
            .Descendants()
            .Single(element => element
                .Descendants()
                .Any(child => (string?)child.Attribute("Text")
                    == "{Binding LiveMonitoring.Disclosure}")
                && element.Name.LocalName == "Border");

        Assert.DoesNotContain(
            disclosure.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        Assert.Single(
            tab.Descendants(),
            element => element.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain(
            tab.Descendants().Single(element => element.Name.LocalName == "ScrollViewer")
                .Descendants(),
            element => (string?)element.Attribute("Text")
                == "{Binding LiveMonitoring.Disclosure}");
    }

    [Fact]
    public async Task DiagnosticMessagesAreTruncated()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await service.StartAsync();
        source.Watcher(0).Report(new LiveMonitoringDiagnostic(
            "live_record_read_failed",
            new string('m', LiveMonitoringLimits.MaxDiagnosticMessageCharacters * 3),
            ImportDiagnosticSeverity.Error,
            "receive",
            DateTimeOffset.UtcNow));
        await service.StopAsync();

        var diagnostic = Assert.Single(repository.LastDiagnostics);
        Assert.Equal(
            LiveMonitoringLimits.MaxDiagnosticMessageCharacters,
            diagnostic.Message.Length);
    }

    // ---------- fault and late callbacks ----------

    [Fact]
    public async Task SourceFaultStopsWatchersAndSavesOneErrorSession()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await service.StartAsync();
        source.Fault("live_watcher_failed", "fixture watcher failure");
        await service.StopAsync();

        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.All(source.Watchers, watcher => Assert.True(watcher.IsDisposed));
        var session = Assert.Single(repository.Sessions);
        Assert.Equal(LiveMonitoringState.Error, session.FinalState);
        Assert.Equal(1, source.StartCount);
    }

    [Fact]
    public async Task EventsAfterAFaultAreNotAccepted()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);

        await service.StartAsync();
        var watcher = source.Watcher(0);
        watcher.Fault("live_watcher_failed", "fixture watcher failure");
        await service.StopAsync();
        watcher.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));

        var counters = service.Snapshot.Counters;
        Assert.Equal(0, counters.Received);
        Assert.Equal(0, counters.DeleteFact);
        Assert.Equal(1, counters.LateDiscarded);
        Assert.True(counters.IsBalanced);
    }

    [Fact]
    public async Task TwoChannelsFaultingSaveExactlyOneSession()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);

        await service.StartAsync();
        var first = source.Watcher(0);
        var second = source.Watcher(1);
        first.Fault("live_watcher_failed", "channel one failed");
        second.Fault("live_watcher_failed", "channel two failed");
        await service.StopAsync();

        Assert.Equal(1, repository.SaveCount);
        Assert.Equal(LiveMonitoringState.Error, Assert.Single(repository.Sessions).FinalState);
    }

    [Fact]
    public async Task FaultRecordedBeforeErrorStateIsPublishedStillPersistsAsError()
    {
        // Deterministic reproduction of the mislabelling race: the source faults from
        // inside StartAsync, so the service publishes its state *after* the fault is
        // already recorded. The persisted final state must come from the fault fact,
        // not from that later UI state.
        var source = new FakeLiveEventSource
        {
            FaultDuringStart = "fixture fault during start"
        };
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);

        await service.StartAsync();
        await service.StopAsync();

        Assert.Equal(1, repository.SaveCount);
        var session = Assert.Single(repository.Sessions);
        Assert.Equal(LiveMonitoringState.Error, session.FinalState);
        Assert.Contains(
            repository.LastDiagnostics,
            item => item.Code == "live_watcher_failed");
        Assert.Equal(0, source.LiveWatcherCount);
        Assert.All(source.Watchers, watcher => Assert.True(watcher.IsDisposed));
        Assert.Equal(LiveMonitoringState.Error, service.Snapshot.State);
    }

    [Fact]
    public async Task FaultAndUserStopRaceSaveExactlyOneErrorSession()
    {
        // The fault teardown is parked inside CompleteSessionAsync while the user's Stop
        // runs, so both paths are genuinely in flight at the same time.
        using var saveGate = new ManualResetEventSlim(false);
        var repository = new FakeRepository { SaveGate = saveGate };
        var source = new FakeLiveEventSource();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);

        await service.StartAsync();
        source.Watcher(0).Fault("live_watcher_failed", "fixture race");
        var stopping = service.StopAsync();
        saveGate.Set();
        await stopping;
        await service.StopAsync();

        Assert.Equal(1, repository.SaveCount);
        Assert.Equal(LiveMonitoringState.Error, Assert.Single(repository.Sessions).FinalState);
        Assert.Equal(0, source.LiveWatcherCount);
    }

    [Fact]
    public async Task CompletionStartedLifecycleAndPersistedAreDistinctFacts()
    {
        var repository = new FakeRepository
        {
            SaveException = new InvalidOperationException("fixture persist failure")
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            new FakeLiveEventSource(),
            repository);

        Assert.True(service.CompletionStarted);
        await service.StartAsync();
        Assert.False(service.CompletionStarted);
        Assert.False(service.LifecycleCompleted);
        Assert.False(service.SessionPersisted);

        await service.StopAsync();

        // The session finished its lifecycle but was never stored; one flag could not
        // have expressed both.
        Assert.True(service.CompletionStarted);
        Assert.True(service.LifecycleCompleted);
        Assert.False(service.SessionPersisted);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task SuccessfulStopMarksTheSessionPersisted()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out _, out _);

        await service.StartAsync();
        await service.StopAsync();

        Assert.True(service.CompletionStarted);
        Assert.True(service.LifecycleCompleted);
        Assert.True(service.SessionPersisted);
    }

    [Fact]
    public async Task NormalStopPersistsAsStoppedWithTheCurrentApplicationVersion()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out _, out var repository);

        await service.StartAsync();
        await service.StopAsync();

        var session = Assert.Single(repository.Sessions);
        Assert.Equal(LiveMonitoringState.Stopped, session.FinalState);
        // The live path takes its version from the one shared constant, so a session
        // summary can never disagree with what the offline path writes.
        Assert.Equal(ApplicationVersionInfo.Current, session.ApplicationVersion);
    }

    [Fact]
    public async Task OldSessionFaultDoesNotMarkTheNextSessionAsError()
    {
        var source = new FakeLiveEventSource();
        var repository = new FakeRepository();
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            source,
            repository);

        await service.StartAsync();
        var oldWatcher = source.Watcher(0);
        oldWatcher.Fault("live_watcher_failed", "fixture first session fault");
        await service.StopAsync();

        await service.StartAsync();
        // A stale in-flight fault from the previous session must not taint this one.
        oldWatcher.Fault("live_watcher_failed", "fixture stale fault");
        await service.StopAsync();

        Assert.Equal(2, repository.SaveCount);
        Assert.Equal(LiveMonitoringState.Error, repository.Sessions[0].FinalState);
        Assert.Equal(LiveMonitoringState.Stopped, repository.Sessions[1].FinalState);
    }

    [Fact]
    public async Task LateCallbackFromAnOldWatcherDoesNotPolluteTheNewSession()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        var oldWatcher = source.Watcher(0);
        await service.StopAsync();

        await service.StartAsync();
        // The old watcher is disposed but an in-flight callback can still fire.
        oldWatcher.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));
        source.Watcher(0).Publish(
            LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(2)));
        observer.WaitForCount(1, Patience);

        var counters = service.Snapshot.Counters;
        Assert.Equal(1, counters.Received);
        Assert.Equal(1, counters.DeleteFact);
        // The old watcher's record belonged to the previous session, so it must not move
        // any counter of this one — including LateDiscarded, which is persisted.
        Assert.Equal(0, counters.LateDiscarded);
        Assert.True(counters.IsBalanced);
    }

    [Fact]
    public void PublicDocsNoLongerClaimLiveDetailIsDiscarded()
    {
        // Phase 2B.1 persists live detail, so every retired Phase 2A-only sentence has
        // become a false public claim. These are exact retired sentences, not loose scans.
        var root = RepositoryRoot();
        foreach (var (file, retired) in new[]
                 {
                     ("README.md", "本次实时事件的明细不会保留"),
                     ("README.en.md", "the detail of those live events is not kept"),
                     ("README.en.md", "only a session summary is saved"),
                     ("README.en.md", "Live event detail is **not retained** today"),
                     ("README.fil.md", "hindi itinatago ang detalye ng mga live event"),
                     ("SECURITY.md", "Only a **session summary** is stored for live monitoring"),
                     ("SECURITY.md", "live event detail is not persisted")
                 })
        {
            Assert.DoesNotContain(
                retired,
                File.ReadAllText(Path.Combine(root, file)),
                StringComparison.Ordinal);
        }

        // Each language must state the new behaviour and its documented limits.
        foreach (var (file, required) in new[]
                 {
                     ("README.md", "会写入本机的 SQLite 数据库"),
                     ("README.md", "63 条"),
                     ("README.en.md", "written to a local SQLite database"),
                     ("README.en.md", "63 uncommitted records"),
                     ("README.fil.md", "isinusulat sa lokal na SQLite database"),
                     ("README.fil.md", "63 na hindi pa na-commit na record"),
                     ("SECURITY.md", "not a tamper-proof medium"),
                     ("CONTRIBUTING.md", "0004_phase_2b_live_evidence.sql")
                 })
        {
            Assert.Contains(
                required,
                File.ReadAllText(Path.Combine(root, file)),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(
        "README.md",
        "达到 64 条时会立即进入持久化",
        "第一条进入空批次起，通常约 5 秒",
        "同批后续记录不会重新开始期限",
        "仍可能丢失最多 63 条尚未提交的记录",
        "只尝试保存一次",
        "不会自动重试",
        "此前已经成功提交的记录仍会保留",
        "显示 `Error`",
        "不是严格时限保证")]
    [InlineData(
        "README.en.md",
        "enters persistence immediately at 64 records",
        "about five seconds after its first record enters an empty batch",
        "later records in that batch do not restart the deadline",
        "still lose up to 63 uncommitted records",
        "completion record is attempted once",
        "no automatic retry",
        "records committed successfully beforehand are kept",
        "session shows `Error`",
        "not a strict timing guarantee")]
    [InlineData(
        "README.fil.md",
        "Agad na pumapasok sa persistence ang batch kapag umabot sa 64 record",
        "mga limang segundo matapos pumasok ang unang record sa bakanteng batch",
        "hindi inuulit ng mga kasunod na record ang deadline",
        "maaari pa ring mawala ang hanggang 63 na hindi pa na-commit na record",
        "Isang beses lang sinusubukang i-save ang completion record",
        "walang awtomatikong retry",
        "nananatili ang mga record na matagumpay nang na-commit",
        "`Error` ang ipinapakita",
        "hindi ito mahigpit na garantiya sa oras")]
    [InlineData(
        "SECURITY.md",
        "enters persistence immediately at 64 records",
        "about five seconds after its first record enters an empty batch",
        "later records in the same batch do not restart that deadline",
        "may lose up to 63 uncommitted records",
        "completion save is attempted once",
        "not retried automatically",
        "records committed successfully before that failure are kept",
        "shown as `Error`",
        "not a strict five-second guarantee")]
    public void PublicDocsDescribeBoundedLatencyWithoutErasingResidualRisk(
        string file,
        string immediateBatch,
        string partialBatch,
        string fixedDeadline,
        string residualLoss,
        string completionAttempt,
        string noRetry,
        string committedRecords,
        string errorState,
        string nonGuarantee)
    {
        var document = string.Join(
            " ",
            File.ReadAllText(Path.Combine(RepositoryRoot(), file))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        foreach (var required in new[]
                 {
                     immediateBatch,
                     partialBatch,
                     fixedDeadline,
                     residualLoss,
                     completionAttempt,
                     noRetry,
                     committedRecords,
                     errorState,
                     nonGuarantee
                 })
        {
            Assert.Contains(required, document, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "guaranteed within five seconds",
            document,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "63-record crash-loss window has been eliminated",
            document,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordsRejectedDuringStopAreNotCountedAsQueueFullDrops()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out _);

        await service.StartAsync();
        var watcher = source.Watcher(0);
        await service.StopAsync();
        watcher.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));

        var snapshot = service.Snapshot;
        Assert.Equal(0, snapshot.Counters.Dropped);
        Assert.Equal(1, snapshot.Counters.LateDiscarded);
        Assert.DoesNotContain("队列已满", snapshot.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalSnapshotIsStableAndBalanced()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        var watcher = source.Watcher(0);
        for (var index = 1; index <= 40; index++)
        {
            watcher.Publish(LiveEventFixtures.SysmonRecord(
                LiveEventFixtures.SysmonDelete(index)));
        }

        observer.WaitForCount(40, Patience);
        await service.StopAsync();
        // Late traffic after the session closed must not move the persisted numbers.
        watcher.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete(99)));

        var session = Assert.Single(repository.Sessions);
        Assert.Equal(40, session.Counters.Received);
        Assert.Equal(40, session.Counters.DeleteFact);
        Assert.True(session.Counters.IsBalanced);
        Assert.Equal(40, service.Snapshot.Counters.Received);
    }

    // ---------- persistence ----------

    [Fact]
    public async Task PersistenceFailureIsReportedOnceWithoutRetryLoop()
    {
        var repository = new FakeRepository
        {
            SaveException = new InvalidOperationException("fixture persist failure")
        };
        await using var service = new LiveMonitoringService(
            FakeProbe.AllAvailable(),
            new FakeLiveEventSource(),
            repository);

        await service.StartAsync();
        await service.StopAsync();

        Assert.Equal(1, repository.SaveCount);
        Assert.Contains(
            "fixture persist failure",
            service.Snapshot.LastError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionStatisticsArePersistedAccurately()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out var source, out var repository);
        using var observer = new ClassificationObserver(service);

        await service.StartAsync();
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonDelete()));
        source.Publish(LiveEventFixtures.SysmonRecord(LiveEventFixtures.SysmonProcessCreate()));
        source.Watcher(1).Publish(LiveEventFixtures.SecurityRecord(
            LiveEventFixtures.Security4663("0x1", "%%4416")));
        source.Publish(LiveEventFixtures.SysmonRecord("<Event"));
        observer.WaitForCount(4, Patience);
        await service.StopAsync();

        var session = Assert.Single(repository.Sessions);
        Assert.Equal(4, session.Counters.Received);
        Assert.Equal(1, session.Counters.DeleteFact);
        Assert.Equal(1, session.Counters.ProcessContext);
        Assert.Equal(0, session.Counters.SecurityEvidence);
        Assert.Equal(1, session.Counters.Ignored);
        Assert.Equal(1, session.Counters.Error);
        Assert.Equal(0, session.Counters.Dropped);
        Assert.Equal(2, session.Counters.Parsed);
        Assert.True(session.Counters.IsBalanced);
        Assert.Equal(2, session.ChannelStatuses.Count);
        Assert.NotNull(session.StoppedUtc);
    }

    // ---------- subscription shape ----------

    [Fact]
    public void DefaultSubscriptionStartsAtNowAndNeverReplaysHistory() =>
        Assert.False(LiveMonitoringChannels.CreateDefaultSubscription().ReadExistingEvents);

    [Fact]
    public void SubscriptionCoversOnlyTheApprovedEventIdsAndProviders()
    {
        var subscription = LiveMonitoringChannels.CreateDefaultSubscription();
        var sysmon = subscription.Channels.Single(channel =>
            channel.ChannelName == LiveMonitoringChannels.SysmonOperational);
        var security = subscription.Channels.Single(channel =>
            channel.ChannelName == LiveMonitoringChannels.Security);

        Assert.Equal([1, 23, 26], sysmon.EventIds);
        Assert.Equal([4663], security.EventIds);
        Assert.Equal(LiveMonitoringChannels.SysmonProvider, sysmon.ExpectedProviderName);
        Assert.Equal(LiveMonitoringChannels.SecurityProvider, security.ExpectedProviderName);
        Assert.Equal(2, subscription.Channels.Count);
        Assert.False(sysmon.Accepts(LiveMonitoringChannels.SecurityProvider, 4663));
        Assert.False(sysmon.Accepts(LiveMonitoringChannels.SysmonProvider, 4663));
        Assert.True(sysmon.Accepts(LiveMonitoringChannels.SysmonProvider, 26));
    }

    [Fact]
    public void EventIdFilterIsAppliedInTheQueryNotInMemory()
    {
        Assert.Equal(
            "*[System[(EventID=1 or EventID=23 or EventID=26)]]",
            WindowsEventLogWatcherSource.BuildEventIdXPath(
                LiveMonitoringChannels.SysmonEventIds));
        Assert.Equal(
            "*[System[(EventID=4663)]]",
            WindowsEventLogWatcherSource.BuildEventIdXPath(
                LiveMonitoringChannels.SecurityEventIds));
    }

    [Fact]
    public void UnfilteredChannelSubscriptionIsRejected()
    {
        var subscription = new LiveEventSubscription(
            [
                new LiveChannelSubscription(
                    LiveMonitoringChannels.Security,
                    [],
                    LiveMonitoringChannels.SecurityProvider)
            ]);

        Assert.Throws<ArgumentException>(subscription.Validate);
    }

    [Fact]
    public void SingleEventXmlLimitIsAProductionConstant()
    {
        Assert.Equal(1_048_576, LiveMonitoringLimits.MaxEventXmlCharacters);
        Assert.Equal(256, LiveMonitoringLimits.MaxDiagnostics);
        Assert.Equal(2_048, LiveMonitoringLimits.MaxDiagnosticMessageCharacters);
    }

    // ---------- disclosure ----------

    [Fact]
    public void BannerDescribesTheCompletedPhase2BLiveSurface()
    {
        var banner = MainWindowViewModel.CapabilityBanner;

        Assert.DoesNotContain("当前尚未实时监控", banner, StringComparison.Ordinal);
        Assert.Contains("用户手动开启的实时事件接入", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("实时事件详情暂不持久保存", banner, StringComparison.Ordinal);
        Assert.Contains("原始 XML", banner, StringComparison.Ordinal);
        Assert.Contains("解析/分类结果", banner, StringComparison.Ordinal);
        Assert.Contains("本机查看器", banner, StringComparison.Ordinal);
        Assert.Contains("实时历史", banner, StringComparison.Ordinal);
        Assert.Contains("派生分析", banner, StringComparison.Ordinal);
        Assert.Contains("live-owned", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("尚无实时历史", banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LivePageDisclosesWhatIsStoredAndWhatIsStillMissing()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out _, out _);
        using var viewModel = CreateViewModel(service);

        // What Phase 2B.1 now actually does.
        Assert.Contains("原始 XML", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("分类结果", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("会写入本机查看器数据库", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("会保留", viewModel.Disclosure, StringComparison.Ordinal);
        // What the later Phase 2B pages now do, without blurring their boundaries.
        Assert.Contains("实时历史", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("风险分析", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("live-owned", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("用户主动", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("不会自动写入或伪装成离线", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("离线身份", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("缺口", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("异常中断", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("不上传", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("达到 64 条时立即", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("第一条进入空批次起通常约 5 秒", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("后续记录不会重新开始期限", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("实际完成时间稍晚", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("不是严格的五秒保证", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("最多 63 条尚未提交记录", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("只尝试保存一次且不自动重试", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("显示 Error", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("已经成功提交的记录仍会保留", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("不是防篡改介质", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("不是完整或生产级取证系统", viewModel.Disclosure, StringComparison.Ordinal);
        // The retired Phase 2A claim must not come back: live detail is no longer lost
        // on stop, so the page may not keep saying that it is.
        Assert.DoesNotContain(
            "实时事件原始 XML、删除事实、关联结果和风险结果不会保存",
            viewModel.Disclosure,
            StringComparison.Ordinal);
        Assert.Contains("不能阻止、恢复或完整取证", viewModel.Disclaimer, StringComparison.Ordinal);

        // The disclosure is a plain always-present string, not a colour or a state.
        await service.StartAsync();
        Assert.Equal(
            LiveMonitoringViewModel.PersistenceDisclosure,
            viewModel.Disclosure);
    }

    [Fact]
    public void ReadmeDocumentsLiveAccessAccurately()
    {
        var readme = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "README.md"));

        Assert.DoesNotContain(
            "读取本机实时 Windows Event Log、注册服务",
            readme,
            StringComparison.Ordinal);
        Assert.DoesNotContain("当前仍没有实时监控", readme, StringComparison.Ordinal);
        Assert.Contains("默认不读取", readme, StringComparison.Ordinal);
        // Phase 2B.1 changed what is kept: the README now states that live detail is
        // written locally, not that only a summary survives.
        Assert.Contains("会写入本机的 SQLite 数据库", readme, StringComparison.Ordinal);
        Assert.Contains("会话摘要", readme, StringComparison.Ordinal);
        Assert.Contains("实时历史与派生分析", readme, StringComparison.Ordinal);
        Assert.Contains("独立实时规范投影", readme, StringComparison.Ordinal);
        Assert.Contains("不会把实时数据伪装成离线导入", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("目前没有实时历史", readme, StringComparison.Ordinal);
        Assert.Contains("Phase 2B", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPlanRecordsCompletedPhase2BScopeAndSeparation()
    {
        var plan = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "docs", "PROJECT_PLAN.md"));

        Assert.Contains("Phase 2A 实现状态", plan, StringComparison.Ordinal);
        Assert.Contains("Phase 2B 实现状态", plan, StringComparison.Ordinal);
        Assert.Contains("WindowsEventXmlParser", plan, StringComparison.Ordinal);
        Assert.Contains("DeleteEventCorrelator", plan, StringComparison.Ordinal);
        Assert.Contains("live-owned", plan, StringComparison.Ordinal);
        Assert.Contains("0005", plan, StringComparison.Ordinal);
        Assert.Contains("不写、不冒充、也不连接", plan, StringComparison.Ordinal);
        Assert.Contains("不是防篡改保证", plan, StringComparison.Ordinal);
    }

    // ---------- view model ----------

    [Fact]
    public async Task ViewModelBusyStateRecoversAfterStartAndStop()
    {
        await using var service = CreateService(FakeProbe.AllAvailable(), out _, out _);
        using var viewModel = CreateViewModel(service);

        await viewModel.StartCommand.ExecuteAsync();
        Assert.False(viewModel.IsBusy);
        Assert.Equal("正在接入（预览）", viewModel.StateLabel);

        await viewModel.StopCommand.ExecuteAsync();
        Assert.False(viewModel.IsBusy);
        Assert.Equal("已停止", viewModel.StateLabel);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ViewModelShowsVisibleErrorWhenStartFails()
    {
        var probe = FakeProbe.With(
            Status(LiveMonitoringChannels.SysmonOperational, LiveChannelAvailability.Unavailable),
            Status(LiveMonitoringChannels.Security, LiveChannelAvailability.Unavailable));
        await using var service = CreateService(probe, out _, out _);
        using var viewModel = CreateViewModel(service);

        await viewModel.StartCommand.ExecuteAsync();

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasError);
        Assert.Equal("错误", viewModel.StateLabel);
        Assert.True(viewModel.CanStart);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DeleteAudit.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    /// <summary>
    /// Spins until a condition the fakes publish becomes true. Used only where the state
    /// being waited for has no signal of its own; the deadline exists to fail a broken
    /// test rather than to time anything.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + (long)Patience.TotalMilliseconds;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition never became true");
            await Task.Yield();
        }
    }

    private static LiveMonitoringService CreateService(
        ILiveEventChannelProbe probe,
        out FakeLiveEventSource source,
        out FakeRepository repository,
        LiveMonitoringOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        source = new FakeLiveEventSource();
        repository = new FakeRepository();
        return new LiveMonitoringService(
            probe,
            source,
            repository,
            options,
            timeProvider);
    }

    private static ManualTimerTimeProvider CaptureTime() =>
        new(new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));

    private static LiveMonitoringViewModel CreateViewModel(ILiveMonitoringService service) =>
        new(service, new InlineDispatcher());

    private static LiveChannelStatus Status(
        string channelName,
        LiveChannelAvailability availability,
        string? detail = null) =>
        new(channelName, availability, detail);

    private static string Head(string value) =>
        value.Split('—', StringSplitOptions.TrimEntries)[0];

    private static LiveMonitoringDiagnostic Diagnostic(string code) =>
        new(
            code,
            "fixture diagnostic",
            ImportDiagnosticSeverity.Error,
            "receive",
            new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero));

    private static RawWindowsEvent RawEvent(
        string provider,
        int eventId,
        string channel) =>
        new(
            "raw-1",
            WindowsEventSource.SysmonDelete,
            "LAB-PC",
            channel,
            provider,
            eventId,
            1,
            new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
            "<Event />",
            new Dictionary<string, string?>(),
            []);

    /// <summary>A source whose StopAsync throws an unexpected exception.</summary>
    private sealed class ThrowingStopSource : ILiveEventSource
    {
        private readonly FakeLiveEventSource _inner = new();

        public bool ThrowOnStop { get; set; } = true;

        public bool IsRunning => _inner.IsRunning;

        public int LiveWatcherCount => _inner.LiveWatcherCount;

        public void Publish(LiveEventRecord record) => _inner.Publish(record);

        public Task StartAsync(
            LiveEventSubscription subscription,
            ILiveEventSink sink,
            CancellationToken cancellationToken = default) =>
            _inner.StartAsync(subscription, sink, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnStop)
            {
                ThrowOnStop = false;
                throw new InvalidCastException("fixture unexpected stop failure");
            }

            return _inner.StopAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class NullSink : ILiveEventSink
    {
        public void Publish(LiveEventRecord record)
        {
        }

        public void Report(LiveMonitoringDiagnostic diagnostic)
        {
        }

        public void Fault(string code, string message)
        {
        }
    }

    private sealed class TrackingSubscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class ThreadRecordingProbe : ILiveEventChannelProbe
    {
        public int ProbeThreadId { get; private set; }

        public Task<IReadOnlyList<LiveChannelStatus>> ProbeAsync(
            IReadOnlyList<string> channelNames,
            CancellationToken cancellationToken = default) =>
            Task.Run<IReadOnlyList<LiveChannelStatus>>(
                () =>
                {
                    ProbeThreadId = Environment.CurrentManagedThreadId;
                    return channelNames
                        .Select(name => new LiveChannelStatus(
                            name,
                            LiveChannelAvailability.Available))
                        .ToArray();
                },
                cancellationToken);
    }

    private sealed class ClassificationObserver : IDisposable
    {
        private readonly LiveMonitoringService _service;
        private readonly List<LiveEventClassification> _classifications = [];
        private readonly object _sync = new();

        public ClassificationObserver(LiveMonitoringService service)
        {
            _service = service;
            service.EventClassified += OnClassified;
        }

        public IReadOnlyList<LiveEventClassification> Classifications
        {
            get
            {
                lock (_sync)
                {
                    return [.. _classifications];
                }
            }
        }

        public void WaitForCount(int expected, TimeSpan patience)
        {
            using var reached = new ManualResetEventSlim(false);
            void Probe(object? sender, LiveEventClassification classification)
            {
                if (Count >= expected)
                {
                    reached.Set();
                }
            }

            _service.EventClassified += Probe;
            try
            {
                if (Count >= expected || reached.Wait(patience))
                {
                    return;
                }

                throw new TimeoutException(
                    $"Only {Count} of {expected} records were classified within {patience}.");
            }
            finally
            {
                _service.EventClassified -= Probe;
            }
        }

        public void Dispose() => _service.EventClassified -= OnClassified;

        private int Count
        {
            get
            {
                lock (_sync)
                {
                    return _classifications.Count;
                }
            }
        }

        private void OnClassified(object? sender, LiveEventClassification classification)
        {
            lock (_sync)
            {
                _classifications.Add(classification);
            }
        }
    }
}
