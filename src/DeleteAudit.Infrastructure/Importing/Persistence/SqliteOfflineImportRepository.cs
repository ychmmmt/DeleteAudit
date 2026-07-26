using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Importing.Persistence;

public sealed partial class SqliteOfflineImportRepository :
    IOfflineImportRepository,
    IDisposable
{
    private static readonly string[] RequiredTables =
    [
        "schema_migrations",
        "channel_epochs",
        "raw_events",
        "process_observations",
        "delete_sessions",
        "delete_events",
        "event_evidence",
        "session_members",
        "risk_assessments",
        "alerts",
        "protected_roots",
        "usn_checkpoints",
        "integrity_checkpoints",
        "import_sessions",
        "import_records",
        "import_diagnostics",
        "event_correlations",
        "risk_assessment_subject_links"
    ];

    private readonly SqliteConnection _connection;
    private readonly SqliteBusyRetryOptions _retryOptions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteOfflineImportRepository(
        SqliteConnection connection,
        SqliteBusyRetryOptions? retryOptions = null,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _retryOptions = retryOptions ?? SqliteBusyRetryOptions.Default;
        _retryOptions.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ValidateSchemaAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            await ExecuteWithBusyRetryAsync(
                async () =>
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = """
                        SELECT name
                        FROM sqlite_master
                        WHERE type = 'table';
                        """;

                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using var reader = await command
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        present.Add(reader.GetString(0));
                    }

                    var missing = RequiredTables
                        .Where(table => !present.Contains(table))
                        .ToArray();
                    if (missing.Length != 0)
                    {
                        throw new InvalidOperationException(
                            $"The DeleteAudit schema is missing required tables: {string.Join(", ", missing)}.");
                    }

                    return 0;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ImportSession?> FindBySha256Async(
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var hash = ParseSha256(sha256, nameof(sha256));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await ExecuteWithBusyRetryAsync(
                () => FindBySha256CoreAsync(hash, null, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlySet<string>> FindExistingDeleteEventIdsAsync(
        IReadOnlyCollection<string> deleteEventIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deleteEventIds);
        if (deleteEventIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Delete event IDs cannot be empty.",
                nameof(deleteEventIds));
        }

        var distinctIds = deleteEventIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (distinctIds.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await ExecuteWithBusyRetryAsync(
                    async () =>
                    {
                        var existing = new HashSet<string>(StringComparer.Ordinal);
                        using var command = _connection.CreateCommand();
                        command.CommandText = """
                            SELECT delete_event_id
                            FROM delete_events
                            WHERE delete_event_id = $delete_event_id
                            LIMIT 1;
                            """;
                        var parameter = command.Parameters.Add(
                            "$delete_event_id",
                            SqliteType.Text);

                        foreach (var deleteEventId in distinctIds)
                        {
                            parameter.Value = deleteEventId;
                            var value = await command
                                .ExecuteScalarAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (value is string existingId)
                            {
                                existing.Add(existingId);
                            }
                        }

                        return existing;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfflineImportCommitResult> CommitAsync(
        PreparedImport preparedImport,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedImport(preparedImport);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await ExecuteWithBusyRetryAsync(
                () => CommitTransactionAsync(preparedImport, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateOutputAsync(
        string importSessionId,
        ImportOutputUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(importSessionId))
        {
            throw new ArgumentException(
                "An import session ID is required.",
                nameof(importSessionId));
        }

        ArgumentNullException.ThrowIfNull(update);
        var status = ToStorageStatus(update.Status);
        var outputStatus = NormalizeOutputStatus(update.OutputStatus);
        var jsonlHash = update.JsonlOutputSha256 is null
            ? null
            : ParseSha256(update.JsonlOutputSha256, nameof(update.JsonlOutputSha256));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            await ExecuteWithBusyRetryAsync(
                async () =>
                {
                    using var transaction = _connection.BeginTransaction(deferred: false);
                    try
                    {
                        using var command = _connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = """
                            UPDATE import_sessions
                            SET status = $status,
                                output_status = $output_status,
                                jsonl_output_path = $jsonl_path,
                                jsonl_output_sha256 = $jsonl_sha256,
                                manifest_output_path = $manifest_path,
                                output_error_code = $output_error_code,
                                output_error_message = $output_error_message
                            WHERE import_session_id = $import_session_id;
                            """;
                        command.Parameters.AddWithValue("$status", status);
                        AddNullable(command, "$output_status", outputStatus);
                        AddNullable(command, "$jsonl_path", update.JsonlOutputPath);
                        AddNullable(command, "$jsonl_sha256", jsonlHash, SqliteType.Blob);
                        AddNullable(command, "$manifest_path", update.ManifestOutputPath);
                        AddNullable(command, "$output_error_code", update.Diagnostic?.Code);
                        AddNullable(command, "$output_error_message", update.Diagnostic?.Message);
                        command.Parameters.AddWithValue("$import_session_id", importSessionId);

                        if (await command
                                .ExecuteNonQueryAsync(cancellationToken)
                                .ConfigureAwait(false) != 1)
                        {
                            throw new InvalidOperationException(
                                $"Import session '{importSessionId}' was not found.");
                        }

                        if (update.Diagnostic is not null)
                        {
                            var state = new DiagnosticWriteState();
                            await InsertDiagnosticIfNewAsync(
                                    importSessionId,
                                    update.Diagnostic with { RecordNumber = null },
                                    new HashSet<long>(),
                                    state,
                                    transaction,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await transaction
                            .CommitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        await transaction
                            .RollbackAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        throw;
                    }

                    return 0;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<OfflineImportCommitResult> CommitTransactionAsync(
        PreparedImport preparedImport,
        CancellationToken cancellationToken)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);
        try
        {
            var sourceHash = ParseSha256(
                preparedImport.ImportSession.Sha256,
                nameof(preparedImport));
            var existing = await FindBySha256CoreAsync(
                    sourceHash,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new OfflineImportCommitResult(
                    ImportStatus.AlreadyImported,
                    existing with { Status = ImportStatus.AlreadyImported },
                    false,
                    0,
                    0);
            }

            await InsertImportSessionAsync(
                    preparedImport,
                    sourceHash,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            var chainState = await ReadChainTailAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
            var rawEventIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var processWrites = new List<(PreparedImportRecord Record, string RawEventId)>();
            var diagnosticState = new DiagnosticWriteState();
            var knownRecordNumbers = preparedImport.Records
                .Select(item => item.SourceRecord.RecordNumber)
                .ToHashSet();
            var insertedRawEvents = 0;

            foreach (var record in preparedImport.Records.OrderBy(
                         item => item.SourceRecord.RecordNumber))
            {
                var rawWrite = await TryPersistRawEventAsync(
                        preparedImport,
                        record,
                        chainState,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (rawWrite.RawEventId is not null && record.RawEvent is not null)
                {
                    rawEventIds[record.RawEvent.RawEventId] = rawWrite.RawEventId;
                }

                insertedRawEvents += rawWrite.WasInserted ? 1 : 0;
                await InsertImportRecordAsync(
                        preparedImport.ImportSession.ImportSessionId,
                        record,
                        rawWrite.RawEventId,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var diagnostic in record.SourceRecord.Diagnostics)
                {
                    await InsertDiagnosticIfNewAsync(
                            preparedImport.ImportSession.ImportSessionId,
                            diagnostic with
                            {
                                RecordNumber = diagnostic.RecordNumber
                                    ?? record.SourceRecord.RecordNumber
                            },
                            knownRecordNumbers,
                            diagnosticState,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (rawWrite.Diagnostic is not null)
                {
                    await InsertDiagnosticIfNewAsync(
                            preparedImport.ImportSession.ImportSessionId,
                            rawWrite.Diagnostic,
                            knownRecordNumbers,
                            diagnosticState,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (rawWrite.RawEventId is not null && record.ProcessContext is not null)
                {
                    processWrites.Add((record, rawWrite.RawEventId));
                }
            }

            foreach (var diagnostic in preparedImport.Diagnostics)
            {
                await InsertDiagnosticIfNewAsync(
                        preparedImport.ImportSession.ImportSessionId,
                        diagnostic,
                        knownRecordNumbers,
                        diagnosticState,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var processWrite in processWrites)
            {
                var diagnostic = await TryInsertProcessObservationAsync(
                        processWrite.Record,
                        processWrite.RawEventId,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (diagnostic is not null)
                {
                    await InsertDiagnosticIfNewAsync(
                            preparedImport.ImportSession.ImportSessionId,
                            diagnostic,
                            knownRecordNumbers,
                            diagnosticState,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var insertedDeleteEvents = await PersistDeleteProjectionsAsync(
                    preparedImport,
                    rawEventIds,
                    knownRecordNumbers,
                    diagnosticState,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            await FinalizeImportSessionAsync(
                    preparedImport.ImportSession,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new OfflineImportCommitResult(
                preparedImport.ImportSession.Status,
                preparedImport.ImportSession,
                true,
                insertedRawEvents,
                insertedDeleteEvents);
        }
        catch
        {
            await transaction
                .RollbackAsync(CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task InsertImportSessionAsync(
        PreparedImport preparedImport,
        byte[] sourceHash,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var session = preparedImport.ImportSession;
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO import_sessions (
                import_session_id,
                source_kind,
                original_file_name,
                normalized_source_path,
                file_size_bytes,
                source_last_write_utc,
                source_sha256,
                started_utc,
                completed_utc,
                total_record_count,
                success_record_count,
                ignored_record_count,
                error_record_count,
                application_version,
                schema_version,
                status)
            VALUES (
                $import_session_id,
                $source_kind,
                $original_file_name,
                $normalized_source_path,
                $file_size_bytes,
                $source_last_write_utc,
                $source_sha256,
                $started_utc,
                NULL,
                $total_record_count,
                $success_record_count,
                $ignored_record_count,
                $error_record_count,
                $application_version,
                $schema_version,
                'in_progress');
            """;
        command.Parameters.AddWithValue("$import_session_id", session.ImportSessionId);
        command.Parameters.AddWithValue("$source_kind", preparedImport.SourceKind);
        command.Parameters.AddWithValue("$original_file_name", session.OriginalFileName);
        command.Parameters.AddWithValue("$normalized_source_path", session.NormalizedAbsolutePath);
        command.Parameters.AddWithValue("$file_size_bytes", session.FileSize);
        command.Parameters.AddWithValue("$source_last_write_utc", Format(session.LastWriteUtc));
        command.Parameters.Add("$source_sha256", SqliteType.Blob).Value = sourceHash;
        command.Parameters.AddWithValue("$started_utc", Format(session.StartedUtc));
        command.Parameters.AddWithValue("$total_record_count", session.TotalRecordCount);
        command.Parameters.AddWithValue("$success_record_count", session.SuccessCount);
        command.Parameters.AddWithValue("$ignored_record_count", session.IgnoredCount);
        command.Parameters.AddWithValue("$error_record_count", session.ErrorCount);
        command.Parameters.AddWithValue("$application_version", session.ApplicationVersion);
        command.Parameters.AddWithValue("$schema_version", session.SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizeImportSessionAsync(
        ImportSession session,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE import_sessions
            SET completed_utc = $completed_utc,
                status = $status
            WHERE import_session_id = $import_session_id
              AND status = 'in_progress';
            """;
        command.Parameters.AddWithValue("$completed_utc", Format(session.EndedUtc));
        command.Parameters.AddWithValue("$status", ToStorageStatus(session.Status));
        command.Parameters.AddWithValue("$import_session_id", session.ImportSessionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Import session '{session.ImportSessionId}' could not be finalized.");
        }
    }

    private async Task<ImportSession?> FindBySha256CoreAsync(
        byte[] sourceHash,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                import_session_id,
                original_file_name,
                normalized_source_path,
                file_size_bytes,
                source_last_write_utc,
                source_sha256,
                started_utc,
                completed_utc,
                total_record_count,
                success_record_count,
                ignored_record_count,
                error_record_count,
                application_version,
                schema_version,
                status
            FROM import_sessions
            WHERE source_sha256 = $source_sha256
            LIMIT 1;
            """;
        command.Parameters.Add("$source_sha256", SqliteType.Blob).Value = sourceHash;

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(7))
        {
            throw new InvalidOperationException(
                "A committed import session cannot have an unfinished status.");
        }

        return new ImportSession(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            ParseTimestamp(reader.GetString(4)),
            Convert.ToHexString((byte[])reader.GetValue(5)).ToLowerInvariant(),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetInt32(13),
            ParseStorageStatus(reader.GetString(14)));
    }

    private async Task<T> ExecuteWithBusyRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException exception)
                when (IsBusyOrLocked(exception) && attempt < _retryOptions.MaxAttempts)
            {
                await Task
                    .Delay(_retryOptions.Delay, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void EnsureOpen()
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The repository requires an already-open SQLite connection.");
        }
    }

    private static void ValidatePreparedImport(PreparedImport preparedImport)
    {
        ArgumentNullException.ThrowIfNull(preparedImport);
        ArgumentNullException.ThrowIfNull(preparedImport.ImportSession);
        ArgumentNullException.ThrowIfNull(preparedImport.Records);
        ArgumentNullException.ThrowIfNull(preparedImport.DeleteProjections);
        ArgumentNullException.ThrowIfNull(preparedImport.Diagnostics);

        if (preparedImport.SourceKind is not ("multi_xml" or "evtx"))
        {
            throw new ArgumentException(
                "SourceKind must be 'multi_xml' or 'evtx'.",
                nameof(preparedImport));
        }

        var session = preparedImport.ImportSession;
        if (string.IsNullOrWhiteSpace(session.ImportSessionId)
            || string.IsNullOrWhiteSpace(session.OriginalFileName)
            || string.IsNullOrWhiteSpace(session.NormalizedAbsolutePath)
            || string.IsNullOrWhiteSpace(session.ApplicationVersion))
        {
            throw new ArgumentException(
                "The import session metadata is incomplete.",
                nameof(preparedImport));
        }

        _ = ParseSha256(session.Sha256, nameof(preparedImport));
        if (session.Status == ImportStatus.AlreadyImported)
        {
            throw new ArgumentException(
                "An already-imported result cannot be committed as a new import.",
                nameof(preparedImport));
        }

        if (session.EndedUtc < session.StartedUtc)
        {
            throw new ArgumentException(
                "The import end time cannot precede its start time.",
                nameof(preparedImport));
        }

        var recordNumbers = new HashSet<long>();
        foreach (var record in preparedImport.Records)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(record.SourceRecord);
            if (record.SourceRecord.RecordNumber <= 0
                || !recordNumbers.Add(record.SourceRecord.RecordNumber))
            {
                throw new ArgumentException(
                    "Import record numbers must be positive and unique.",
                    nameof(preparedImport));
            }

            var hasXml = record.SourceRecord.RawXml is not null;
            if ((record.SourceRecord.State == OfflineRecordState.Available) != hasXml)
            {
                throw new ArgumentException(
                    "Offline record state and raw XML availability disagree.",
                    nameof(preparedImport));
            }

            if (record.RawEvent is not null
                && !string.Equals(
                    record.SourceRecord.RawXml,
                    record.RawEvent.RawXml,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A parsed raw event must retain the exact source XML.",
                    nameof(preparedImport));
            }

            if (record.ProcessContext is not null
                && (record.RawEvent is null
                    || !string.Equals(
                        record.ProcessContext.RawEventId,
                        record.RawEvent.RawEventId,
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "A process context must refer to its prepared raw event.",
                    nameof(preparedImport));
            }
        }

        var succeeded = preparedImport.Records.Count(
            item => item.Outcome == ImportRecordOutcome.Succeeded);
        var ignored = preparedImport.Records.Count(
            item => item.Outcome == ImportRecordOutcome.Ignored);
        var errors = preparedImport.Records.Count(
            item => item.Outcome == ImportRecordOutcome.Error);
        if (session.TotalRecordCount != preparedImport.Records.Count
            || session.SuccessCount != succeeded
            || session.IgnoredCount != ignored
            || session.ErrorCount != errors)
        {
            throw new ArgumentException(
                "Import session counts do not match the prepared records.",
                nameof(preparedImport));
        }
    }

    private static string ToStorageStatus(ImportStatus status) => status switch
    {
        ImportStatus.Completed => "completed",
        ImportStatus.PartialFailure => "partial_failure",
        ImportStatus.Failed => "failed",
        ImportStatus.AlreadyImported => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "AlreadyImported is a result status, not a persisted session state."),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static ImportStatus ParseStorageStatus(string status) => status switch
    {
        "completed" => ImportStatus.Completed,
        "partial_failure" => ImportStatus.PartialFailure,
        "failed" => ImportStatus.Failed,
        _ => throw new InvalidOperationException(
            $"Unsupported persisted import status '{status}'.")
    };

    private static string? NormalizeOutputStatus(string? status)
    {
        if (status is null)
        {
            return null;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "prepared" or "complete" or "failed"
            ? normalized
            : throw new ArgumentException(
                "OutputStatus must be prepared, complete, failed, or null.",
                nameof(status));
    }

    private static byte[] ParseSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            throw new ArgumentException(
                "A SHA-256 value must contain 64 hexadecimal characters.",
                parameterName);
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 32)
            {
                throw new FormatException();
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A SHA-256 value must contain 64 hexadecimal characters.",
                parameterName,
                exception);
        }
    }

    private static bool IsBusyOrLocked(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static byte[] HashUtf8(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static byte[] HashFields(params object?[] values)
    {
        var material = string.Join(
            "\u001f",
            values.Select(value => value switch
            {
                null => "<null>",
                DateTimeOffset timestamp => Format(timestamp),
                bool boolean => boolean ? "1" : "0",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>"
            }));
        return HashUtf8(material);
    }

    private static string StableId(string prefix, params object?[] values) =>
        $"{prefix}-{Convert.ToHexString(HashFields(values)).ToLowerInvariant()}";

    private static void AddNullable(
        SqliteCommand command,
        string name,
        object? value,
        SqliteType? type = null)
    {
        var parameter = type is null
            ? command.Parameters.AddWithValue(name, value ?? DBNull.Value)
            : command.Parameters.Add(name, type.Value);
        parameter.Value = value ?? DBNull.Value;
    }

    private sealed class DiagnosticWriteState
    {
        public int Sequence { get; set; }

        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }
}
