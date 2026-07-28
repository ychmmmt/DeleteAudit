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
        var repository = new FakeRepository();
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
        _ = repository;
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
        // The fault teardown is parked inside SaveSessionAsync while the user's Stop
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
        Assert.Equal(1, counters.LateDiscarded);
        Assert.True(counters.IsBalanced);
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
    public void BannerNoLongerClaimsThereIsNoLiveMonitoring()
    {
        var banner = MainWindowViewModel.CapabilityBanner;

        Assert.DoesNotContain("当前尚未实时监控", banner, StringComparison.Ordinal);
        Assert.Contains("实时事件接入预览", banner, StringComparison.Ordinal);
        Assert.Contains("实时事件详情暂不持久保存", banner, StringComparison.Ordinal);
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
        // What it still does not do.
        Assert.Contains("尚未接入", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("风险评估", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("缺口", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("异常中断", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("不上传", viewModel.Disclosure, StringComparison.Ordinal);
        Assert.Contains("尚未投影", viewModel.Disclosure, StringComparison.Ordinal);
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
        Assert.Contains("仅保存监控会话摘要", readme, StringComparison.Ordinal);
        Assert.Contains("尚未接入", readme, StringComparison.Ordinal);
        Assert.Contains("Phase 2B", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPlanRecordsPhase2AScopeAndPhase2BDeferrals()
    {
        var plan = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "docs", "PROJECT_PLAN.md"));

        Assert.Contains("Phase 2A 实现状态", plan, StringComparison.Ordinal);
        Assert.Contains("只复用 `WindowsEventXmlParser`", plan, StringComparison.Ordinal);
        Assert.Contains("尚未接入实时管线", plan, StringComparison.Ordinal);
        Assert.Contains("不会伪造", plan, StringComparison.Ordinal);
        Assert.Contains("不构成完整的实时删除审计", plan, StringComparison.Ordinal);
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

    private static LiveMonitoringService CreateService(
        ILiveEventChannelProbe probe,
        out FakeLiveEventSource source,
        out FakeRepository repository,
        LiveMonitoringOptions? options = null)
    {
        source = new FakeLiveEventSource();
        repository = new FakeRepository();
        return new LiveMonitoringService(probe, source, repository, options);
    }

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
