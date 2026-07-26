using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.LiveMonitoring;

/// <summary>
/// Detects, read-only, whether an event log channel already exists on this machine.
/// It never creates, enables, clears, or reconfigures a channel, and a missing channel
/// is a normal result rather than a product error.
///
/// The Windows APIs used here are synchronous and can block for a noticeable time on
/// the Security channel, so the whole probe runs on the thread pool: the caller's
/// thread (the WPF UI thread) is never blocked.
/// </summary>
public sealed class WindowsLiveEventChannelProbe : ILiveEventChannelProbe
{
    public Task<IReadOnlyList<LiveChannelStatus>> ProbeAsync(
        IReadOnlyList<string> channelNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelNames);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run<IReadOnlyList<LiveChannelStatus>>(
            () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return channelNames
                        .Select(name => new LiveChannelStatus(
                            name,
                            LiveChannelAvailability.Unavailable,
                            "Windows event log channels are only available on Windows."))
                        .ToArray();
                }

                var statuses = new List<LiveChannelStatus>(channelNames.Count);
                foreach (var name in channelNames)
                {
                    // The underlying call for a single channel cannot be interrupted,
                    // but every handle it opens is released before the next one starts,
                    // so cancellation takes effect at the next channel boundary.
                    cancellationToken.ThrowIfCancellationRequested();
                    statuses.Add(ProbeChannel(name));
                }

                return statuses;
            },
            cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static LiveChannelStatus ProbeChannel(string channelName)
    {
        try
        {
            using var configuration = new EventLogConfiguration(channelName);
            if (!configuration.IsEnabled)
            {
                return new LiveChannelStatus(
                    channelName,
                    LiveChannelAvailability.Disabled,
                    "通道存在但当前已禁用。");
            }
        }
        catch (EventLogNotFoundException)
        {
            return new LiveChannelStatus(
                channelName,
                LiveChannelAvailability.Unavailable,
                "系统中不存在该通道。");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Denied(channelName, exception);
        }
        catch (EventLogException exception)
        {
            return Unknown(channelName, exception);
        }

        return ProbeReadAccess(channelName);
    }

    /// <summary>
    /// An enabled channel can still be unreadable for the current user; the Security
    /// channel usually is. Opening a read-only reader is the only honest way to tell.
    /// The reader is disposed immediately and no records are read, so an empty channel
    /// is reported as available rather than missing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static LiveChannelStatus ProbeReadAccess(string channelName)
    {
        try
        {
            var query = new EventLogQuery(channelName, PathType.LogName)
            {
                TolerateQueryErrors = false
            };
            using var reader = new EventLogReader(query);
            return new LiveChannelStatus(
                channelName,
                LiveChannelAvailability.Available,
                "通道存在且当前用户可只读访问。");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Denied(channelName, exception);
        }
        catch (EventLogNotFoundException)
        {
            return new LiveChannelStatus(
                channelName,
                LiveChannelAvailability.Unavailable,
                "系统中不存在该通道。");
        }
        catch (EventLogException exception)
        {
            return Unknown(channelName, exception);
        }
    }

    private static LiveChannelStatus Denied(string channelName, Exception exception) =>
        new(
            channelName,
            LiveChannelAvailability.AccessDenied,
            LiveMonitoringLimits.TruncateMessage(exception.Message));

    private static LiveChannelStatus Unknown(string channelName, Exception exception) =>
        new(
            channelName,
            LiveChannelAvailability.UnknownError,
            LiveMonitoringLimits.TruncateMessage(exception.Message));
}
