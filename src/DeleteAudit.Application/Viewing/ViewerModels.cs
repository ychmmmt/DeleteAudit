using DeleteAudit.Domain;

namespace DeleteAudit.Application.Viewing;

public enum ViewerDatabaseState
{
    Ready,
    MissingDatabase,
    MissingSchema,
    Inaccessible
}

public sealed record ViewerDatabaseStatus(
    ViewerDatabaseState State,
    string Message,
    IReadOnlyList<string> MissingObjects)
{
    public bool IsReady => State == ViewerDatabaseState.Ready;
}

public sealed record PageRequest(int Offset = 0, int Limit = 50)
{
    public const int MaximumLimit = 200;

    public void Validate()
    {
        if (Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Offset));
        }

        if (Limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit));
        }
    }
}

public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    long TotalCount,
    int Offset,
    int Limit)
{
    public bool HasPrevious => Offset > 0;

    public bool HasNext => Offset + Items.Count < TotalCount;
}

public sealed record AuditQuery(
    AuditRiskLevel? Risk,
    DateTimeOffset? FromUtcInclusive,
    DateTimeOffset? ToUtcExclusive,
    string? PathContains,
    string? ProcessContains,
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

public sealed record ImportHistoryQuery(
    string? Status,
    DateTimeOffset? FromUtcInclusive,
    DateTimeOffset? ToUtcExclusive,
    string? SourcePathContains,
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

public sealed record DiagnosticQuery(
    ImportDiagnosticSeverity? Severity,
    DateTimeOffset? FromUtcInclusive,
    DateTimeOffset? ToUtcExclusive,
    string? TextContains,
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

public sealed record DashboardSummary(
    long ImportCount,
    long DeleteSessionCount,
    long DeleteEventCount,
    long WarningSessionCount,
    long CriticalSessionCount,
    long WarningDiagnosticCount,
    long ErrorDiagnosticCount,
    DateTimeOffset? LastImportStartedUtc);

public sealed record ImportHistoryRow(
    string ImportSessionId,
    string SourceKind,
    string OriginalFileName,
    string NormalizedSourcePath,
    long FileSizeBytes,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    int TotalRecordCount,
    int SuccessRecordCount,
    int IgnoredRecordCount,
    int ErrorRecordCount,
    string ApplicationVersion,
    int SchemaVersion,
    string Status,
    string? OutputStatus,
    string? OutputErrorCode,
    string? OutputErrorMessage);

public sealed record DeleteSessionRow(
    string DeleteSessionId,
    DateTimeOffset OpenedUtc,
    DateTimeOffset LastEventUtc,
    DateTimeOffset? SealedUtc,
    string? ProcessIdentity,
    int? ProcessId,
    string? ProcessGuid,
    string? UserSid,
    string? PathScope,
    int ConfirmedItemCount,
    int ProtectedItemCount,
    AuditRiskLevel RiskLevel);

public sealed record DeleteEventRow(
    string DeleteEventId,
    DateTimeOffset OccurredUtc,
    string? OccurredLocal,
    int SourceEventId,
    string? FullPath,
    string? ObjectKind,
    int? ProcessId,
    string? ProcessPath,
    string? ProcessGuid,
    string? UserName,
    string? UserSid,
    string DeleteSessionId,
    AuditRiskLevel RiskLevel,
    int AttributionConfidence,
    string? MissingFieldsJson);

public sealed record DiagnosticRow(
    string DiagnosticId,
    string ImportSessionId,
    long? RecordOrdinal,
    string OriginalFileName,
    string Stage,
    ImportDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? DetailsJson,
    DateTimeOffset OccurredUtc);

public sealed class RawXmlDocument
{
    public const int MaxPreviewCharacters = 262_144;

    private RawXmlDocument(
        string resourceId,
        string? previewText,
        long originalLength,
        bool isAvailable,
        string? unavailableReason)
    {
        ResourceId = resourceId;
        PreviewText = previewText;
        OriginalLength = originalLength;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
    }

    public static RawXmlDocument CreatePreview(
        string resourceId,
        string previewText,
        long originalLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(previewText);
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        return new RawXmlDocument(resourceId, previewText, originalLength, true, null);
    }

    public static RawXmlDocument CreateUnavailable(
        string resourceId,
        string unavailableReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unavailableReason);
        return new RawXmlDocument(resourceId, null, 0, false, unavailableReason);
    }

    public string ResourceId { get; }

    public string? PreviewText { get; }

    public long OriginalLength { get; }

    public int PreviewLength => PreviewText?.Length ?? 0;

    public int PreviewLimit { get; } = MaxPreviewCharacters;

    public bool IsTruncated => IsAvailable && OriginalLength > PreviewLimit;

    public bool IsAvailable { get; }

    public string? UnavailableReason { get; }

    public bool IsReadOnly { get; } = true;
}

public static class ViewerDisplay
{
    public const string Unknown = "未知";

    public static string Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unknown : value;

    public static string Value(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Unknown;
}
