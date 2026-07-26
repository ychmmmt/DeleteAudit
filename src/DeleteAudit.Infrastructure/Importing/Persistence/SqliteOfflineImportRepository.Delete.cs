using System.Globalization;
using System.Text.Json;
using DeleteAudit.Domain;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Importing.Persistence;

public sealed partial class SqliteOfflineImportRepository
{
    private async Task<int> PersistDeleteProjectionsAsync(
        PreparedImport preparedImport,
        IReadOnlyDictionary<string, string> rawEventIds,
        IReadOnlySet<long> knownRecordNumbers,
        DiagnosticWriteState diagnosticState,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var insertedCount = 0;
        var insertedSessions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var projection in preparedImport.DeleteProjections
                     .OrderBy(item => item.CorrelationResult.Event.OccurredUtc))
        {
            var deleteEvent = projection.CorrelationResult.Event;
            var recordNumber = FindRecordNumber(preparedImport.Records, deleteEvent.RawEventId);
            var validationError = ValidateDeleteProjection(deleteEvent);
            if (validationError is not null)
            {
                await InsertDiagnosticIfNewAsync(
                        preparedImport.ImportSession.ImportSessionId,
                        new ImportDiagnostic(
                            "delete_projection_missing_required_field",
                            validationError,
                            ImportDiagnosticSeverity.Warning,
                            "persist",
                            recordNumber),
                        knownRecordNumbers,
                        diagnosticState,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var primaryRawEventId = await ResolveRawEventIdAsync(
                    deleteEvent.RawEventId,
                    rawEventIds,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (primaryRawEventId is null)
            {
                await InsertDiagnosticIfNewAsync(
                        preparedImport.ImportSession.ImportSessionId,
                        new ImportDiagnostic(
                            "delete_projection_raw_event_unavailable",
                            "The delete projection was skipped because its primary raw event was not persisted.",
                            ImportDiagnosticSeverity.Warning,
                            "persist",
                            recordNumber),
                        knownRecordNumbers,
                        diagnosticState,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var existingDeleteEventId = await FindExistingDeleteEventIdAsync(
                    deleteEvent.DeleteEventId,
                    primaryRawEventId,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingDeleteEventId is not null)
            {
                throw new InvalidOperationException(
                    $"Delete event '{deleteEvent.DeleteEventId}' became duplicate after import preparation. The transaction was rolled back to prevent session or risk over-counting.");
            }

            var aggregation = projection.SessionAggregationResult;
            if (insertedSessions.Add(aggregation.Session.DeleteSessionId))
            {
                await InsertDeleteSessionAsync(
                        aggregation,
                        deleteEvent,
                        preparedImport.ImportSession.EndedUtc,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await InsertDeleteEventAsync(
                    projection,
                    primaryRawEventId,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            insertedCount++;

            var correlationDiagnostic = await TryInsertEventCorrelationAsync(
                    projection.CorrelationResult,
                    rawEventIds,
                    preparedImport.ImportSession.EndedUtc,
                    recordNumber,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (correlationDiagnostic is not null)
            {
                await InsertDiagnosticIfNewAsync(
                        preparedImport.ImportSession.ImportSessionId,
                        correlationDiagnostic,
                        knownRecordNumbers,
                        diagnosticState,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await InsertPrimaryEvidenceAsync(
                    deleteEvent.DeleteEventId,
                    primaryRawEventId,
                    preparedImport.ImportSession.EndedUtc,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertSessionMemberAsync(
                    aggregation.Session.DeleteSessionId,
                    deleteEvent.DeleteEventId,
                    preparedImport.ImportSession.EndedUtc,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertRiskAssessmentAsync(
                    aggregation.Assessment,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return insertedCount;
    }

    private async Task<string?> FindExistingDeleteEventIdAsync(
        string deleteEventId,
        string primaryRawEventId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT delete_event_id
            FROM delete_events
            WHERE delete_event_id = $delete_event_id
               OR primary_raw_event_id = $primary_raw_event_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$delete_event_id", deleteEventId);
        command.Parameters.AddWithValue("$primary_raw_event_id", primaryRawEventId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task InsertDeleteSessionAsync(
        SessionAggregationResult aggregation,
        NormalizedDeleteEvent representativeEvent,
        DateTimeOffset sealedUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var session = aggregation.Session;
        var integrityHash = HashFields(
            session.DeleteSessionId,
            session.OpenedUtc,
            session.LastEventUtc,
            sealedUtc,
            session.ProcessIdentity,
            representativeEvent.ProcessId,
            representativeEvent.ProcessGuid,
            representativeEvent.UserSid,
            session.MainPath,
            session.ConfirmedItemCount,
            session.ProtectedItemCount,
            session.CurrentRisk);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO delete_sessions (
                delete_session_id,
                opened_utc,
                last_event_utc,
                sealed_utc,
                process_identity,
                process_id,
                process_guid,
                user_sid,
                path_scope,
                confirmed_item_count,
                protected_item_count,
                current_risk,
                warning_emitted,
                critical_emitted,
                integrity_hash)
            VALUES (
                $delete_session_id,
                $opened_utc,
                $last_event_utc,
                $sealed_utc,
                $process_identity,
                $process_id,
                $process_guid,
                $user_sid,
                $path_scope,
                $confirmed_item_count,
                $protected_item_count,
                $current_risk,
                0,
                0,
                $integrity_hash)
            ON CONFLICT(delete_session_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$delete_session_id", session.DeleteSessionId);
        command.Parameters.AddWithValue("$opened_utc", Format(session.OpenedUtc));
        command.Parameters.AddWithValue("$last_event_utc", Format(session.LastEventUtc));
        command.Parameters.AddWithValue("$sealed_utc", Format(sealedUtc));
        command.Parameters.AddWithValue("$process_identity", session.ProcessIdentity);
        AddNullable(command, "$process_id", representativeEvent.ProcessId);
        AddNullable(command, "$process_guid", representativeEvent.ProcessGuid);
        AddNullable(command, "$user_sid", representativeEvent.UserSid);
        command.Parameters.AddWithValue("$path_scope", session.MainPath);
        command.Parameters.AddWithValue("$confirmed_item_count", session.ConfirmedItemCount);
        command.Parameters.AddWithValue("$protected_item_count", session.ProtectedItemCount);
        command.Parameters.AddWithValue("$current_risk", ToStorageRisk(session.CurrentRisk));
        command.Parameters.Add("$integrity_hash", SqliteType.Blob).Value = integrityHash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertDeleteEventAsync(
        PreparedDeleteProjection projection,
        string primaryRawEventId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var correlation = projection.CorrelationResult;
        var deleteEvent = correlation.Event;
        var assessment = projection.SessionAggregationResult.Assessment;
        var normalizedPath = NormalizeAuditPath(deleteEvent.FullPath!);
        var missingFieldsJson = JsonSerializer.Serialize(deleteEvent.MissingFields);
        var contentHash = HashFields(
            deleteEvent.DeleteEventId,
            primaryRawEventId,
            deleteEvent.OccurredUtc,
            deleteEvent.SourceEventId,
            deleteEvent.EventRecordId,
            deleteEvent.FullPath,
            deleteEvent.ObjectKind,
            deleteEvent.ProcessId,
            deleteEvent.ProcessPath,
            deleteEvent.ProcessGuid,
            deleteEvent.CommandLine,
            deleteEvent.ParentProcessId,
            deleteEvent.ParentProcessPath,
            deleteEvent.ParentProcessGuid,
            deleteEvent.UserName,
            deleteEvent.UserSid,
            deleteEvent.DeletePermission,
            missingFieldsJson);
        var integrityHash = HashFields(
            Convert.ToHexString(contentHash),
            projection.SessionAggregationResult.Session.DeleteSessionId,
            assessment.RiskLevel,
            correlation.Confidence);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO delete_events (
                delete_event_id,
                primary_raw_event_id,
                delete_session_id,
                occurred_utc,
                occurred_local,
                local_utc_offset_minutes,
                windows_time_zone_id,
                event_record_id,
                source,
                source_event_id,
                full_path,
                normalized_path,
                object_kind,
                volume_serial,
                file_reference_number,
                process_id,
                process_path,
                process_guid,
                command_line,
                parent_process_id,
                parent_process_path,
                parent_process_guid,
                user_name,
                user_sid,
                delete_permission_type,
                initial_risk,
                attribution_confidence,
                archive_expected,
                archive_reference,
                missing_fields_json,
                content_sha256,
                integrity_hash)
            VALUES (
                $delete_event_id,
                $primary_raw_event_id,
                $delete_session_id,
                $occurred_utc,
                $occurred_local,
                0,
                'UTC',
                $event_record_id,
                $source,
                $source_event_id,
                $full_path,
                $normalized_path,
                $object_kind,
                NULL,
                NULL,
                $process_id,
                $process_path,
                $process_guid,
                $command_line,
                $parent_process_id,
                $parent_process_path,
                $parent_process_guid,
                $user_name,
                $user_sid,
                $delete_permission_type,
                $initial_risk,
                $attribution_confidence,
                $archive_expected,
                NULL,
                $missing_fields_json,
                $content_sha256,
                $integrity_hash);
            """;
        command.Parameters.AddWithValue("$delete_event_id", deleteEvent.DeleteEventId);
        command.Parameters.AddWithValue("$primary_raw_event_id", primaryRawEventId);
        command.Parameters.AddWithValue(
            "$delete_session_id",
            projection.SessionAggregationResult.Session.DeleteSessionId);
        command.Parameters.AddWithValue("$occurred_utc", Format(deleteEvent.OccurredUtc));
        command.Parameters.AddWithValue(
            "$occurred_local",
            deleteEvent.OccurredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event_record_id", deleteEvent.EventRecordId!.Value);
        command.Parameters.AddWithValue(
            "$source",
            deleteEvent.SourceEventId == 23 ? "sysmon_23" : "sysmon_26");
        command.Parameters.AddWithValue("$source_event_id", deleteEvent.SourceEventId);
        command.Parameters.AddWithValue("$full_path", deleteEvent.FullPath!);
        command.Parameters.AddWithValue("$normalized_path", normalizedPath);
        command.Parameters.AddWithValue("$object_kind", ToStorageObjectKind(deleteEvent.ObjectKind));
        AddNullable(command, "$process_id", deleteEvent.ProcessId);
        AddNullable(command, "$process_path", deleteEvent.ProcessPath);
        AddNullable(command, "$process_guid", deleteEvent.ProcessGuid);
        AddNullable(command, "$command_line", deleteEvent.CommandLine);
        AddNullable(command, "$parent_process_id", deleteEvent.ParentProcessId);
        AddNullable(command, "$parent_process_path", deleteEvent.ParentProcessPath);
        AddNullable(command, "$parent_process_guid", deleteEvent.ParentProcessGuid);
        AddNullable(command, "$user_name", deleteEvent.UserName);
        AddNullable(command, "$user_sid", deleteEvent.UserSid);
        command.Parameters.AddWithValue(
            "$delete_permission_type",
            ToStorageDeletePermission(deleteEvent.DeletePermission));
        command.Parameters.AddWithValue("$initial_risk", ToStorageRisk(assessment.RiskLevel));
        command.Parameters.AddWithValue(
            "$attribution_confidence",
            ToConfidenceScore(correlation.Confidence));
        command.Parameters.AddWithValue("$archive_expected", deleteEvent.ArchiveExpected ? 1 : 0);
        command.Parameters.AddWithValue("$missing_fields_json", missingFieldsJson);
        command.Parameters.Add("$content_sha256", SqliteType.Blob).Value = contentHash;
        command.Parameters.Add("$integrity_hash", SqliteType.Blob).Value = integrityHash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportDiagnostic?> TryInsertEventCorrelationAsync(
        CorrelationResult correlation,
        IReadOnlyDictionary<string, string> rawEventIds,
        DateTimeOffset createdUtc,
        long? recordNumber,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var processRawEventId = await ResolveRawEventIdAsync(
                correlation.MatchedProcessRawEventId,
                rawEventIds,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        var securityRawEventId = await ResolveRawEventIdAsync(
                correlation.MatchedSecurityRawEventId,
                rawEventIds,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        if (correlation.Method == CorrelationMethod.None)
        {
            processRawEventId = null;
            securityRawEventId = null;
        }
        else if (correlation.TimeDelta is null
                 || (processRawEventId is null && securityRawEventId is null))
        {
            return new ImportDiagnostic(
                "correlation_projection_missing_evidence",
                "The structured correlation row was skipped because its time delta or matched raw evidence was unavailable.",
                ImportDiagnosticSeverity.Warning,
                "persist",
                recordNumber);
        }

        if (correlation.Method == CorrelationMethod.PathAndTimeHeuristic
            && correlation.IdentityFieldsEnriched)
        {
            return new ImportDiagnostic(
                "correlation_projection_unsafe_enrichment",
                "A path/time heuristic correlation that claims identity enrichment was rejected.",
                ImportDiagnosticSeverity.Error,
                "persist",
                recordNumber);
        }

        long? timeDeltaMs = correlation.TimeDelta is null
            ? null
            : checked((long)Math.Round(
                correlation.TimeDelta.Value.TotalMilliseconds,
                MidpointRounding.AwayFromZero));
        if (timeDeltaMs < 0)
        {
            return new ImportDiagnostic(
                "correlation_projection_invalid_delta",
                "A negative correlation time delta was rejected.",
                ImportDiagnosticSeverity.Error,
                "persist",
                recordNumber);
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_correlations (
                event_correlation_id,
                delete_event_id,
                matched_process_raw_event_id,
                matched_security_raw_event_id,
                method,
                confidence,
                time_delta_ms,
                identity_fields_enriched,
                reasons_json,
                created_utc)
            VALUES (
                $event_correlation_id,
                $delete_event_id,
                $matched_process_raw_event_id,
                $matched_security_raw_event_id,
                $method,
                $confidence,
                $time_delta_ms,
                $identity_fields_enriched,
                $reasons_json,
                $created_utc)
            ON CONFLICT(delete_event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue(
            "$event_correlation_id",
            StableId("corr", correlation.Event.DeleteEventId));
        command.Parameters.AddWithValue("$delete_event_id", correlation.Event.DeleteEventId);
        AddNullable(command, "$matched_process_raw_event_id", processRawEventId);
        AddNullable(command, "$matched_security_raw_event_id", securityRawEventId);
        command.Parameters.AddWithValue("$method", ToStorageCorrelationMethod(correlation.Method));
        command.Parameters.AddWithValue(
            "$confidence",
            ToStorageCorrelationConfidence(correlation.Confidence));
        AddNullable(command, "$time_delta_ms", timeDeltaMs);
        command.Parameters.AddWithValue(
            "$identity_fields_enriched",
            correlation.IdentityFieldsEnriched ? 1 : 0);
        command.Parameters.AddWithValue(
            "$reasons_json",
            JsonSerializer.Serialize(correlation.Reasons));
        command.Parameters.AddWithValue("$created_utc", Format(createdUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task InsertPrimaryEvidenceAsync(
        string deleteEventId,
        string primaryRawEventId,
        DateTimeOffset createdUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string reasonsJson = """["primary_delete_event"]""";
        var integrityHash = HashFields(
            deleteEventId,
            primaryRawEventId,
            "primary_delete",
            100,
            "confirmed",
            reasonsJson,
            createdUtc);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_evidence (
                event_evidence_id,
                delete_event_id,
                source_raw_event_id,
                evidence_kind,
                correlation_score,
                correlation_confidence,
                reasons_json,
                created_utc,
                integrity_hash)
            VALUES (
                $event_evidence_id,
                $delete_event_id,
                $source_raw_event_id,
                'primary_delete',
                100,
                'confirmed',
                $reasons_json,
                $created_utc,
                $integrity_hash)
            ON CONFLICT(delete_event_id, source_raw_event_id, evidence_kind) DO NOTHING;
            """;
        command.Parameters.AddWithValue(
            "$event_evidence_id",
            StableId("evidence", deleteEventId, primaryRawEventId, "primary_delete"));
        command.Parameters.AddWithValue("$delete_event_id", deleteEventId);
        command.Parameters.AddWithValue("$source_raw_event_id", primaryRawEventId);
        command.Parameters.AddWithValue("$reasons_json", reasonsJson);
        command.Parameters.AddWithValue("$created_utc", Format(createdUtc));
        command.Parameters.Add("$integrity_hash", SqliteType.Blob).Value = integrityHash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertSessionMemberAsync(
        string deleteSessionId,
        string deleteEventId,
        DateTimeOffset addedUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var integrityHash = HashFields(deleteSessionId, deleteEventId, addedUtc);
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_members (
                delete_session_id,
                delete_event_id,
                added_utc,
                integrity_hash)
            VALUES (
                $delete_session_id,
                $delete_event_id,
                $added_utc,
                $integrity_hash)
            ON CONFLICT(delete_event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$delete_session_id", deleteSessionId);
        command.Parameters.AddWithValue("$delete_event_id", deleteEventId);
        command.Parameters.AddWithValue("$added_utc", Format(addedUtc));
        command.Parameters.Add("$integrity_hash", SqliteType.Blob).Value = integrityHash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertRiskAssessmentAsync(
        RiskAssessment assessment,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ruleCode = ToStorageRuleCode(assessment);
        var detailsJson = JsonSerializer.Serialize(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["originalRuleCode"] = assessment.RuleCode,
                ["protectedPathMatched"] = assessment.ProtectedPathMatched
            });
        var integrityHash = HashFields(
            assessment.RiskAssessmentId,
            assessment.DeleteSessionId,
            assessment.AssessedUtc,
            assessment.RiskLevel,
            ruleCode,
            assessment.ObservedCount,
            assessment.WindowStartUtc,
            assessment.WindowEndUtc,
            detailsJson);

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO risk_assessments (
                    risk_assessment_id,
                    subject_kind,
                    subject_id,
                    assessed_utc,
                    risk_level,
                    rule_code,
                    window_start_utc,
                    window_end_utc,
                    observed_count,
                    details_json,
                    integrity_hash)
                VALUES (
                    $risk_assessment_id,
                    'delete_session',
                    $subject_id,
                    $assessed_utc,
                    $risk_level,
                    $rule_code,
                    $window_start_utc,
                    $window_end_utc,
                    $observed_count,
                    $details_json,
                    $integrity_hash)
                ON CONFLICT(risk_assessment_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue(
                "$risk_assessment_id",
                assessment.RiskAssessmentId);
            command.Parameters.AddWithValue("$subject_id", assessment.DeleteSessionId);
            command.Parameters.AddWithValue("$assessed_utc", Format(assessment.AssessedUtc));
            command.Parameters.AddWithValue("$risk_level", ToStorageRisk(assessment.RiskLevel));
            command.Parameters.AddWithValue("$rule_code", ruleCode);
            command.Parameters.AddWithValue(
                "$window_start_utc",
                Format(assessment.WindowStartUtc));
            command.Parameters.AddWithValue(
                "$window_end_utc",
                Format(assessment.WindowEndUtc));
            command.Parameters.AddWithValue("$observed_count", assessment.ObservedCount);
            command.Parameters.AddWithValue("$details_json", detailsJson);
            command.Parameters.Add("$integrity_hash", SqliteType.Blob).Value = integrityHash;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO risk_assessment_subject_links (
                    risk_assessment_id,
                    delete_session_id,
                    delete_event_id)
                VALUES (
                    $risk_assessment_id,
                    $delete_session_id,
                    NULL)
                ON CONFLICT(risk_assessment_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue(
                "$risk_assessment_id",
                assessment.RiskAssessmentId);
            command.Parameters.AddWithValue("$delete_session_id", assessment.DeleteSessionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ValidateDeleteProjection(NormalizedDeleteEvent deleteEvent)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(deleteEvent.DeleteEventId))
        {
            missing.Add("deleteEventId");
        }

        if (string.IsNullOrWhiteSpace(deleteEvent.RawEventId))
        {
            missing.Add("rawEventId");
        }

        if (deleteEvent.EventRecordId is null)
        {
            missing.Add("eventRecordId");
        }

        if (string.IsNullOrWhiteSpace(deleteEvent.FullPath))
        {
            missing.Add("fullPath");
        }

        if (deleteEvent.SourceEventId is not (23 or 26))
        {
            missing.Add("supportedSourceEventId");
        }

        return missing.Count == 0
            ? null
            : $"The delete projection was skipped because required factual fields are absent or invalid: {string.Join(", ", missing)}.";
    }

    private static long? FindRecordNumber(
        IReadOnlyList<PreparedImportRecord> records,
        string rawEventId) =>
        records
            .FirstOrDefault(item =>
                string.Equals(
                    item.RawEvent?.RawEventId,
                    rawEventId,
                    StringComparison.Ordinal))
            ?.SourceRecord.RecordNumber;

    private static string NormalizeAuditPath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        return normalized.Length > 3 ? normalized.TrimEnd('\\') : normalized;
    }

    private static string ToStorageObjectKind(AuditObjectKind kind) => kind switch
    {
        AuditObjectKind.File => "file",
        AuditObjectKind.Directory => "directory",
        AuditObjectKind.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string ToStorageDeletePermission(DeletePermissionType permission) =>
        permission switch
        {
            DeletePermissionType.Delete => "delete",
            DeletePermissionType.DeleteChild => "delete_child",
            DeletePermissionType.DeleteAndDeleteChild => "delete_and_delete_child",
            DeletePermissionType.NotObserved => "not_observed",
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
        };

    private static string ToStorageRisk(AuditRiskLevel risk) => risk switch
    {
        AuditRiskLevel.Informational => "informational",
        AuditRiskLevel.Warning => "warning",
        AuditRiskLevel.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };

    private static int ToConfidenceScore(CorrelationConfidence confidence) =>
        confidence switch
        {
            CorrelationConfidence.None => 0,
            CorrelationConfidence.Low => 25,
            CorrelationConfidence.Medium => 75,
            CorrelationConfidence.High => 100,
            _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, null)
        };

    private static string ToStorageCorrelationMethod(CorrelationMethod method) => method switch
    {
        CorrelationMethod.None => "none",
        CorrelationMethod.ProcessGuid => "process_guid",
        CorrelationMethod.DevicePidUserAndTime => "device_pid_user_and_time",
        CorrelationMethod.PathAndTimeHeuristic => "path_and_time_heuristic",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
    };

    private static string ToStorageCorrelationConfidence(CorrelationConfidence confidence) =>
        confidence switch
        {
            CorrelationConfidence.None => "none",
            CorrelationConfidence.Low => "low",
            CorrelationConfidence.Medium => "medium",
            CorrelationConfidence.High => "high",
            _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, null)
        };

    private static string ToStorageRuleCode(RiskAssessment assessment)
    {
        if (assessment.ProtectedPathMatched)
        {
            return "protected_root";
        }

        return assessment.RiskLevel switch
        {
            AuditRiskLevel.Informational => "single_delete",
            AuditRiskLevel.Warning => "burst_30_in_10s",
            AuditRiskLevel.Critical => "burst_100_in_10s",
            _ => throw new ArgumentOutOfRangeException(
                nameof(assessment),
                assessment.RiskLevel,
                null)
        };
    }
}
