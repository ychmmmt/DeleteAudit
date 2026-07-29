using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Parsing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Projection;

internal static class LiveProjectionCanonicalizer
{
    internal static CanonicalizedRecord Canonicalize(SourceRecord record)
    {
        var expectedEvidenceId = LiveEvidenceIdentity.Create(
            record.LiveSessionId,
            record.ReceivedSequence);
        if (!string.Equals(
                record.LiveEvidenceId,
                expectedEvidenceId,
                StringComparison.Ordinal))
        {
            throw InvalidSource(
                "live_evidence_identity_mismatch",
                "live evidence id 与会话及接收序号不一致。");
        }

        var rawDigest = SHA256.HashData(Encoding.UTF8.GetBytes(record.RawXml));
        if (!CryptographicOperations.FixedTimeEquals(record.RawXmlSha256, rawDigest))
        {
            throw InvalidSource(
                "source_digest_mismatch",
                "live evidence 的 raw XML digest 与内容不一致。");
        }

        if (!TryParseOutcome(record.Outcome, out var outcome)
            || !LiveProjectionIdentity.TryGetSource(outcome, out var source))
        {
            throw InvalidSource(
                "source_outcome_not_projectable",
                "live evidence outcome 不是可投影的删除、进程或 Security 补强证据。");
        }

        var parsed = new WindowsEventXmlParser().Parse(record.RawXml);
        if (parsed.Error is not null || parsed.RawEvent is null)
        {
            throw InvalidSource(
                "source_reparse_failed",
                parsed.Error?.Message ?? "live evidence 无法重新解析。");
        }

        if (!string.Equals(
                record.ParserRawEventId,
                parsed.RawEvent.RawEventId,
                StringComparison.Ordinal)
            || record.ParsedEventId != parsed.RawEvent.EventId)
        {
            throw InvalidSource(
                "source_parser_identity_mismatch",
                "capture 保存的 parser identity 与重新解析结果不一致。");
        }

        LiveProjectionPayload payload = outcome switch
        {
            LiveEventOutcome.DeleteFact when parsed.DeleteEvent is not null =>
                FromDelete(record, source, parsed.DeleteEvent),
            LiveEventOutcome.ProcessContext when parsed.ProcessContext is not null =>
                FromProcess(record, source, parsed.ProcessContext),
            LiveEventOutcome.SecurityEvidence when parsed.SecurityEvidence is not null =>
                FromSecurity(record, source, parsed.SecurityEvidence),
            _ => throw InvalidSource(
                "source_classification_mismatch",
                "capture outcome 与重新解析得到的事件分类不一致。")
        };

        return new CanonicalizedRecord(
            payload,
            LiveProjectionIdentity.CreateEpochId(
                record.LiveSessionId,
                payload.ChannelName,
                payload.MachineName),
            rawDigest,
            SerializeMissing(payload.MissingFields));
    }

    internal static SourceRecord ReadSourceRecord(SqliteDataReader reader, int offset) =>
        new(
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetInt64(offset + 2),
            GetNullableInt64(reader, offset + 3),
            GetNullableString(reader, offset + 4),
            reader.GetString(offset + 5),
            GetNullableString(reader, offset + 6),
            GetNullableTimestamp(reader, offset + 7),
            ParseTimestamp(reader.GetString(offset + 8)),
            reader.GetString(offset + 9),
            (byte[])reader.GetValue(offset + 10),
            GetNullableString(reader, offset + 11),
            GetNullableInt32(reader, offset + 12),
            reader.GetString(offset + 13));

    internal static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new FormatException("Stored live timestamp is not canonical ISO-8601.");
        }

        return timestamp.ToUniversalTime();
    }

    internal static DateTimeOffset? GetNullableTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseTimestamp(reader.GetString(ordinal));

    internal static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    internal static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    internal static bool? GetNullableBoolean(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;

    internal static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static bool NullableEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    internal static object ToDb(string? value) =>
        value is null ? DBNull.Value : value;

    internal static object ToDb(int? value) =>
        value is null ? DBNull.Value : value.Value;

    internal static object ToDb(long? value) =>
        value is null ? DBNull.Value : value.Value;

    internal static ProjectionFailureException InvalidSource(
        string code,
        string detail) =>
        new(code, Bound(detail, 2_048), 0, 0);

    internal static string Bound(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static LiveProjectionPayload FromDelete(
        SourceRecord record,
        string source,
        NormalizedDeleteEvent deleteEvent) =>
        new(
            source,
            record.EventRecordId,
            record.ProviderName,
            record.ChannelName,
            record.MachineName,
            record.TimeCreatedUtc,
            record.ObservedUtc,
            record.ParserRawEventId,
            record.ParsedEventId,
            deleteEvent.FullPath,
            ToStorageObjectKind(deleteEvent.ObjectKind),
            deleteEvent.ProcessId,
            deleteEvent.ProcessPath,
            deleteEvent.ProcessGuid,
            deleteEvent.CommandLine,
            deleteEvent.ParentProcessId,
            deleteEvent.ParentProcessPath,
            deleteEvent.ParentProcessGuid,
            deleteEvent.UserName,
            deleteEvent.UserSid,
            ToStoragePermission(deleteEvent.DeletePermission),
            deleteEvent.ArchiveExpected,
            deleteEvent.MissingFields);

    private static LiveProjectionPayload FromProcess(
        SourceRecord record,
        string source,
        ProcessContextEvent process) =>
        new(
            source,
            record.EventRecordId,
            record.ProviderName,
            record.ChannelName,
            record.MachineName,
            record.TimeCreatedUtc,
            record.ObservedUtc,
            record.ParserRawEventId,
            record.ParsedEventId,
            null,
            null,
            process.ProcessId,
            process.ProcessPath,
            process.ProcessGuid,
            process.CommandLine,
            process.ParentProcessId,
            process.ParentProcessPath,
            process.ParentProcessGuid,
            process.UserName,
            process.UserSid,
            null,
            null,
            process.MissingFields);

    private static LiveProjectionPayload FromSecurity(
        SourceRecord record,
        string source,
        SecurityDeleteEvidence evidence) =>
        new(
            source,
            record.EventRecordId,
            record.ProviderName,
            record.ChannelName,
            record.MachineName,
            record.TimeCreatedUtc,
            record.ObservedUtc,
            record.ParserRawEventId,
            record.ParsedEventId,
            evidence.ObjectPath,
            null,
            evidence.ProcessId,
            evidence.ProcessPath,
            null,
            null,
            null,
            null,
            null,
            evidence.UserName,
            evidence.UserSid,
            ToStoragePermission(evidence.DeletePermission),
            null,
            evidence.MissingFields);

    private static string SerializeMissing(IEnumerable<string> missingFields) =>
        JsonSerializer.Serialize(
            missingFields.OrderBy(field => field, StringComparer.Ordinal).ToArray());

    private static string ToStorageObjectKind(AuditObjectKind kind) => kind switch
    {
        AuditObjectKind.Unknown => "unknown",
        AuditObjectKind.File => "file",
        AuditObjectKind.Directory => "directory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string ToStoragePermission(DeletePermissionType permission) =>
        permission switch
        {
            DeletePermissionType.NotObserved => "not_observed",
            DeletePermissionType.Delete => "delete",
            DeletePermissionType.DeleteChild => "delete_child",
            DeletePermissionType.DeleteAndDeleteChild => "delete_and_delete_child",
            _ => throw new ArgumentOutOfRangeException(
                nameof(permission),
                permission,
                null)
        };

    private static bool TryParseOutcome(string value, out LiveEventOutcome outcome)
    {
        outcome = value switch
        {
            "delete_fact" => LiveEventOutcome.DeleteFact,
            "process_context" => LiveEventOutcome.ProcessContext,
            "security_evidence" => LiveEventOutcome.SecurityEvidence,
            _ => LiveEventOutcome.Error
        };
        return value is "delete_fact" or "process_context" or "security_evidence";
    }
}

internal sealed record SourceRecord(
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
    string Outcome);

internal sealed record CanonicalizedRecord(
    LiveProjectionPayload Payload,
    string EpochId,
    byte[] RawXmlSha256,
    string MissingFieldsJson);

internal sealed class ProjectionFailureException(
    string code,
    string message,
    long consideredCount,
    long skippedCount)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public long ConsideredCount { get; } = consideredCount;

    public long SkippedCount { get; } = skippedCount;
}
