using System.Globalization;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public sealed class LiveMonitoringViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Always visible on the live page. States plainly what is kept, what is not, and
    /// where the gaps come from, so the counts below can never be read as a complete
    /// audit record.
    /// </summary>
    public const string PersistenceDisclosure =
        "Phase 2B.1 实时接入：你手动开始后，本次接收到的受支持事件的原始 XML 与分类结果会写入本机查看器数据库；"
        + "停止或关闭应用后，已成功保存的实时明细会保留。仅保存监控会话摘要与这些接收明细——"
        + "实时关联、删除会话聚合和风险评估尚未接入，不会保存。"
        + "队列溢出、超大记录、写入失败或进程异常终止都可能造成缺口，因此接收序号可能不连续；"
        + "没有完成记录的会话表示本次接入可能异常中断。本程序不上传任何数据。"
        + "本轮仅完成持久化基础，尚未投影到“删除事件”或“原始证据”页面。";

    public const string CapabilityDisclaimer =
        "实时监控能力取决于系统当前已有的事件日志配置；未安装 Sysmon或未启用文件系统审计时，可能无法记录删除操作。"
        + "本功能不能阻止、恢复或完整取证删除操作。";

    private readonly ILiveMonitoringService _service;
    private readonly IUiDispatcher _dispatcher;
    private LiveMonitoringSnapshot _snapshot;
    private CancellationTokenSource? _probeCts;
    private bool _disposed;

    public LiveMonitoringViewModel(
        ILiveMonitoringService service,
        IUiDispatcher dispatcher)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _snapshot = service.Snapshot;

        StartCommand = new AsyncCommand(
            StartAsync,
            () => !IsBusy && CanStart,
            ShowUnexpectedError);
        StopCommand = new AsyncCommand(
            StopAsync,
            () => !IsBusy && CanStop,
            ShowUnexpectedError);
        ProbeChannelsCommand = new AsyncCommand(
            () => ProbeChannelsAsync(),
            () => !IsBusy && !CanStop,
            ShowUnexpectedError);

        _service.SnapshotChanged += OnSnapshotChanged;
    }

    public string Disclosure { get; } = PersistenceDisclosure;

    public string Disclaimer { get; } = CapabilityDisclaimer;

    public LiveMonitoringState State => _snapshot.State;

    public string StateLabel => _snapshot.State switch
    {
        LiveMonitoringState.Stopped => "已停止",
        LiveMonitoringState.Starting => "正在启动",
        LiveMonitoringState.Running => "正在接入（预览）",
        LiveMonitoringState.Stopping => "正在停止",
        LiveMonitoringState.Error => "错误",
        _ => ViewerDisplay.Unknown
    };

    public string SysmonChannelStatus =>
        DescribeChannel(LiveMonitoringChannels.SysmonOperational);

    public string SecurityChannelStatus =>
        DescribeChannel(LiveMonitoringChannels.Security);

    public long ReceivedCount => _snapshot.Counters.Received;

    public long DeleteFactCount => _snapshot.Counters.DeleteFact;

    public long ProcessContextCount => _snapshot.Counters.ProcessContext;

    public long SecurityEvidenceCount => _snapshot.Counters.SecurityEvidence;

    /// <summary>Classified events — the sum of the three classification counts.</summary>
    public long ClassifiedCount => _snapshot.Counters.Parsed;

    public long IgnoredCount => _snapshot.Counters.Ignored;

    public long ErrorCount => _snapshot.Counters.Error;

    public long DroppedCount => _snapshot.Counters.Dropped;

    public long LateDiscardedCount => _snapshot.Counters.LateDiscarded;

    public long SuppressedDiagnosticCount => _snapshot.Counters.SuppressedDiagnostics;

    public bool HasDropped => _snapshot.Counters.Dropped > 0;

    public string CountersSummary => string.Format(
        CultureInfo.InvariantCulture,
        "接收 {0:N0}；已分类事件 {1:N0}；忽略 {2:N0}；错误 {3:N0}；丢弃 {4:N0}",
        ReceivedCount,
        ClassifiedCount,
        IgnoredCount,
        ErrorCount,
        DroppedCount);

    /// <summary>
    /// Spelled out so nobody can read process context or security evidence as a delete.
    /// </summary>
    public string ClassificationSummary => string.Format(
        CultureInfo.InvariantCulture,
        "删除事实（Sysmon 23/26）{0:N0}；进程上下文（Sysmon 1）{1:N0}；安全补强（Security 4663）{2:N0}",
        DeleteFactCount,
        ProcessContextCount,
        SecurityEvidenceCount);

    public string QueueSummary => string.Format(
        CultureInfo.InvariantCulture,
        "队列容量 {0:N0}；当前排队 {1:N0}；停止后丢弃 {2:N0}；被抑制诊断 {3:N0}",
        _snapshot.QueueCapacity,
        _snapshot.QueueDepth,
        LateDiscardedCount,
        SuppressedDiagnosticCount);

    public string LastError => ViewerDisplay.Value(_snapshot.LastError);

    public bool HasLastError => !string.IsNullOrWhiteSpace(_snapshot.LastError);

    public bool CanStart =>
        _snapshot.State is LiveMonitoringState.Stopped or LiveMonitoringState.Error;

    public bool CanStop =>
        _snapshot.State is LiveMonitoringState.Running or LiveMonitoringState.Starting;

    public AsyncCommand StartCommand { get; }

    public AsyncCommand StopCommand { get; }

    public AsyncCommand ProbeChannelsCommand { get; }

    /// <summary>
    /// Reads current channel availability without subscribing to anything. Opening the
    /// page calls at most this; monitoring itself always waits for an explicit Start.
    /// </summary>
    public Task ProbeChannelsAsync(CancellationToken cancellationToken = default) =>
        RunSafelyAsync(async () =>
        {
            // One probe at a time: IsBusy already gates re-entry, and the previous
            // token source is replaced only after the previous run finished.
            _probeCts?.Dispose();
            _probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await _service
                .ProbeChannelsAsync(_probeCts.Token)
                .ConfigureAwait(true);
        });

    public void CancelProbe() => _probeCts?.Cancel();

    public Task StartAsync() =>
        RunSafelyAsync(async () =>
        {
            await _service.StartAsync().ConfigureAwait(true);
            if (_snapshot.State == LiveMonitoringState.Error)
            {
                ErrorMessage = _snapshot.LastError ?? "实时接入预览启动失败。";
            }
        });

    public Task StopAsync() =>
        RunSafelyAsync(async () =>
        {
            await _service.StopAsync().ConfigureAwait(true);
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.SnapshotChanged -= OnSnapshotChanged;
        _probeCts?.Dispose();
        _probeCts = null;
    }

    protected override void OnBusyStateChanged()
    {
        NotifyCommands();
        base.OnBusyStateChanged();
    }

    private void OnSnapshotChanged(object? sender, LiveMonitoringSnapshot snapshot) =>
        _dispatcher.Post(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(LiveMonitoringSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        _snapshot = snapshot;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(SysmonChannelStatus));
        OnPropertyChanged(nameof(SecurityChannelStatus));
        OnPropertyChanged(nameof(ReceivedCount));
        OnPropertyChanged(nameof(DeleteFactCount));
        OnPropertyChanged(nameof(ProcessContextCount));
        OnPropertyChanged(nameof(SecurityEvidenceCount));
        OnPropertyChanged(nameof(ClassifiedCount));
        OnPropertyChanged(nameof(IgnoredCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(DroppedCount));
        OnPropertyChanged(nameof(LateDiscardedCount));
        OnPropertyChanged(nameof(SuppressedDiagnosticCount));
        OnPropertyChanged(nameof(HasDropped));
        OnPropertyChanged(nameof(CountersSummary));
        OnPropertyChanged(nameof(ClassificationSummary));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasLastError));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        NotifyCommands();
    }

    private string DescribeChannel(string channelName)
    {
        var status = _snapshot.ChannelStatuses.FirstOrDefault(item =>
            string.Equals(item.ChannelName, channelName, StringComparison.OrdinalIgnoreCase));
        if (status is null)
        {
            return "尚未检测";
        }

        var label = status.Availability switch
        {
            LiveChannelAvailability.Available => "可用",
            LiveChannelAvailability.Unavailable =>
                string.Equals(
                    channelName,
                    LiveMonitoringChannels.SysmonOperational,
                    StringComparison.OrdinalIgnoreCase)
                    ? "未检测到 Sysmon"
                    : "通道不存在",
            LiveChannelAvailability.AccessDenied => "access_denied（无访问权限）",
            LiveChannelAvailability.Disabled => "通道已禁用",
            LiveChannelAvailability.UnknownError => "未知错误",
            _ => ViewerDisplay.Unknown
        };

        return string.IsNullOrWhiteSpace(status.Detail)
            ? label
            : $"{label} — {status.Detail}";
    }

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ProbeChannelsCommand.NotifyCanExecuteChanged();
    }
}
