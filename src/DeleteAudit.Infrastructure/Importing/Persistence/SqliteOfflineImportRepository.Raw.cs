using System.Globalization;
using System.Text.Json;
using DeleteAudit.Domain;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Importing.Persistence;

public sealed partial class SqliteOfflineImportRepository
{
    private async Task<RawChainState> ReadChainTailAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ingest_sequence, entry_hash
            FROM raw_events
            ORDER BY ingest_sequence DESC
            LIMIT 1;
            """;

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new RawChainState(0, new byte[32]);
        }

        var previousHash = (byte[])reader.GetValue(1);
        if (previousHash.Length != 32)
        {
            throw new InvalidOperationException(
                "The existing raw event chain tail has an invalid hash.");
        }

        return new RawChainState(reader.GetInt64(0), previousHash);
    }

    private async Task<RawEventWriteResult> TryPersistRawEventAsync(
        PreparedImport preparedImport,
        PreparedImportRecord record,
        RawChainState chainState,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rawEvent = record.RawEvent;
        if (rawEvent is null)
        {
            return new RawEventWriteResult(null, false, null);
        }

        if (!TryStorageSource(rawEvent.Source, out var storageSource))
        {
            return SkippedRawEvent(
                record,
                "raw_event_projection_unsupported_source",
                $"Event source '{rawEvent.Source}' cannot be represented by the baseline schema.");
        }

        var missing = new List<string>();
        AddMissing(missing, "computer", rawEvent.ComputerName);
        AddMissing(missing, "channel", rawEvent.ChannelName);
        AddMissing(missing, "provider", rawEvent.ProviderName);
        if (rawEvent.EventRecordId is null)
        {
            missing.Add("eventRecordId");
        }

        if (string.IsNullOrWhiteSpace(rawEvent.RawEventId))
        {
            missing.Add("rawEventId");
        }

        if (missing.Count != 0)
        {
            return SkippedRawEvent(
                record,
                "raw_event_projection_missing_required_field",
                $"The raw event projection was skipped because required factual fields are absent: {string.Join(", ", missing)}.");
        }

        var epochId = CreateOfflineEpochId(
            preparedImport.ImportSession.Sha256,
            rawEvent.ComputerName!,
            rawEvent.ChannelName!,
            rawEvent.ProviderName!);
        var existingId = await FindExistingRawEventIdAsync(
                rawEvent,
                epochId,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingId is not null)
        {
            return new RawEventWriteResult(existingId, false, null);
        }

        await InsertOfflineEpochAsync(
                epochId,
                rawEvent,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        var sequence = chainState.NextSequence();
        var rawXmlHash = HashUtf8(rawEvent.RawXml);
        var entryHash = HashFields(
            Convert.ToHexString(chainState.PreviousEntryHash),
            rawEvent.RawEventId,
            Convert.ToHexString(rawXmlHash),
            sequence);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_events (
                raw_event_id,
                channel_epoch_id,
                source,
                computer_name,
                channel_name,
                provider_name,
                event_id,
                event_record_id,
                event_utc,
                event_local,
                local_utc_offset_minutes,
                windows_time_zone_id,
                observed_utc,
                raw_xml,
                raw_xml_sha256,
                ingest_sequence,
                previous_entry_hash,
                entry_hash,
                format_version)
            VALUES (
                $raw_event_id,
                $channel_epoch_id,
                $source,
                $computer_name,
                $channel_name,
                $provider_name,
                $event_id,
                $event_record_id,
                $event_utc,
                $event_local,
                0,
                'UTC',
                $observed_utc,
                $raw_xml,
                $raw_xml_sha256,
                $ingest_sequence,
                $previous_entry_hash,
                $entry_hash,
                1);
            """;
        command.Parameters.AddWithValue("$raw_event_id", rawEvent.RawEventId);
        command.Parameters.AddWithValue("$channel_epoch_id", epochId);
        command.Parameters.AddWithValue("$source", storageSource);
        command.Parameters.AddWithValue("$computer_name", rawEvent.ComputerName!);
        command.Parameters.AddWithValue("$channel_name", rawEvent.ChannelName!);
        command.Parameters.AddWithValue("$provider_name", rawEvent.ProviderName!);
        command.Parameters.AddWithValue("$event_id", rawEvent.EventId);
        command.Parameters.AddWithValue("$event_record_id", rawEvent.EventRecordId!.Value);
        command.Parameters.AddWithValue("$event_utc", Format(rawEvent.EventTimeUtc));
        command.Parameters.AddWithValue(
            "$event_local",
            rawEvent.EventTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observed_utc", Format(rawEvent.ObservedUtc));
        command.Parameters.AddWithValue("$raw_xml", rawEvent.RawXml);
        command.Parameters.Add("$raw_xml_sha256", SqliteType.Blob).Value = rawXmlHash;
        command.Parameters.AddWithValue("$ingest_sequence", sequence);
        command.Parameters.Add("$previous_entry_hash", SqliteType.Blob).Value =
            chainState.PreviousEntryHash;
        command.Parameters.Add("$entry_hash", SqliteType.Blob).Value = entryHash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        chainState.Accept(entryHash);
        return new RawEventWriteResult(rawEvent.RawEventId, true, null);
    }

    private async Task<string?> FindExistingRawEventIdAsync(
        RawWindowsEvent rawEvent,
        string epochId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_event_id
            FROM raw_events
            WHERE raw_event_id = $raw_event_id
               OR (
                    computer_name = $computer_name
                    AND channel_epoch_id = $channel_epoch_id
                    AND event_record_id = $event_record_id)
            ORDER BY CASE WHEN raw_event_id = $raw_event_id THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$raw_event_id", rawEvent.RawEventId);
        command.Parameters.AddWithValue("$computer_name", rawEvent.ComputerName!);
        command.Parameters.AddWithValue("$channel_epoch_id", epochId);
        command.Parameters.AddWithValue("$event_record_id", rawEvent.EventRecordId!.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task InsertOfflineEpochAsync(
        string epochId,
        RawWindowsEvent rawEvent,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO channel_epochs (
                channel_epoch_id,
                computer_name,
                channel_name,
                provider_name,
                started_utc,
                first_record_id,
                start_reason,
                coverage_gap)
            VALUES (
                $channel_epoch_id,
                $computer_name,
                $channel_name,
                $provider_name,
                $started_utc,
                $first_record_id,
                'initial',
                0)
            ON CONFLICT(channel_epoch_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$channel_epoch_id", epochId);
        command.Parameters.AddWithValue("$computer_name", rawEvent.ComputerName!);
        command.Parameters.AddWithValue("$channel_name", rawEvent.ChannelName!);
        command.Parameters.AddWithValue("$provider_name", rawEvent.ProviderName!);
        command.Parameters.AddWithValue("$started_utc", Format(rawEvent.EventTimeUtc));
        command.Parameters.AddWithValue("$first_record_id", rawEvent.EventRecordId!.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertImportRecordAsync(
        string importSessionId,
        PreparedImportRecord record,
        string? rawEventId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rawXml = record.SourceRecord.RawXml;
        var rawXmlHash = rawXml is null ? null : HashUtf8(rawXml);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO import_records (
                import_session_id,
                record_ordinal,
                raw_xml_state,
                raw_xml,
                raw_xml_sha256,
                outcome,
                raw_event_id)
            VALUES (
                $import_session_id,
                $record_ordinal,
                $raw_xml_state,
                $raw_xml,
                $raw_xml_sha256,
                $outcome,
                $raw_event_id);
            """;
        command.Parameters.AddWithValue("$import_session_id", importSessionId);
        command.Parameters.AddWithValue("$record_ordinal", record.SourceRecord.RecordNumber);
        command.Parameters.AddWithValue(
            "$raw_xml_state",
            record.SourceRecord.State == OfflineRecordState.Available
                ? "captured"
                : "unavailable");
        AddNullable(command, "$raw_xml", rawXml);
        AddNullable(command, "$raw_xml_sha256", rawXmlHash, SqliteType.Blob);
        command.Parameters.AddWithValue("$outcome", ToStorageOutcome(record.Outcome));
        AddNullable(command, "$raw_event_id", rawEventId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportDiagnostic?> TryInsertProcessObservationAsync(
        PreparedImportRecord record,
        string rawEventId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var context = record.ProcessContext!;
        if (context.ProcessId is null || string.IsNullOrWhiteSpace(context.ProcessGuid))
        {
            return new ImportDiagnostic(
                "process_projection_missing_required_identity",
                "The process observation was retained only in raw evidence because PID and ProcessGuid are both required by the baseline schema.",
                ImportDiagnosticSeverity.Warning,
                "persist",
                record.SourceRecord.RecordNumber);
        }

        var integrityHash = HashFields(
            rawEventId,
            context.ProcessGuid,
            context.ProcessId,
            context.StartedUtc,
            context.ProcessPath,
            context.CommandLine,
            context.ParentProcessGuid,
            context.ParentProcessId,
            context.ParentProcessPath,
            context.UserName,
            context.UserSid);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO process_observations (
                process_observation_id,
                source_raw_event_id,
                process_guid,
                boot_id,
                process_id,
                process_start_utc,
                process_path,
                command_line,
                parent_process_guid,
                parent_process_id,
                parent_process_path,
                user_name,
                user_sid,
                integrity_hash)
            VALUES (
                $process_observation_id,
                $source_raw_event_id,
                $process_guid,
                NULL,
                $process_id,
                $process_start_utc,
                $process_path,
                $command_line,
                $parent_process_guid,
                $parent_process_id,
                $parent_process_path,
                $user_name,
                $user_sid,
                $integrity_hash)
            ON CONFLICT(source_raw_event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue(
            "$process_observation_id",
            StableId("proc", rawEventId));
        command.Parameters.AddWithValue("$source_raw_event_id", rawEventId);
        command.Parameters.AddWithValue("$process_guid", context.ProcessGuid);
        command.Parameters.AddWithValue("$process_id", context.ProcessId.Value);
        command.Parameters.AddWithValue("$process_start_utc", Format(context.StartedUtc));
        AddNullable(command, "$process_path", context.ProcessPath);
        AddNullable(command, "$command_line", context.CommandLine);
        AddNullable(command, "$parent_process_guid", context.ParentProcessGuid);
        AddNullable(command, "$parent_process_id", context.ParentProcessId);
        AddNullable(command, "$parent_process_path", context.ParentProcessPath);
        AddNullable(command, "$user_name", context.UserName);
        AddNullable(command, "$user_sid", context.UserSid);
        command.Parameters.Add("$integrity_hash", SqliteType.Blob).Value = integrityHash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task InsertDiagnosticIfNewAsync(
        string importSessionId,
        ImportDiagnostic diagnostic,
        IReadOnlySet<long> knownRecordNumbers,
        DiagnosticWriteState state,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var recordNumber = diagnostic.RecordNumber is not null
            && knownRecordNumbers.Contains(diagnostic.RecordNumber.Value)
                ? diagnostic.RecordNumber
                : null;
        var key = string.Join(
            "\u001f",
            recordNumber,
            diagnostic.Stage,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message);
        if (!state.Keys.Add(key))
        {
            return;
        }

        var stage = NormalizeDiagnosticStage(diagnostic.Stage);
        var details = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (!string.Equals(stage, diagnostic.Stage, StringComparison.OrdinalIgnoreCase))
        {
            details["originalStage"] = diagnostic.Stage;
        }

        if (diagnostic.RecordNumber is not null && recordNumber is null)
        {
            details["unmatchedRecordNumber"] = diagnostic.RecordNumber.Value;
        }

        state.Sequence++;
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO import_diagnostics (
                import_diagnostic_id,
                import_session_id,
                record_ordinal,
                stage,
                severity,
                code,
                message,
                details_json,
                occurred_utc)
            VALUES (
                $import_diagnostic_id,
                $import_session_id,
                $record_ordinal,
                $stage,
                $severity,
                $code,
                $message,
                $details_json,
                $occurred_utc);
            """;
        command.Parameters.AddWithValue(
            "$import_diagnostic_id",
            StableId(
                "diag",
                importSessionId,
                recordNumber,
                diagnostic.Code,
                diagnostic.Message,
                state.Sequence));
        command.Parameters.AddWithValue("$import_session_id", importSessionId);
        AddNullable(command, "$record_ordinal", recordNumber);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$severity", ToStorageSeverity(diagnostic.Severity));
        command.Parameters.AddWithValue("$code", diagnostic.Code);
        command.Parameters.AddWithValue("$message", diagnostic.Message);
        command.Parameters.AddWithValue("$details_json", JsonSerializer.Serialize(details));
        command.Parameters.AddWithValue("$occurred_utc", Format(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveRawEventIdAsync(
        string? requestedRawEventId,
        IReadOnlyDictionary<string, string> currentImportMappings,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedRawEventId))
        {
            return null;
        }

        if (currentImportMappings.TryGetValue(requestedRawEventId, out var mapped))
        {
            return mapped;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_event_id
            FROM raw_events
            WHERE raw_event_id = $raw_event_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$raw_event_id", requestedRawEventId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static string CreateOfflineEpochId(
        string sourceSha256,
        string computerName,
        string channelName,
        string providerName) =>
        StableId(
            "offline-epoch",
            sourceSha256.ToLowerInvariant(),
            computerName,
            channelName,
            providerName);

    private static RawEventWriteResult SkippedRawEvent(
        PreparedImportRecord record,
        string code,
        string message) =>
        new(
            null,
            false,
            new ImportDiagnostic(
                code,
                message,
                ImportDiagnosticSeverity.Warning,
                "persist",
                record.SourceRecord.RecordNumber));

    private static void AddMissing(List<string> output, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            output.Add(name);
        }
    }

    private static bool TryStorageSource(
        WindowsEventSource source,
        out string storageSource)
    {
        storageSource = source switch
        {
            WindowsEventSource.SysmonDelete => "sysmon_delete",
            WindowsEventSource.SysmonProcess => "sysmon_process",
            WindowsEventSource.Security4663 => "security_4663",
            _ => string.Empty
        };
        return storageSource.Length != 0;
    }

    private static string ToStorageOutcome(ImportRecordOutcome outcome) => outcome switch
    {
        ImportRecordOutcome.Succeeded => "success",
        ImportRecordOutcome.Ignored => "ignored",
        ImportRecordOutcome.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToStorageSeverity(ImportDiagnosticSeverity severity) => severity switch
    {
        ImportDiagnosticSeverity.Information => "info",
        ImportDiagnosticSeverity.Warning => "warning",
        ImportDiagnosticSeverity.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
    };

    private static string NormalizeDiagnosticStage(string stage)
    {
        var normalized = stage.Trim().ToLowerInvariant();
        return normalized switch
        {
            "source_validation" or "validation" => "source_validation",
            "source_read" or "read" => "read",
            "record_extract" or "source_extract" or "extract" => "extract",
            "xml_parse" or "parse" => "parse",
            "normalization" or "normalize" => "normalize",
            "correlation" or "correlate" => "correlate",
            "persistence" or "persist" => "persist",
            "jsonl" => "jsonl",
            "manifest" => "manifest",
            _ => "persist"
        };
    }

    private sealed record RawEventWriteResult(
        string? RawEventId,
        bool WasInserted,
        ImportDiagnostic? Diagnostic);

    private sealed class RawChainState(long lastSequence, byte[] previousEntryHash)
    {
        public long LastSequence { get; private set; } = lastSequence;

        public byte[] PreviousEntryHash { get; private set; } = previousEntryHash;

        public long NextSequence() => checked(LastSequence + 1);

        public void Accept(byte[] entryHash)
        {
            LastSequence = checked(LastSequence + 1);
            PreviousEntryHash = entryHash;
        }
    }
}
