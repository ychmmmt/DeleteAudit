using System.Globalization;

namespace DeleteAudit.Domain;

/// <summary>
/// Identity for one live-captured record.
/// </summary>
/// <remarks>
/// <para>
/// A live capture has no input file, so it cannot borrow the offline import identity.
/// It is identified by the session that received it plus the position at which it was
/// received:
/// </para>
/// <code>live_evidence_id = live_session_id + ":" + received_sequence</code>
/// <para>
/// Nothing offline is reused: not <c>import_session_id</c>, not an input-file SHA-256,
/// not <c>channel_epoch_id</c>, not the offline ingest sequence or entry hash. The
/// parser's content id and the raw XML digest are stored alongside as corroboration,
/// but neither is a signature, an external anchor, or a tamper-evident chain.
/// </para>
/// </remarks>
public static class LiveEvidenceIdentity
{
    /// <summary>
    /// Builds the evidence id from the session id and its 1-based receive position.
    /// </summary>
    public static string Create(string liveSessionId, long receivedSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveSessionId);
        if (receivedSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(receivedSequence),
                receivedSequence,
                "A received sequence starts at 1 and increases strictly.");
        }

        return string.Concat(
            liveSessionId,
            ":",
            receivedSequence.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The fact that a live capture session started. Written before any watcher exists, so
/// a session that later dies abruptly still leaves a row that explains what was running.
/// </summary>
public sealed record LiveCaptureSessionStart(
    string LiveSessionId,
    DateTimeOffset StartedUtc,
    int QueueCapacity,
    string ApplicationVersion);

/// <summary>
/// One received live record together with the single parse of its XML. Nothing here is
/// inferred: every field is either what the channel reported or what the parser produced.
/// </summary>
public sealed record LiveCaptureRecord(
    string LiveEvidenceId,
    string LiveSessionId,
    long ReceivedSequence,
    long? EventRecordId,
    string? ProviderName,
    string ChannelName,
    string? MachineName,
    DateTimeOffset? TimeCreatedUtc,
    DateTimeOffset ObservedUtc,
    string RawXml,
    byte[] RawXmlSha256,
    string? ParserRawEventId,
    int? ParsedEventId,
    LiveEventOutcome Outcome,
    string? ErrorCode,
    string? Detail);

/// <summary>
/// The fact that a live capture session finished, with the final stable counts. Absence
/// of this row for a started session means the capture did not finish cleanly; it must
/// never be reinterpreted as a normal stop.
/// </summary>
public sealed record LiveCaptureCompletion(
    string LiveSessionId,
    DateTimeOffset StoppedUtc,
    LiveMonitoringState FinalState,
    LiveMonitoringCounters Counters,
    long PersistedRecordCount)
{
    /// <summary>
    /// Records that never entered the queue cannot have been persisted, so the persisted
    /// count can never exceed the classified ones.
    /// </summary>
    public bool IsConsistent =>
        Counters.IsBalanced
        && PersistedRecordCount >= 0
        && PersistedRecordCount <=
           Counters.Parsed + Counters.Ignored + Counters.Error;
}
