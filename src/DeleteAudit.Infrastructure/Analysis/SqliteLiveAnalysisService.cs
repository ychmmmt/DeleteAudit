using System.Globalization;
using DeleteAudit.Application.Analysis;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Correlation;
using DeleteAudit.Infrastructure.Parsing;
using DeleteAudit.Infrastructure.Sessions;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Analysis;

/// <summary>
/// Derives correlation, delete sessions and risk from one stored capture session.
/// </summary>
/// <remarks>
/// <para>
/// Everything determinate about this class is deliberate. It reuses
/// <see cref="WindowsEventXmlParser"/>, <see cref="DeleteEventCorrelator"/> and
/// <see cref="DeleteSessionAggregator"/> unchanged — there is no second, parallel set of
/// rules that could drift away from the offline import path.
/// </para>
/// <para>
/// The database is opened ReadOnly and nothing is written back. The result is an
/// interpretation of stored evidence; it is never itself stored as evidence.
/// </para>
/// </remarks>
public sealed class SqliteLiveAnalysisService : ILiveAnalysisService
{
    private readonly ViewerDataLocation _location;
    private readonly CorrelationOptions _correlationOptions;
    private readonly AuditRiskOptions _riskOptions;
    private readonly IReadOnlyList<ProtectedPathRule> _protectedRules;
    private readonly TimeProvider _timeProvider;

    public SqliteLiveAnalysisService(ViewerDataLocation location)
        : this(
            location,
            // The same defaults the offline viewer import uses, so a delete analysed here
            // and the same delete imported offline cannot disagree.
            new CorrelationOptions(TimeSpan.FromSeconds(3)),
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100))
    {
    }

    public SqliteLiveAnalysisService(
        ViewerDataLocation location,
        CorrelationOptions correlationOptions,
        AuditRiskOptions riskOptions,
        IEnumerable<ProtectedPathRule>? protectedRules = null,
        TimeProvider? timeProvider = null)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        ArgumentNullException.ThrowIfNull(correlationOptions);
        ArgumentNullException.ThrowIfNull(riskOptions);
        correlationOptions.Validate();
        riskOptions.Validate();
        _correlationOptions = correlationOptions;
        _riskOptions = riskOptions;
        _protectedRules = (protectedRules ?? []).ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LiveSessionAnalysis> AnalyzeAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveSessionId);

        var parsed = await ReadAndParseAsync(liveSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (parsed.Records.Count == 0)
        {
            return LiveSessionAnalysis.Empty(liveSessionId);
        }

        var correlator = new DeleteEventCorrelator(_correlationOptions);
        var aggregator = new DeleteSessionAggregator(
            _riskOptions,
            _protectedRules,
            _timeProvider);
        var sessionOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var sessionRisk = new Dictionary<string, RiskAssessment>(StringComparer.Ordinal);
        var deletes = new List<LiveCorrelatedDeleteRow>();
        var uncorrelated = 0;

        // Records are walked in receive order. For each delete the candidate lists are
        // trimmed to the correlation window plus a small grace, and hard-capped, so the
        // working set stays bounded no matter how long the session ran.
        foreach (var record in parsed.Records.Where(item => item.DeleteEvent is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleteEvent = record.DeleteEvent!;
            var processes = SelectCandidates(
                parsed.Processes,
                deleteEvent.OccurredUtc,
                candidate => candidate.Context.StartedUtc);
            var security = SelectCandidates(
                parsed.Security,
                deleteEvent.OccurredUtc,
                candidate => candidate.Evidence.OccurredUtc);

            var correlation = correlator.Correlate(
                deleteEvent,
                processes.Select(candidate => candidate.Context),
                security.Select(candidate => candidate.Evidence));
            var aggregation = aggregator.Add(correlation.Event);

            var sessionId = aggregation.Session.DeleteSessionId;
            if (!sessionOrdinals.TryGetValue(sessionId, out var ordinal))
            {
                ordinal = sessionOrdinals.Count + 1;
                sessionOrdinals[sessionId] = ordinal;
            }

            // The aggregator reports the assessment for the session as it now stands;
            // the last one for a session is its current state.
            sessionRisk[sessionId] = aggregation.Assessment;

            if (correlation.Method == CorrelationMethod.None)
            {
                uncorrelated++;
            }

            deletes.Add(new LiveCorrelatedDeleteRow(
                record.LiveEvidenceId,
                record.ReceivedSequence,
                correlation.Event.DeleteEventId,
                correlation.Event.OccurredUtc,
                correlation.Event.FullPath,
                correlation.Event.ProcessPath,
                correlation.Event.ProcessGuid,
                correlation.Event.ProcessId,
                correlation.Event.UserName,
                correlation.Event.UserSid,
                correlation.Method,
                correlation.Confidence,
                correlation.TimeDelta is null
                    ? null
                    : (long)Math.Round(
                        correlation.TimeDelta.Value.TotalMilliseconds,
                        MidpointRounding.AwayFromZero),
                FindEvidenceId(processes, correlation.MatchedProcessRawEventId),
                FindEvidenceId(security, correlation.MatchedSecurityRawEventId),
                ordinal,
                aggregation.Assessment.RiskLevel,
                aggregation.Assessment.RuleCode,
                aggregation.Assessment.ProtectedPathMatched,
                correlation.Reasons));
        }

        var sessions = aggregator.Sessions
            .Where(session => sessionOrdinals.ContainsKey(session.DeleteSessionId))
            .Select(session => new LiveDeleteSessionRow(
                sessionOrdinals[session.DeleteSessionId],
                session.ProcessIdentity,
                session.UserIdentity,
                session.MainPath,
                session.OpenedUtc,
                session.LastEventUtc,
                session.ConfirmedItemCount,
                session.ProtectedItemCount,
                session.CurrentRisk,
                sessionRisk[session.DeleteSessionId].RuleCode))
            .OrderBy(session => session.Ordinal)
            .ToArray();

        return new LiveSessionAnalysis(
            liveSessionId,
            parsed.Records.Count,
            deletes.Count,
            parsed.Processes.Count,
            parsed.Security.Count,
            parsed.UnparsableCount,
            uncorrelated,
            parsed.Truncation,
            sessions,
            deletes);
    }

    /// <summary>
    /// Keeps candidates whose own time is within the correlation window (plus a grace
    /// period for out-of-order delivery) of the delete, newest first, hard-capped.
    /// </summary>
    private List<T> SelectCandidates<T>(
        IReadOnlyList<T> candidates,
        DateTimeOffset occurredUtc,
        Func<T, DateTimeOffset> timeOf)
    {
        var limit = _correlationOptions.CandidateWindow + LiveAnalysisLimits.CandidateGrace;
        return candidates
            .Where(candidate => (occurredUtc - timeOf(candidate)).Duration() <= limit)
            .OrderBy(candidate => (occurredUtc - timeOf(candidate)).Duration())
            .Take(LiveAnalysisLimits.MaxCandidatesPerKind)
            .ToList();
    }

    private static string? FindEvidenceId(
        IEnumerable<ProcessCandidate> candidates,
        string? rawEventId) =>
        rawEventId is null
            ? null
            : candidates
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Context.RawEventId,
                        rawEventId,
                        StringComparison.Ordinal))
                ?.LiveEvidenceId;

    private static string? FindEvidenceId(
        IEnumerable<SecurityCandidate> candidates,
        string? rawEventId) =>
        rawEventId is null
            ? null
            : candidates
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Evidence.RawEventId,
                        rawEventId,
                        StringComparison.Ordinal))
                ?.LiveEvidenceId;

    private async Task<ParsedSession> ReadAndParseAsync(
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        var parser = new WindowsEventXmlParser(_timeProvider);
        var records = new List<ParsedRecord>();
        var processes = new List<ProcessCandidate>();
        var security = new List<SecurityCandidate>();
        var unparsable = 0;
        var truncation = LiveAnalysisTruncation.None;

        await using var connection = _location.CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // One extra row is requested so a full result set can be reported as truncated
        // rather than silently presented as complete.
        command.CommandText = """
            SELECT live_evidence_id, received_sequence, raw_xml
            FROM live_capture_records
            WHERE live_session_id = $session
              AND outcome IN ('delete_fact', 'process_context', 'security_evidence')
            ORDER BY received_sequence ASC
            LIMIT $limit;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        command.Parameters.Add("$limit", SqliteType.Integer).Value =
            LiveAnalysisLimits.MaxAnalyzedRecords + 1;

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (records.Count >= LiveAnalysisLimits.MaxAnalyzedRecords)
            {
                truncation = LiveAnalysisTruncation.RecordCapReached;
                break;
            }

            var evidenceId = reader.GetString(0);
            var sequence = reader.GetInt64(1);
            var result = parser.Parse(reader.GetString(2));
            if (result.Error is not null)
            {
                // A record the capture classified as usable but that no longer parses is
                // counted, never guessed at.
                unparsable++;
                continue;
            }

            if (result.ProcessContext is not null)
            {
                processes.Add(new ProcessCandidate(evidenceId, result.ProcessContext));
            }

            if (result.SecurityEvidence is not null)
            {
                security.Add(new SecurityCandidate(evidenceId, result.SecurityEvidence));
            }

            records.Add(new ParsedRecord(evidenceId, sequence, result.DeleteEvent));
        }

        return new ParsedSession(records, processes, security, unparsable, truncation);
    }

    private sealed record ParsedRecord(
        string LiveEvidenceId,
        long ReceivedSequence,
        NormalizedDeleteEvent? DeleteEvent);

    private sealed record ProcessCandidate(
        string LiveEvidenceId,
        ProcessContextEvent Context);

    private sealed record SecurityCandidate(
        string LiveEvidenceId,
        SecurityDeleteEvidence Evidence);

    private sealed record ParsedSession(
        IReadOnlyList<ParsedRecord> Records,
        IReadOnlyList<ProcessCandidate> Processes,
        IReadOnlyList<SecurityCandidate> Security,
        int UnparsableCount,
        LiveAnalysisTruncation Truncation);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
