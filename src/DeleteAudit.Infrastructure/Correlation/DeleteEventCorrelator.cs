using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Correlation;

public sealed class DeleteEventCorrelator
{
    private readonly CorrelationOptions _options;

    public DeleteEventCorrelator(CorrelationOptions options)
    {
        options.Validate();
        _options = options;
    }

    public CorrelationResult Correlate(
        NormalizedDeleteEvent deleteEvent,
        IEnumerable<ProcessContextEvent> processContexts,
        IEnumerable<SecurityDeleteEvidence>? securityEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(deleteEvent);
        ArgumentNullException.ThrowIfNull(processContexts);

        var contexts = processContexts.ToArray();
        var reasons = new List<string>();
        var processMatch = FindProcessMatch(deleteEvent, contexts);
        var evidenceMatch = FindSecurityMatch(deleteEvent, securityEvidence ?? []);
        var enriched = deleteEvent;
        var identityFieldsEnriched = false;

        if (processMatch is { IsAuthoritative: true })
        {
            enriched = EnrichFromProcess(enriched, processMatch.Context, reasons);
            identityFieldsEnriched = true;
        }
        else if (processMatch is not null)
        {
            reasons.Add("path_time_match_retained_as_non_authoritative_candidate");
        }

        if (evidenceMatch is { IsAuthoritative: true })
        {
            enriched = EnrichFromSecurity(enriched, evidenceMatch.Evidence, reasons);
            identityFieldsEnriched = true;
        }
        else if (evidenceMatch is not null)
        {
            reasons.Add("security_path_time_match_retained_as_non_authoritative_candidate");
        }

        enriched = enriched with { MissingFields = ComputeMissingFields(enriched) };

        var bestMatch = new[]
            {
                processMatch is null
                    ? null
                    : new MatchSummary(processMatch.Method, processMatch.Confidence, processMatch.Delta),
                evidenceMatch is null
                    ? null
                    : new MatchSummary(evidenceMatch.Method, evidenceMatch.Confidence, evidenceMatch.Delta)
            }
            .OfType<MatchSummary>()
            .OrderByDescending(match => match.Confidence)
            .ThenBy(match => match.Method)
            .FirstOrDefault();
        var method = bestMatch?.Method ?? CorrelationMethod.None;
        var confidence = bestMatch?.Confidence ?? CorrelationConfidence.None;
        var delta = bestMatch?.Delta;

        if (method == CorrelationMethod.None)
        {
            reasons.Add("no_reliable_match");
        }

        return new CorrelationResult(
            enriched,
            method,
            confidence,
            delta,
            processMatch?.Context.RawEventId,
            evidenceMatch?.Evidence.RawEventId,
            identityFieldsEnriched,
            reasons);
    }

    private ProcessMatch? FindProcessMatch(
        NormalizedDeleteEvent deleteEvent,
        IReadOnlyCollection<ProcessContextEvent> contexts)
    {
        if (!string.IsNullOrWhiteSpace(deleteEvent.ProcessGuid))
        {
            var guidMatch = contexts
                .Where(context =>
                    SameComputer(deleteEvent.ComputerName, context.ComputerName)
                    && string.Equals(
                        deleteEvent.ProcessGuid,
                        context.ProcessGuid,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(context => AbsoluteDelta(deleteEvent.OccurredUtc, context.StartedUtc))
                .FirstOrDefault();

            if (guidMatch is not null)
            {
                return new ProcessMatch(
                    guidMatch,
                    CorrelationMethod.ProcessGuid,
                    CorrelationConfidence.High,
                    AbsoluteDelta(deleteEvent.OccurredUtc, guidMatch.StartedUtc),
                    true);
            }
        }

        var pidMatch = contexts
            .Where(context =>
                deleteEvent.ProcessId is not null
                && context.ProcessId == deleteEvent.ProcessId
                && SameComputer(deleteEvent.ComputerName, context.ComputerName)
                && SameKnownUser(deleteEvent.UserSid, deleteEvent.UserName, context.UserSid, context.UserName)
                && IsWithinWindow(deleteEvent.OccurredUtc, context.StartedUtc))
            .Where(context => !WasPidReusedBeforeDelete(deleteEvent, context, contexts))
            .OrderBy(context => AbsoluteDelta(deleteEvent.OccurredUtc, context.StartedUtc))
            .FirstOrDefault();

        if (pidMatch is not null)
        {
            return new ProcessMatch(
                pidMatch,
                CorrelationMethod.DevicePidUserAndTime,
                CorrelationConfidence.Medium,
                AbsoluteDelta(deleteEvent.OccurredUtc, pidMatch.StartedUtc),
                true);
        }

        if (!string.IsNullOrWhiteSpace(deleteEvent.ProcessPath))
        {
            var pathMatch = contexts
                .Where(context =>
                    SameComputer(deleteEvent.ComputerName, context.ComputerName)
                    && PathEquals(deleteEvent.ProcessPath, context.ProcessPath)
                    && IsWithinWindow(deleteEvent.OccurredUtc, context.StartedUtc))
                .OrderBy(context => AbsoluteDelta(deleteEvent.OccurredUtc, context.StartedUtc))
                .FirstOrDefault();

            if (pathMatch is not null)
            {
                return new ProcessMatch(
                    pathMatch,
                    CorrelationMethod.PathAndTimeHeuristic,
                    CorrelationConfidence.Low,
                    AbsoluteDelta(deleteEvent.OccurredUtc, pathMatch.StartedUtc),
                    false);
            }
        }

        return null;
    }

    private SecurityMatch? FindSecurityMatch(
        NormalizedDeleteEvent deleteEvent,
        IEnumerable<SecurityDeleteEvidence> evidence)
    {
        var candidates = evidence
            .Where(item =>
                item.DeletePermission != DeletePermissionType.NotObserved
                && SameComputer(deleteEvent.ComputerName, item.ComputerName)
                && IsWithinWindow(deleteEvent.OccurredUtc, item.OccurredUtc))
            .ToArray();

        var reliable = candidates
            .Where(item =>
                deleteEvent.ProcessId is not null
                && item.ProcessId == deleteEvent.ProcessId
                && SameKnownUser(deleteEvent.UserSid, deleteEvent.UserName, item.UserSid, item.UserName)
                && PathEquals(deleteEvent.FullPath, item.ObjectPath))
            .OrderBy(item => AbsoluteDelta(deleteEvent.OccurredUtc, item.OccurredUtc))
            .FirstOrDefault();

        if (reliable is not null)
        {
            return new SecurityMatch(
                reliable,
                CorrelationMethod.DevicePidUserAndTime,
                CorrelationConfidence.Medium,
                AbsoluteDelta(deleteEvent.OccurredUtc, reliable.OccurredUtc),
                true);
        }

        var heuristic = candidates
            .Where(item => PathEquals(deleteEvent.FullPath, item.ObjectPath))
            .OrderBy(item => AbsoluteDelta(deleteEvent.OccurredUtc, item.OccurredUtc))
            .FirstOrDefault();

        return heuristic is null
            ? null
            : new SecurityMatch(
                heuristic,
                CorrelationMethod.PathAndTimeHeuristic,
                CorrelationConfidence.Low,
                AbsoluteDelta(deleteEvent.OccurredUtc, heuristic.OccurredUtc),
                false);
    }

    private static NormalizedDeleteEvent EnrichFromProcess(
        NormalizedDeleteEvent target,
        ProcessContextEvent source,
        ICollection<string> reasons)
    {
        return target with
        {
            CommandLine = Merge(target.CommandLine, source.CommandLine, "commandLine", reasons),
            ParentProcessId = Merge(target.ParentProcessId, source.ParentProcessId, "parentProcessId", reasons),
            ParentProcessPath = Merge(
                target.ParentProcessPath,
                source.ParentProcessPath,
                "parentProcessPath",
                reasons),
            ParentProcessGuid = Merge(
                target.ParentProcessGuid,
                source.ParentProcessGuid,
                "parentProcessGuid",
                reasons),
            UserName = Merge(target.UserName, source.UserName, "userName", reasons),
            UserSid = Merge(target.UserSid, source.UserSid, "userSid", reasons)
        };
    }

    private static NormalizedDeleteEvent EnrichFromSecurity(
        NormalizedDeleteEvent target,
        SecurityDeleteEvidence source,
        ICollection<string> reasons)
    {
        return target with
        {
            UserName = Merge(target.UserName, source.UserName, "userName", reasons),
            UserSid = Merge(target.UserSid, source.UserSid, "userSid", reasons),
            DeletePermission = source.DeletePermission
        };
    }

    private static T? Merge<T>(
        T? current,
        T? candidate,
        string field,
        ICollection<string> reasons)
    {
        if (candidate is null)
        {
            return current;
        }

        if (current is null)
        {
            reasons.Add($"enriched:{field}");
            return candidate;
        }

        if (!EqualityComparer<T>.Default.Equals(current, candidate))
        {
            reasons.Add($"conflict_preserved_original:{field}");
        }

        return current;
    }

    private static bool WasPidReusedBeforeDelete(
        NormalizedDeleteEvent deleteEvent,
        ProcessContextEvent candidate,
        IEnumerable<ProcessContextEvent> contexts) =>
        contexts.Any(other =>
            other.RawEventId != candidate.RawEventId
            && other.ProcessId == candidate.ProcessId
            && SameComputer(candidate.ComputerName, other.ComputerName)
            && other.StartedUtc > candidate.StartedUtc
            && other.StartedUtc <= deleteEvent.OccurredUtc);

    private bool IsWithinWindow(DateTimeOffset left, DateTimeOffset right) =>
        AbsoluteDelta(left, right) <= _options.CandidateWindow;

    private static bool SameComputer(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameKnownUser(
        string? leftSid,
        string? leftName,
        string? rightSid,
        string? rightName)
    {
        if (!string.IsNullOrWhiteSpace(leftSid) && !string.IsNullOrWhiteSpace(rightSid))
        {
            return string.Equals(leftSid, rightSid, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(leftName)
            && !string.IsNullOrWhiteSpace(rightName)
            && string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        return normalized.Length > 3 ? normalized.TrimEnd('\\') : normalized;
    }

    private static TimeSpan AbsoluteDelta(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration();

    private static List<string> ComputeMissingFields(NormalizedDeleteEvent value)
    {
        var missing = new List<string>();
        AddIfNull(missing, "fullPath", value.FullPath);
        AddIfNull(missing, "processId", value.ProcessId);
        AddIfNull(missing, "processPath", value.ProcessPath);
        AddIfNull(missing, "processGuid", value.ProcessGuid);
        AddIfNull(missing, "commandLine", value.CommandLine);
        if (value.ParentProcessId is null
            && value.ParentProcessPath is null
            && value.ParentProcessGuid is null)
        {
            missing.Add("parentProcess");
        }

        AddIfNull(missing, "userName", value.UserName);
        AddIfNull(missing, "userSid", value.UserSid);
        if (value.DeletePermission == DeletePermissionType.NotObserved)
        {
            missing.Add("deletePermission");
        }

        return missing;
    }

    private static void AddIfNull(List<string> output, string name, object? value)
    {
        if (value is null)
        {
            output.Add(name);
        }
    }

    private sealed record ProcessMatch(
        ProcessContextEvent Context,
        CorrelationMethod Method,
        CorrelationConfidence Confidence,
        TimeSpan Delta,
        bool IsAuthoritative);

    private sealed record SecurityMatch(
        SecurityDeleteEvidence Evidence,
        CorrelationMethod Method,
        CorrelationConfidence Confidence,
        TimeSpan Delta,
        bool IsAuthoritative);

    private sealed record MatchSummary(
        CorrelationMethod Method,
        CorrelationConfidence Confidence,
        TimeSpan Delta);
}
