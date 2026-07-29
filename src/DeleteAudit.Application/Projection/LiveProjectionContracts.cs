using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Projection;

/// <summary>
/// Whether the live-owned projection tables are usable. Reported separately from every
/// other readiness check, so a database without this migration keeps all existing pages
/// working and only the projection capability reports itself unavailable.
/// </summary>
public enum LiveProjectionState
{
    Ready,
    MissingDatabase,
    MissingSchema,
    Inaccessible
}

public sealed record LiveProjectionAvailability(
    LiveProjectionState State,
    string Message,
    IReadOnlyList<string> MissingObjects)
{
    public bool IsReady => State == LiveProjectionState.Ready;
}

/// <summary>The outcome of one projection run over one capture session.</summary>
public sealed record LiveProjectionRunResult(
    string LiveSessionId,
    long ConsideredCount,
    long ProjectedCount,
    long SkippedCount,
    bool Succeeded,
    string? FailureCode,
    string? FailureDetail)
{
    /// <summary>Everything eligible was already projected by an earlier run.</summary>
    public bool WasAlreadyComplete =>
        Succeeded && ProjectedCount == 0 && SkippedCount == ConsideredCount;
}

/// <summary>
/// The result of re-deriving a session's continuity chain from stored fields.
/// </summary>
/// <remarks>
/// A continuous chain means the projected records are in an unbroken recorded order and
/// none of the values the chain covers has changed since it was written. It does not mean
/// the database is tamper-proof: a writer who can reach the file can rebuild a whole
/// chain consistently. This is an accidental-modification and ordering aid only.
/// </remarks>
public sealed record LiveContinuityStatus(
    string LiveSessionId,
    long ProjectedCount,
    bool IsContinuous,
    long? FirstBrokenSequence,
    string? FirstBrokenLiveEvidenceId,
    string? Detail);

/// <summary>One canonically projected record, with its source always visible.</summary>
public sealed record LiveProjectedRecordRow(
    string LiveProjectionId,
    string LiveEvidenceId,
    string LiveSessionId,
    string LiveChannelEpochId,
    long SourceReceivedSequence,
    long LiveIngestSequence,
    long? EventRecordId,
    string? ProviderName,
    string ChannelName,
    string? MachineName,
    DateTimeOffset? EventUtc,
    DateTimeOffset ObservedUtc,
    string Source,
    string? NormalizedPath,
    string? ObjectKind,
    int? ProcessId,
    string? ProcessPath,
    string? ProcessGuid,
    string? CommandLine,
    int? ParentProcessId,
    string? ParentProcessPath,
    string? ParentProcessGuid,
    string? UserName,
    string? UserSid,
    string? DeletePermission,
    bool? ArchiveExpected,
    string MissingFieldsJson,
    string RawXmlSha256,
    string CanonicalPayloadSha256,
    string EntryHash,
    string? PreviousEntryHash,
    DateTimeOffset ProjectedUtc)
{
    /// <summary>
    /// Always "live capture". A read model that mixes this with offline results must keep
    /// this distinction and the evidence id visible rather than hide where a row came from.
    /// </summary>
    public string Origin { get; } = "live_capture";

    public string SourceLabel => LiveProjectionSources.Label(Source);
}

public sealed record LiveProjectionQuery(
    string LiveSessionId,
    string? Source,
    string? PathContains,
    string? ProcessContains,
    bool Descending,
    PageRequest Page)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LiveSessionId);
        Page.Validate();
        if (Source is not null && !LiveProjectionSources.All.Contains(Source))
        {
            throw new ArgumentException(
                $"'{Source}' is not a live projection source.",
                nameof(Source));
        }
    }
}

public static class LiveProjectionSources
{
    public const string SysmonDelete = "sysmon_delete";
    public const string SysmonProcess = "sysmon_process";
    public const string Security4663 = "security_4663";

    public static IReadOnlyList<string> All { get; } =
    [
        SysmonDelete,
        SysmonProcess,
        Security4663
    ];

    public static string Label(string source) => source switch
    {
        SysmonDelete => "Sysmon 删除事实",
        SysmonProcess => "Sysmon 进程上下文",
        Security4663 => "Security 4663 补强",
        _ => ViewerDisplay.Unknown
    };
}

/// <summary>
/// Normalises live-captured evidence into a canonical shape that belongs entirely to the
/// live path.
/// </summary>
/// <remarks>
/// <para>
/// This writes only to the tables introduced by the Phase 2B.4 migration. It never writes
/// to, extends or reuses <c>raw_events</c>, <c>delete_events</c>, <c>delete_sessions</c>,
/// <c>channel_epochs</c>, the offline ingest sequence or the offline hash chain, and it
/// fabricates no offline identity. The decisions recorded in the 0003 and 0004 migrations
/// stand unchanged.
/// </para>
/// <para>
/// Captured evidence is never modified by projection: a projection failure cannot damage
/// what a capture already committed, because projection does not write to those tables at
/// all.
/// </para>
/// </remarks>
public interface ILiveProjectionService
{
    Task<LiveProjectionAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Projects everything in one capture session that is not projected yet. Running it
    /// again projects nothing further: identity is derived from the evidence, so a replay
    /// can only collide with itself.
    /// </summary>
    Task<LiveProjectionRunResult> ProjectSessionAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Re-derives the session's chain from stored fields and reports breaks.</summary>
    Task<LiveContinuityStatus> VerifyContinuityAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default);

    Task<PageResult<LiveProjectedRecordRow>> GetProjectedRecordsAsync(
        LiveProjectionQuery query,
        CancellationToken cancellationToken = default);
}
