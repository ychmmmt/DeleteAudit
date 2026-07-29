using System.Globalization;
using DeleteAudit.Application.Projection;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Projection;

public sealed partial class SqliteLiveProjectionService
{
    private const int ProjectionBatchSize = 32;

    private async Task<LiveProjectionRunResult> ProjectCoreAsync(
        string liveSessionId,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenWritableAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            if (!await SessionExistsAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new ProjectionFailureException(
                    "session_not_found",
                    "找不到指定的实时接入会话。",
                    0,
                    0);
            }

            var existing = await ReadProjectionPositionAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            var continuity = await VerifyContinuityCoreAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!continuity.IsContinuous)
            {
                throw new ProjectionFailureException(
                    "continuity_broken",
                    continuity.Detail ?? "已有实时投影连续性不完整。",
                    existing.Count,
                    existing.Count);
            }

            var considered = await CountProjectableSourceAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    maximumReceivedSequence: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing.Count > considered)
            {
                throw new ProjectionFailureException(
                    "projection_exceeds_source",
                    "实时投影记录数超过当前可投影源证据数。",
                    considered,
                    existing.Count);
            }

            if (existing.Count > 0)
            {
                var prefixCount = await CountProjectableSourceAsync(
                        connection,
                        transaction,
                        liveSessionId,
                        existing.LastSourceReceivedSequence,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (prefixCount != existing.Count)
                {
                    throw new ProjectionFailureException(
                        "projection_not_source_prefix",
                        "已有实时投影不是源证据接收顺序的完整前缀，拒绝追加。",
                        considered,
                        existing.Count);
                }
            }

            var nextIngestSequence = existing.Count + 1;
            var projectedCount = 0L;
            var lastReceivedSequence = existing.LastSourceReceivedSequence;
            var previousEntryHash = existing.LastEntryHash
                ?? LiveProjectionIdentity.ChainAnchor.ToArray();

            while (true)
            {
                var batch = await ReadSourceBatchAsync(
                        connection,
                        transaction,
                        liveSessionId,
                        lastReceivedSequence,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var sourceRecord in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CanonicalizedRecord canonical;
                    try
                    {
                        canonical = LiveProjectionCanonicalizer.Canonicalize(sourceRecord);
                    }
                    catch (ProjectionFailureException exception)
                    {
                        throw new ProjectionFailureException(
                            exception.Code,
                            exception.Message,
                            considered,
                            existing.Count);
                    }
                    await EnsureEpochAsync(
                            connection,
                            transaction,
                            sourceRecord,
                            canonical,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var payloadHash = LiveProjectionIdentity.ComputePayloadHash(
                        canonical.Payload);
                    var entryHash = LiveProjectionIdentity.ComputeEntryHash(
                        previousEntryHash,
                        sourceRecord.LiveEvidenceId,
                        canonical.EpochId,
                        nextIngestSequence,
                        canonical.RawXmlSha256,
                        payloadHash);

                    await InsertProjectionAsync(
                            connection,
                            transaction,
                            sourceRecord,
                            canonical,
                            nextIngestSequence,
                            previousEntryHash,
                            payloadHash,
                            entryHash,
                            startedUtc,
                            cancellationToken)
                        .ConfigureAwait(false);

                    previousEntryHash = entryHash;
                    nextIngestSequence++;
                    projectedCount++;
                    lastReceivedSequence = sourceRecord.ReceivedSequence;
                }
            }

            if (existing.Count + projectedCount != considered)
            {
                throw new ProjectionFailureException(
                    "source_changed_during_projection",
                    "投影事务内的源证据计数发生不一致，拒绝提交。",
                    considered,
                    existing.Count);
            }

            await InsertRunAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    startedUtc,
                    _timeProvider.GetUtcNow(),
                    succeeded: true,
                    considered,
                    projectedCount,
                    existing.Count,
                    failureCode: null,
                    failureDetail: null,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new LiveProjectionRunResult(
                liveSessionId,
                considered,
                projectedCount,
                existing.Count,
                true,
                null,
                null);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                _ = rollbackFailure;
            }

            throw;
        }
    }

    private async Task TryRecordFailureAsync(
        string liveSessionId,
        DateTimeOffset startedUtc,
        string code,
        string detail,
        long consideredCount,
        long skippedCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenWritableAsync(cancellationToken)
                .ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!await SessionExistsAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            await InsertRunAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    startedUtc,
                    _timeProvider.GetUtcNow(),
                    succeeded: false,
                    consideredCount,
                    projectedCount: 0,
                    skippedCount,
                    LiveProjectionCanonicalizer.Bound(
                        code,
                        MaximumFailureCodeCharacters),
                    Bound(detail),
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failureRecordingException) when (
            failureRecordingException is SqliteException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            // The primary projection failure remains the result. A secondary attempt to
            // describe it must never rewrite or obscure the captured evidence.
            _ = failureRecordingException;
        }
    }

    private static async Task EnsureEpochAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceRecord sourceRecord,
        CanonicalizedRecord canonical,
        CancellationToken cancellationToken)
    {
        var payload = canonical.Payload;
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT live_session_id, channel_name, machine_name
                FROM live_channel_epochs
                WHERE live_channel_epoch_id = $id;
                """;
            query.Parameters.Add("$id", SqliteType.Text).Value = canonical.EpochId;
            await using var reader = await query
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(
                        reader.GetString(0),
                        sourceRecord.LiveSessionId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        reader.GetString(1),
                        payload.ChannelName,
                        StringComparison.Ordinal)
                    || !LiveProjectionCanonicalizer.NullableEquals(
                        LiveProjectionCanonicalizer.GetNullableString(reader, 2),
                        payload.MachineName))
                {
                    throw LiveProjectionCanonicalizer.InvalidSource(
                        "epoch_identity_collision",
                        "deterministic live epoch id 已存在但元数据不一致。");
                }

                return;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO live_channel_epochs (
                live_channel_epoch_id,
                live_session_id,
                channel_name,
                machine_name,
                provider_name,
                opened_utc,
                first_received_sequence)
            VALUES (
                $epoch,
                $session,
                $channel,
                $machine,
                $provider,
                $opened,
                $first_sequence);
            """;
        insert.Parameters.Add("$epoch", SqliteType.Text).Value = canonical.EpochId;
        insert.Parameters.Add("$session", SqliteType.Text).Value =
            sourceRecord.LiveSessionId;
        insert.Parameters.Add("$channel", SqliteType.Text).Value = payload.ChannelName;
        insert.Parameters.Add("$machine", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.MachineName);
        insert.Parameters.Add("$provider", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ProviderName);
        insert.Parameters.Add("$opened", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.Format(
                payload.EventUtc ?? payload.ObservedUtc);
        insert.Parameters.Add("$first_sequence", SqliteType.Integer).Value =
            sourceRecord.ReceivedSequence;
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceRecord sourceRecord,
        CanonicalizedRecord canonical,
        long liveIngestSequence,
        byte[] previousEntryHash,
        byte[] payloadHash,
        byte[] entryHash,
        DateTimeOffset projectedUtc,
        CancellationToken cancellationToken)
    {
        var payload = canonical.Payload;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO live_projected_records (
                live_projection_id, live_evidence_id, live_session_id,
                live_channel_epoch_id, source_received_sequence,
                live_ingest_sequence, event_record_id, provider_name,
                channel_name, machine_name, event_utc, observed_utc, source,
                parser_raw_event_id, parsed_event_id, normalized_path, object_kind,
                process_id, process_path, process_guid, command_line,
                parent_process_id, parent_process_path, parent_process_guid,
                user_name, user_sid, delete_permission, archive_expected,
                missing_fields_json, raw_xml_sha256, canonical_payload_sha256,
                previous_entry_hash, entry_hash, projected_utc)
            VALUES (
                $projection_id, $evidence_id, $session, $epoch, $source_sequence,
                $ingest_sequence, $event_record_id, $provider, $channel, $machine,
                $event_utc, $observed_utc, $source, $parser_raw_event_id,
                $parsed_event_id, $path, $object_kind, $process_id, $process_path,
                $process_guid, $command_line, $parent_process_id,
                $parent_process_path, $parent_process_guid, $user_name, $user_sid,
                $delete_permission, $archive_expected, $missing_fields_json,
                $raw_xml_sha256, $payload_sha256, $previous_hash, $entry_hash,
                $projected_utc);
            """;
        command.Parameters.Add("$projection_id", SqliteType.Text).Value =
            LiveProjectionIdentity.CreateProjectionId(sourceRecord.LiveEvidenceId);
        command.Parameters.Add("$evidence_id", SqliteType.Text).Value =
            sourceRecord.LiveEvidenceId;
        command.Parameters.Add("$session", SqliteType.Text).Value =
            sourceRecord.LiveSessionId;
        command.Parameters.Add("$epoch", SqliteType.Text).Value = canonical.EpochId;
        command.Parameters.Add("$source_sequence", SqliteType.Integer).Value =
            sourceRecord.ReceivedSequence;
        command.Parameters.Add("$ingest_sequence", SqliteType.Integer).Value =
            liveIngestSequence;
        command.Parameters.Add("$event_record_id", SqliteType.Integer).Value =
            LiveProjectionCanonicalizer.ToDb(payload.EventRecordId);
        command.Parameters.Add("$provider", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ProviderName);
        command.Parameters.Add("$channel", SqliteType.Text).Value = payload.ChannelName;
        command.Parameters.Add("$machine", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.MachineName);
        command.Parameters.Add("$event_utc", SqliteType.Text).Value =
            payload.EventUtc is null
                ? DBNull.Value
                : LiveProjectionCanonicalizer.Format(payload.EventUtc.Value);
        command.Parameters.Add("$observed_utc", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.Format(payload.ObservedUtc);
        command.Parameters.Add("$source", SqliteType.Text).Value = payload.Source;
        command.Parameters.Add("$parser_raw_event_id", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ParserRawEventId);
        command.Parameters.Add("$parsed_event_id", SqliteType.Integer).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ParsedEventId);
        command.Parameters.Add("$path", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.NormalizedPath);
        command.Parameters.Add("$object_kind", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ObjectKind);
        command.Parameters.Add("$process_id", SqliteType.Integer).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ProcessId);
        command.Parameters.Add("$process_path", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ProcessPath);
        command.Parameters.Add("$process_guid", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ProcessGuid);
        command.Parameters.Add("$command_line", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.CommandLine);
        command.Parameters.Add("$parent_process_id", SqliteType.Integer).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ParentProcessId);
        command.Parameters.Add("$parent_process_path", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ParentProcessPath);
        command.Parameters.Add("$parent_process_guid", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.ParentProcessGuid);
        command.Parameters.Add("$user_name", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.UserName);
        command.Parameters.Add("$user_sid", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.UserSid);
        command.Parameters.Add("$delete_permission", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(payload.DeletePermission);
        command.Parameters.Add("$archive_expected", SqliteType.Integer).Value =
            payload.ArchiveExpected is null
                ? DBNull.Value
                : payload.ArchiveExpected.Value ? 1 : 0;
        command.Parameters.Add("$missing_fields_json", SqliteType.Text).Value =
            canonical.MissingFieldsJson;
        command.Parameters.Add("$raw_xml_sha256", SqliteType.Blob).Value =
            canonical.RawXmlSha256;
        command.Parameters.Add("$payload_sha256", SqliteType.Blob).Value = payloadHash;
        command.Parameters.Add("$previous_hash", SqliteType.Blob).Value =
            liveIngestSequence == 1 ? DBNull.Value : previousEntryHash;
        command.Parameters.Add("$entry_hash", SqliteType.Blob).Value = entryHash;
        command.Parameters.Add("$projected_utc", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.Format(projectedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string liveSessionId,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc,
        bool succeeded,
        long consideredCount,
        long projectedCount,
        long skippedCount,
        string? failureCode,
        string? failureDetail,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO live_projection_runs (
                live_projection_run_id, live_session_id, started_utc, completed_utc,
                outcome, considered_count, projected_count, skipped_count,
                failure_code, failure_detail)
            VALUES (
                $run_id, $session, $started, $completed, $outcome, $considered,
                $projected, $skipped, $failure_code, $failure_detail);
            """;
        command.Parameters.Add("$run_id", SqliteType.Text).Value =
            $"live-projection-run:{Guid.NewGuid():N}";
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        command.Parameters.Add("$started", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.Format(startedUtc);
        command.Parameters.Add("$completed", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.Format(completedUtc);
        command.Parameters.Add("$outcome", SqliteType.Text).Value =
            succeeded ? "completed" : "failed";
        command.Parameters.Add("$considered", SqliteType.Integer).Value = consideredCount;
        command.Parameters.Add("$projected", SqliteType.Integer).Value = projectedCount;
        command.Parameters.Add("$skipped", SqliteType.Integer).Value = skippedCount;
        command.Parameters.Add("$failure_code", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(failureCode);
        command.Parameters.Add("$failure_detail", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(failureDetail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProjectionPosition> ReadProjectionPositionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(MAX(source_received_sequence), 0),
                (
                    SELECT entry_hash
                    FROM live_projected_records
                    WHERE live_session_id = $session
                    ORDER BY live_ingest_sequence DESC
                    LIMIT 1
                )
            FROM live_projected_records
            WHERE live_session_id = $session;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectionPosition(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2));
    }

    private static async Task<long> CountProjectableSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string liveSessionId,
        long? maximumReceivedSequence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM live_capture_records
            WHERE live_session_id = $session
              AND outcome IN ('delete_fact', 'process_context', 'security_evidence')
              AND ($maximum IS NULL OR received_sequence <= $maximum);
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        command.Parameters.Add("$maximum", SqliteType.Integer).Value =
            maximumReceivedSequence is null
                ? DBNull.Value
                : maximumReceivedSequence.Value;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<SourceRecord>> ReadSourceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string liveSessionId,
        long afterReceivedSequence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                live_evidence_id, live_session_id, received_sequence,
                event_record_id, provider_name, channel_name, machine_name,
                time_created_utc, observed_utc, raw_xml, raw_xml_sha256,
                parser_raw_event_id, parsed_event_id, outcome
            FROM live_capture_records
            WHERE live_session_id = $session
              AND outcome IN ('delete_fact', 'process_context', 'security_evidence')
              AND received_sequence > $after
            ORDER BY received_sequence ASC
            LIMIT $limit;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        command.Parameters.Add("$after", SqliteType.Integer).Value = afterReceivedSequence;
        command.Parameters.Add("$limit", SqliteType.Integer).Value = ProjectionBatchSize;

        var records = new List<SourceRecord>(ProjectionBatchSize);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(LiveProjectionCanonicalizer.ReadSourceRecord(reader, 0));
        }

        return records;
    }

    private async Task<SqliteConnection> OpenWritableAsync(
        CancellationToken cancellationToken)
    {
        var databasePath = _location.EnsureDatabasePath();
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "The viewer database does not exist; runtime projection does not create it.");
        }

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA recursive_triggers = ON;
                """;
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed record ProjectionPosition(
        long Count,
        long LastSourceReceivedSequence,
        byte[]? LastEntryHash);
}
