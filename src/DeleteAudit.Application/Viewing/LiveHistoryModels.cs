using DeleteAudit.Domain;

namespace DeleteAudit.Application.Viewing;

/// <summary>
/// Whether the live capture history tables are usable. This is deliberately separate
/// from <see cref="ViewerDatabaseStatus"/>: a database that predates the live evidence
/// migration must leave every existing page working and fail closed only here.
/// </summary>
public enum LiveHistoryState
{
    Ready,
    MissingDatabase,
    MissingSchema,
    Inaccessible
}

public sealed record LiveHistoryAvailability(
    LiveHistoryState State,
    string Message,
    IReadOnlyList<string> MissingObjects)
{
    public bool IsReady => State == LiveHistoryState.Ready;
}

/// <summary>
/// One live capture session. A session with no completion row did not finish cleanly;
/// <see cref="FinalState"/> is null in that case and must never be read as "stopped".
/// </summary>
public sealed record LiveCaptureSessionRow(
    string LiveSessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? StoppedUtc,
    string? FinalState,
    string ApplicationVersion,
    long QueueCapacity,
    long ReceivedCount,
    long DeleteFactCount,
    long ProcessContextCount,
    long SecurityEvidenceCount,
    long IgnoredCount,
    long ErrorCount,
    long DroppedCount,
    long LateDiscardedCount,
    long SuppressedDiagnosticCount,
    long PersistedRecordCount,
    long StoredRecordCount)
{
    /// <summary>A completion row exists, so the capture ended through its own shutdown.</summary>
    public bool IsComplete => FinalState is not null;

    /// <summary>
    /// Records that were classified but are not in the database. Only meaningful for a
    /// completed session; an incomplete one has no trustworthy total to compare against.
    /// </summary>
    public long UncommittedCount =>
        IsComplete
            ? Math.Max(
                0,
                DeleteFactCount + ProcessContextCount + SecurityEvidenceCount
                    + IgnoredCount + ErrorCount - PersistedRecordCount)
            : 0;
}

/// <summary>
/// One received live record. The raw XML itself is never carried here — only its length
/// and digest — so a list page can never materialise a megabyte of evidence per row.
/// </summary>
public sealed record LiveCaptureRecordRow(
    string LiveEvidenceId,
    string LiveSessionId,
    long ReceivedSequence,
    long? EventRecordId,
    string? ProviderName,
    string ChannelName,
    string? MachineName,
    DateTimeOffset? TimeCreatedUtc,
    DateTimeOffset ObservedUtc,
    string RawXmlSha256,
    long RawXmlLength,
    string? ParserRawEventId,
    int? ParsedEventId,
    string Outcome,
    string? ErrorCode,
    string? Detail)
{
    /// <summary>
    /// Only <c>delete_fact</c> is an observed delete. Process context and security
    /// evidence are corroboration and must never be presented as deletions.
    /// </summary>
    public bool EstablishesDeleteFact =>
        string.Equals(Outcome, "delete_fact", StringComparison.Ordinal);
}

public sealed record LiveCaptureDiagnosticRow(
    string LiveDiagnosticId,
    string LiveSessionId,
    string Stage,
    ImportDiagnosticSeverity Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredUtc);

/// <summary>Which capture sessions to list.</summary>
public sealed record LiveHistorySessionQuery(
    DateTimeOffset? FromUtcInclusive,
    DateTimeOffset? ToUtcExclusive,
    LiveHistorySessionState? State,
    PageRequest Page)
{
    public void Validate()
    {
        Page.Validate();
        if (FromUtcInclusive is not null
            && ToUtcExclusive is not null
            && FromUtcInclusive >= ToUtcExclusive)
        {
            throw new ArgumentException("The start time must be earlier than the end time.");
        }
    }
}

public enum LiveHistorySessionState
{
    Stopped,
    Error,
    Incomplete
}

/// <summary>Which records of one capture session to list.</summary>
public sealed record LiveHistoryRecordQuery(
    string LiveSessionId,
    DateTimeOffset? FromUtcInclusive,
    DateTimeOffset? ToUtcExclusive,
    string? Outcome,
    string? ProviderContains,
    string? ChannelContains,
    int? ParsedEventId,
    long? EventRecordId,
    string? ErrorCodeContains,
    bool ErrorsOnly,
    long? MinReceivedSequence,
    long? MaxReceivedSequence,
    bool Descending,
    PageRequest Page)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LiveSessionId);
        Page.Validate();
        if (FromUtcInclusive is not null
            && ToUtcExclusive is not null
            && FromUtcInclusive >= ToUtcExclusive)
        {
            throw new ArgumentException("The start time must be earlier than the end time.");
        }

        if (MinReceivedSequence is not null
            && MaxReceivedSequence is not null
            && MinReceivedSequence > MaxReceivedSequence)
        {
            throw new ArgumentException(
                "The smallest received sequence must not exceed the largest.");
        }

        if (Outcome is not null && !LiveCaptureOutcomes.All.Contains(Outcome))
        {
            throw new ArgumentException(
                $"'{Outcome}' is not a live capture outcome.",
                nameof(Outcome));
        }
    }
}

/// <summary>
/// The five stored outcomes, exactly as <c>0004_phase_2b_live_evidence.sql</c> constrains
/// them. Filters are matched against this list rather than passed through as free text.
/// </summary>
public static class LiveCaptureOutcomes
{
    public const string DeleteFact = "delete_fact";
    public const string ProcessContext = "process_context";
    public const string SecurityEvidence = "security_evidence";
    public const string Ignored = "ignored";
    public const string Error = "error";

    public static IReadOnlyList<string> All { get; } =
    [
        DeleteFact,
        ProcessContext,
        SecurityEvidence,
        Ignored,
        Error
    ];

    public static string Label(string outcome) => outcome switch
    {
        DeleteFact => "删除事实",
        ProcessContext => "进程上下文",
        SecurityEvidence => "安全补强",
        Ignored => "已忽略",
        Error => "错误",
        _ => ViewerDisplay.Unknown
    };
}
