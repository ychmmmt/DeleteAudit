using DeleteAudit.Domain;

namespace DeleteAudit.Application.Analysis;

/// <summary>
/// Hard limits for analysing one stored capture session. These are production constants,
/// not tuning knobs: they bound how much evidence a single analysis can pull into memory.
/// </summary>
public static class LiveAnalysisLimits
{
    /// <summary>Maximum records read from one session.</summary>
    public const int MaxAnalyzedRecords = 5_000;

    /// <summary>
    /// Maximum process-context and security-evidence candidates kept while correlating.
    /// The correlator already bounds candidates by time; this bounds them by count too,
    /// so a burst inside one window cannot grow the working set without limit.
    /// </summary>
    public const int MaxCandidatesPerKind = 512;

    /// <summary>
    /// How far outside the correlation window a candidate is still retained. Delivery
    /// order is not event order, so a small grace period keeps a late-delivered context
    /// eligible without widening the correlator's own matching window.
    /// </summary>
    public static readonly TimeSpan CandidateGrace = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Why an analysis covers less than the whole session. Absence of evidence is reported,
/// never silently rounded away.
/// </summary>
public enum LiveAnalysisTruncation
{
    None,
    RecordCapReached
}

/// <summary>
/// One delete observed in a stored capture session, together with the corroboration the
/// existing correlator was able to attach to it.
/// </summary>
/// <remarks>
/// <see cref="LiveEvidenceId"/> and the two matched evidence ids point back at the stored
/// records this was derived from. Nothing here is written back into the evidence tables:
/// this is an interpretation of the capture, not part of it.
/// </remarks>
public sealed record LiveCorrelatedDeleteRow(
    string LiveEvidenceId,
    long ReceivedSequence,
    string DeleteEventId,
    DateTimeOffset OccurredUtc,
    string? FullPath,
    string? ProcessPath,
    string? ProcessGuid,
    int? ProcessId,
    string? UserName,
    string? UserSid,
    CorrelationMethod Method,
    CorrelationConfidence Confidence,
    long? TimeDeltaMilliseconds,
    string? MatchedProcessLiveEvidenceId,
    string? MatchedSecurityLiveEvidenceId,
    int DeleteSessionOrdinal,
    AuditRiskLevel RiskLevel,
    string RiskRuleCode,
    bool ProtectedPathMatched,
    IReadOnlyList<string> Reasons)
{
    public bool IsCorrelated => Method != CorrelationMethod.None;

    /// <summary>
    /// A path/time match is retained as a candidate but never used to fill in identity.
    /// It must be read as "possibly related", not as attribution.
    /// </summary>
    public bool IsHeuristicOnly => Method == CorrelationMethod.PathAndTimeHeuristic;
}

/// <summary>One aggregated delete session derived from a stored capture.</summary>
public sealed record LiveDeleteSessionRow(
    int Ordinal,
    string ProcessIdentity,
    string UserIdentity,
    string MainPath,
    DateTimeOffset OpenedUtc,
    DateTimeOffset LastEventUtc,
    int ConfirmedItemCount,
    int ProtectedItemCount,
    AuditRiskLevel RiskLevel,
    string RiskRuleCode);

/// <summary>The whole derived analysis of one stored capture session.</summary>
public sealed record LiveSessionAnalysis(
    string LiveSessionId,
    int AnalyzedRecordCount,
    int DeleteFactCount,
    int ProcessContextCount,
    int SecurityEvidenceCount,
    int UnparsableRecordCount,
    int UncorrelatedDeleteCount,
    LiveAnalysisTruncation Truncation,
    IReadOnlyList<LiveDeleteSessionRow> DeleteSessions,
    IReadOnlyList<LiveCorrelatedDeleteRow> Deletes)
{
    public static LiveSessionAnalysis Empty(string liveSessionId) =>
        new(liveSessionId, 0, 0, 0, 0, 0, 0, LiveAnalysisTruncation.None, [], []);

    public bool WasTruncated => Truncation != LiveAnalysisTruncation.None;

    public bool HasDeletes => Deletes.Count > 0;
}

/// <summary>
/// Derives correlation, delete-session grouping and risk from evidence a capture session
/// already stored.
/// </summary>
/// <remarks>
/// <para>
/// The analysis is a read-only projection in the ordinary sense of the word: it reads
/// stored records, re-parses their raw XML with the same parser the capture used, and
/// runs the same correlator, aggregator and risk rules the offline import path uses.
/// It writes nothing, and it deliberately produces no new evidence.
/// </para>
/// <para>
/// Risk levels come from the existing deterministic rules only. There is no model, no
/// scoring heuristic of its own, and no claim that a risk level means an attack occurred.
/// </para>
/// </remarks>
public interface ILiveAnalysisService
{
    Task<LiveSessionAnalysis> AnalyzeAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default);
}
