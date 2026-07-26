namespace DeleteAudit.Collector;

public sealed class DesignOnlyWorker(ILogger<DesignOnlyWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDesignOnly =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(DesignOnlyWorker)),
            "DeleteAudit is a design-only skeleton. All collectors and runtime writes are disabled.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogDesignOnly(logger, null);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
