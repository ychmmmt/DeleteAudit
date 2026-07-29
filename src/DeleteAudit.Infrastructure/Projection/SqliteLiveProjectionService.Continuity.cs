using System.Security.Cryptography;
using DeleteAudit.Application.Projection;
using DeleteAudit.Domain;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Projection;

public sealed partial class SqliteLiveProjectionService
{
    private static async Task<LiveContinuityStatus> VerifyContinuityCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        var epochs = await ReadEpochsAsync(
                connection,
                transaction,
                liveSessionId,
                cancellationToken)
            .ConfigureAwait(false);
        var seenEpochs = new HashSet<string>(StringComparer.Ordinal);
        var expectedSequence = 1L;
        byte[] previousHash = LiveProjectionIdentity.ChainAnchor.ToArray();
        string? priorEvidenceId = null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                p.live_projection_id,
                p.live_evidence_id,
                p.live_channel_epoch_id,
                p.source_received_sequence,
                p.live_ingest_sequence,
                p.event_record_id,
                p.provider_name,
                p.channel_name,
                p.machine_name,
                p.event_utc,
                p.observed_utc,
                p.source,
                p.parser_raw_event_id,
                p.parsed_event_id,
                p.normalized_path,
                p.object_kind,
                p.process_id,
                p.process_path,
                p.process_guid,
                p.command_line,
                p.parent_process_id,
                p.parent_process_path,
                p.parent_process_guid,
                p.user_name,
                p.user_sid,
                p.delete_permission,
                p.archive_expected,
                p.missing_fields_json,
                p.raw_xml_sha256,
                p.canonical_payload_sha256,
                p.previous_entry_hash,
                p.entry_hash,
                c.live_evidence_id,
                c.live_session_id,
                c.received_sequence,
                c.event_record_id,
                c.provider_name,
                c.channel_name,
                c.machine_name,
                c.time_created_utc,
                c.observed_utc,
                c.raw_xml,
                c.raw_xml_sha256,
                c.parser_raw_event_id,
                c.parsed_event_id,
                c.outcome
            FROM live_projected_records AS p
            LEFT JOIN live_capture_records AS c
              ON c.live_evidence_id = p.live_evidence_id
            WHERE p.live_session_id = $session
            ORDER BY p.live_ingest_sequence ASC, p.live_projection_id ASC;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stored = ReadStoredProjection(reader);
            if (stored.LiveIngestSequence != expectedSequence)
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    $"live ingest sequence 应为 {expectedSequence}，实际为 "
                    + $"{stored.LiveIngestSequence}。");
            }

            if (reader.IsDBNull(32))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "投影引用的 live evidence 不存在。");
            }

            var sourceRecord = LiveProjectionCanonicalizer.ReadSourceRecord(reader, 32);
            var expectedEvidenceId = LiveEvidenceIdentity.Create(
                liveSessionId,
                sourceRecord.ReceivedSequence);
            if (!string.Equals(
                    stored.LiveEvidenceId,
                    expectedEvidenceId,
                    StringComparison.Ordinal)
                || stored.SourceReceivedSequence != sourceRecord.ReceivedSequence
                || !string.Equals(
                    sourceRecord.LiveSessionId,
                    liveSessionId,
                    StringComparison.Ordinal))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "投影来源身份或接收序号与 live evidence 不一致。");
            }

            CanonicalizedRecord canonical;
            try
            {
                canonical = LiveProjectionCanonicalizer.Canonicalize(sourceRecord);
            }
            catch (ProjectionFailureException exception)
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    exception.Message);
            }

            if (!string.Equals(
                    stored.LiveProjectionId,
                    LiveProjectionIdentity.CreateProjectionId(stored.LiveEvidenceId),
                    StringComparison.Ordinal)
                || !string.Equals(
                    stored.LiveChannelEpochId,
                    canonical.EpochId,
                    StringComparison.Ordinal))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "deterministic projection 或 epoch identity 不匹配。");
            }

            if (!StoredPayloadMatches(stored, canonical))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "规范投影字段与重新解析的 live evidence 不一致。");
            }

            var expectedPayloadHash = LiveProjectionIdentity.ComputePayloadHash(
                canonical.Payload);
            if (!CryptographicOperations.FixedTimeEquals(
                    stored.CanonicalPayloadSha256,
                    expectedPayloadHash)
                || !CryptographicOperations.FixedTimeEquals(
                    stored.RawXmlSha256,
                    canonical.RawXmlSha256))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "raw XML 或 canonical payload digest 不匹配。");
            }

            var expectedPrevious = expectedSequence == 1 ? null : previousHash;
            if (!NullableHashEquals(stored.PreviousEntryHash, expectedPrevious))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "previous entry hash 未指向前一投影记录。");
            }

            var expectedEntryHash = LiveProjectionIdentity.ComputeEntryHash(
                previousHash,
                stored.LiveEvidenceId,
                stored.LiveChannelEpochId,
                expectedSequence,
                canonical.RawXmlSha256,
                expectedPayloadHash);
            if (!CryptographicOperations.FixedTimeEquals(
                    stored.EntryHash,
                    expectedEntryHash))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "entry hash 与重算结果不一致。");
            }

            if (!epochs.TryGetValue(stored.LiveChannelEpochId, out var epoch))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "投影引用的 live channel epoch 不存在。");
            }

            if (!EpochIdentityMatches(epoch, liveSessionId, canonical.Payload))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "live channel epoch 元数据与投影来源不一致。");
            }

            if (seenEpochs.Add(epoch.Id)
                && (!LiveProjectionCanonicalizer.NullableEquals(
                        epoch.ProviderName,
                        canonical.Payload.ProviderName)
                    || epoch.FirstReceivedSequence != sourceRecord.ReceivedSequence
                    || epoch.OpenedUtc !=
                       (canonical.Payload.EventUtc ?? canonical.Payload.ObservedUtc)))
            {
                return Broken(
                    liveSessionId,
                    expectedSequence - 1,
                    expectedSequence,
                    stored.LiveEvidenceId,
                    "live channel epoch 的首记录元数据不一致。");
            }

            previousHash = stored.EntryHash;
            priorEvidenceId = stored.LiveEvidenceId;
            expectedSequence++;
        }

        var projectedCount = expectedSequence - 1;
        var successfulHighWater = await ReadSuccessfulProjectionHighWaterAsync(
                connection,
                transaction,
                liveSessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (successfulHighWater > projectedCount)
        {
            return Broken(
                liveSessionId,
                projectedCount,
                projectedCount + 1,
                priorEvidenceId,
                $"已有成功投影曾覆盖 {successfulHighWater} 条记录，当前仅剩 "
                + $"{projectedCount} 条；检测到尾部截断。");
        }

        if (epochs.Count != seenEpochs.Count)
        {
            return Broken(
                liveSessionId,
                expectedSequence - 1,
                expectedSequence,
                priorEvidenceId,
                "会话包含没有被任何投影记录引用的 live channel epoch。");
        }

        return new LiveContinuityStatus(
            liveSessionId,
            expectedSequence - 1,
            true,
            null,
            null,
            expectedSequence == 1
                ? "该会话尚无规范投影记录。"
                : "live-owned continuity chain 连续；这仅辅助检测顺序或意外修改，"
                  + "不代表 SQLite 防篡改。");
    }

    private static bool StoredPayloadMatches(
        StoredProjection stored,
        CanonicalizedRecord canonical)
    {
        var payload = canonical.Payload;
        return stored.EventRecordId == payload.EventRecordId
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ProviderName,
                payload.ProviderName)
            && string.Equals(
                stored.ChannelName,
                payload.ChannelName,
                StringComparison.Ordinal)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.MachineName,
                payload.MachineName)
            && stored.EventUtc == payload.EventUtc
            && stored.ObservedUtc == payload.ObservedUtc
            && string.Equals(stored.Source, payload.Source, StringComparison.Ordinal)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ParserRawEventId,
                payload.ParserRawEventId)
            && stored.ParsedEventId == payload.ParsedEventId
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.NormalizedPath,
                payload.NormalizedPath)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ObjectKind,
                payload.ObjectKind)
            && stored.ProcessId == payload.ProcessId
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ProcessPath,
                payload.ProcessPath)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ProcessGuid,
                payload.ProcessGuid)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.CommandLine,
                payload.CommandLine)
            && stored.ParentProcessId == payload.ParentProcessId
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ParentProcessPath,
                payload.ParentProcessPath)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.ParentProcessGuid,
                payload.ParentProcessGuid)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.UserName,
                payload.UserName)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.UserSid,
                payload.UserSid)
            && LiveProjectionCanonicalizer.NullableEquals(
                stored.DeletePermission,
                payload.DeletePermission)
            && stored.ArchiveExpected == payload.ArchiveExpected
            && string.Equals(
                stored.MissingFieldsJson,
                canonical.MissingFieldsJson,
                StringComparison.Ordinal);
    }

    private static async Task<Dictionary<string, EpochRecord>> ReadEpochsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                live_channel_epoch_id, live_session_id, channel_name, machine_name,
                provider_name, opened_utc, first_received_sequence
            FROM live_channel_epochs
            WHERE live_session_id = $session;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        var epochs = new Dictionary<string, EpochRecord>(StringComparer.Ordinal);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var epoch = new EpochRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                LiveProjectionCanonicalizer.GetNullableString(reader, 3),
                LiveProjectionCanonicalizer.GetNullableString(reader, 4),
                LiveProjectionCanonicalizer.ParseTimestamp(reader.GetString(5)),
                reader.GetInt64(6));
            if (!epochs.TryAdd(epoch.Id, epoch))
            {
                throw new InvalidOperationException(
                    "Duplicate live channel epoch identity.");
            }
        }

        return epochs;
    }

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM live_capture_sessions
            WHERE live_session_id = $session
            LIMIT 1;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is not null;
    }

    private static async Task<CompletedSourceLedger?> ReadCompletedSourceLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                c.persisted_record_count,
                (SELECT COUNT(*)
                 FROM live_capture_records AS r
                 WHERE r.live_session_id = c.live_session_id)
            FROM live_capture_completions AS c
            WHERE c.live_session_id = $session;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CompletedSourceLedger(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<long> ReadSuccessfulProjectionHighWaterAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(considered_count), 0)
            FROM live_projection_runs
            WHERE live_session_id = $session
              AND outcome = 'completed';
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static StoredProjection ReadStoredProjection(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            LiveProjectionCanonicalizer.GetNullableInt64(reader, 5),
            LiveProjectionCanonicalizer.GetNullableString(reader, 6),
            reader.GetString(7),
            LiveProjectionCanonicalizer.GetNullableString(reader, 8),
            LiveProjectionCanonicalizer.GetNullableTimestamp(reader, 9),
            LiveProjectionCanonicalizer.ParseTimestamp(reader.GetString(10)),
            reader.GetString(11),
            LiveProjectionCanonicalizer.GetNullableString(reader, 12),
            LiveProjectionCanonicalizer.GetNullableInt32(reader, 13),
            LiveProjectionCanonicalizer.GetNullableString(reader, 14),
            LiveProjectionCanonicalizer.GetNullableString(reader, 15),
            LiveProjectionCanonicalizer.GetNullableInt32(reader, 16),
            LiveProjectionCanonicalizer.GetNullableString(reader, 17),
            LiveProjectionCanonicalizer.GetNullableString(reader, 18),
            LiveProjectionCanonicalizer.GetNullableString(reader, 19),
            LiveProjectionCanonicalizer.GetNullableInt32(reader, 20),
            LiveProjectionCanonicalizer.GetNullableString(reader, 21),
            LiveProjectionCanonicalizer.GetNullableString(reader, 22),
            LiveProjectionCanonicalizer.GetNullableString(reader, 23),
            LiveProjectionCanonicalizer.GetNullableString(reader, 24),
            LiveProjectionCanonicalizer.GetNullableString(reader, 25),
            LiveProjectionCanonicalizer.GetNullableBoolean(reader, 26),
            reader.GetString(27),
            (byte[])reader.GetValue(28),
            (byte[])reader.GetValue(29),
            reader.IsDBNull(30) ? null : (byte[])reader.GetValue(30),
            (byte[])reader.GetValue(31));

    private static bool EpochIdentityMatches(
        EpochRecord epoch,
        string liveSessionId,
        LiveProjectionPayload payload) =>
        string.Equals(
            epoch.LiveSessionId,
            liveSessionId,
            StringComparison.Ordinal)
        && string.Equals(
            epoch.ChannelName,
            payload.ChannelName,
            StringComparison.Ordinal)
        && LiveProjectionCanonicalizer.NullableEquals(
            epoch.MachineName,
            payload.MachineName)
        && string.Equals(
            epoch.Id,
            LiveProjectionIdentity.CreateEpochId(
                liveSessionId,
                payload.ChannelName,
                payload.MachineName),
            StringComparison.Ordinal);

    private static bool NullableHashEquals(byte[]? actual, byte[]? expected) =>
        actual is null
            ? expected is null
            : expected is not null
              && CryptographicOperations.FixedTimeEquals(actual, expected);

    private sealed record StoredProjection(
        string LiveProjectionId,
        string LiveEvidenceId,
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
        string? ParserRawEventId,
        int? ParsedEventId,
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
        byte[] RawXmlSha256,
        byte[] CanonicalPayloadSha256,
        byte[]? PreviousEntryHash,
        byte[] EntryHash);

    private sealed record EpochRecord(
        string Id,
        string LiveSessionId,
        string ChannelName,
        string? MachineName,
        string? ProviderName,
        DateTimeOffset OpenedUtc,
        long FirstReceivedSequence);

    private sealed record CompletedSourceLedger(
        long PersistedRecordCount,
        long ActualRecordCount)
    {
        public bool IsConsistent => PersistedRecordCount == ActualRecordCount;

        public string DescribeMismatch() =>
            $"完成记录声明已持久化 {PersistedRecordCount} 条 live evidence，"
            + $"当前源账本实际为 {ActualRecordCount} 条；拒绝把不完整来源视为连续。";
    }
}
