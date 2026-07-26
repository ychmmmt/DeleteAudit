using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Sessions;

public sealed class DeleteSessionAggregator
{
    private readonly AuditRiskOptions _options;
    private readonly IReadOnlyList<ProtectedPathRule> _protectedRules;
    private readonly TimeProvider _timeProvider;
    private readonly List<DeleteSession> _sessions = [];

    public DeleteSessionAggregator(
        AuditRiskOptions options,
        IEnumerable<ProtectedPathRule>? protectedRules = null,
        TimeProvider? timeProvider = null)
    {
        options.Validate();
        _options = options;
        _protectedRules = (protectedRules ?? []).Where(rule => rule.Enabled).ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<DeleteSession> Sessions => _sessions;

    public SessionAggregationResult Add(NormalizedDeleteEvent deleteEvent)
    {
        ArgumentNullException.ThrowIfNull(deleteEvent);

        var matchingRule = _protectedRules.FirstOrDefault(rule => rule.Matches(deleteEvent.FullPath));
        var isProtected = matchingRule is not null;
        var processIdentity = GetProcessIdentity(deleteEvent);
        var userIdentity = GetUserIdentity(deleteEvent);
        var mainPath = matchingRule?.Path ?? GetParentPath(deleteEvent.FullPath) ?? $"unknown:{deleteEvent.RawEventId}";

        var session = _sessions
            .Where(candidate =>
                candidate.ProcessIdentity == processIdentity
                && candidate.UserIdentity == userIdentity
                && string.Equals(candidate.MainPath, mainPath, StringComparison.OrdinalIgnoreCase)
                && IsInSessionWindow(candidate, deleteEvent.OccurredUtc))
            .OrderByDescending(candidate => candidate.LastEventUtc)
            .FirstOrDefault();

        var sessionCreated = session is null;
        session ??= CreateSession(processIdentity, userIdentity, mainPath, deleteEvent.OccurredUtc);

        var nextCount = session.DeleteEventIds.Contains(deleteEvent.DeleteEventId)
            ? session.ConfirmedItemCount
            : session.ConfirmedItemCount + 1;
        var (risk, ruleCode) = Assess(nextCount, isProtected);
        var added = session.Add(deleteEvent, isProtected, risk);
        var assessment = new RiskAssessment(
            Guid.NewGuid().ToString("D"),
            session.DeleteSessionId,
            _timeProvider.GetUtcNow(),
            risk,
            ruleCode,
            session.ConfirmedItemCount,
            session.OpenedUtc,
            session.LastEventUtc,
            isProtected);

        return new SessionAggregationResult(session, assessment, sessionCreated, added);
    }

    private DeleteSession CreateSession(
        string processIdentity,
        string userIdentity,
        string mainPath,
        DateTimeOffset openedUtc)
    {
        var session = new DeleteSession(
            Guid.NewGuid().ToString("D"),
            processIdentity,
            userIdentity,
            mainPath,
            openedUtc);
        _sessions.Add(session);
        return session;
    }

    private (AuditRiskLevel Risk, string RuleCode) Assess(int count, bool isProtected)
    {
        if (isProtected)
        {
            return (AuditRiskLevel.Critical, "protected_root");
        }

        if (count >= _options.CriticalCount)
        {
            return (AuditRiskLevel.Critical, $"burst_{_options.CriticalCount}_in_window");
        }

        if (count >= _options.WarningCount)
        {
            return (AuditRiskLevel.Warning, $"burst_{_options.WarningCount}_in_window");
        }

        return (AuditRiskLevel.Informational, "single_delete");
    }

    private bool IsInSessionWindow(DeleteSession session, DateTimeOffset occurredUtc) =>
        (occurredUtc - session.LastEventUtc).Duration() <= _options.SessionWindow
        && occurredUtc >= session.OpenedUtc - _options.SessionWindow;

    private static string GetProcessIdentity(NormalizedDeleteEvent value)
    {
        if (!string.IsNullOrWhiteSpace(value.ProcessGuid))
        {
            return $"guid:{value.ProcessGuid.ToUpperInvariant()}";
        }

        if (value.ProcessId is not null && !string.IsNullOrWhiteSpace(value.ComputerName))
        {
            return $"pid:{value.ComputerName.ToUpperInvariant()}:{value.ProcessId.Value}";
        }

        return $"unknown:{value.RawEventId}";
    }

    private static string GetUserIdentity(NormalizedDeleteEvent value)
    {
        if (!string.IsNullOrWhiteSpace(value.UserSid))
        {
            return $"sid:{value.UserSid.ToUpperInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(value.UserName))
        {
            return $"name:{value.UserName.ToUpperInvariant()}";
        }

        return $"unknown:{value.RawEventId}";
    }

    private static string? GetParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim().Replace('/', '\\').TrimEnd('\\');
        var separator = normalized.LastIndexOf('\\');
        return separator <= 2 ? normalized : normalized[..separator];
    }
}
