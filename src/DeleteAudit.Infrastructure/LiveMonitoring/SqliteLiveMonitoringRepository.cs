using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.LiveMonitoring;

/// <summary>
/// Appends live monitoring sessions and live captured evidence. Opens the database
/// ReadWrite without Create: a missing database is a visible failure, never a silently
/// created file. This type never applies a migration and never issues a destructive
/// statement.
/// </summary>
/// <remarks>
/// Every method runs on a background workflow or a lifecycle transition. None of them is
/// reachable from a watcher delivery callback — that thread performs no database I/O.
/// </remarks>
public sealed class SqliteLiveMonitoringRepository : ILiveMonitoringRepository
{
    /// <summary>Phase 2A summary tables, from 0003.</summary>
    private static readonly string[] SummaryTables =
    [
        "live_monitoring_sessions",
        "live_monitoring_channels",
        "live_monitoring_diagnostics"
    ];

    /// <summary>Phase 2B.1 live evidence tables, from 0004.</summary>
    private static readonly string[] EvidenceTables =
    [
        "live_capture_sessions",
        "live_capture_records",
        "live_capture_completions"
    ];

    private readonly ViewerDataLocation _location;

    public SqliteLiveMonitoringRepository(ViewerDataLocation location)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public async Task ValidateSchemaAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = RequireDatabase();

        await using var connection = _location.CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
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

        // Reported separately so the message names the migration that is actually
        // missing instead of a generic "schema is wrong".
        var missingSummary = SummaryTables
            .Where(table => !present.Contains(table))
            .ToArray();
        if (missingSummary.Length != 0)
        {
            throw new InvalidOperationException(
                "The live monitoring schema increment is missing required tables: "
                + $"{string.Join(", ", missingSummary)}. Apply db/migrations/0003_phase_2a_live_monitoring.sql explicitly.");
        }

        var missingEvidence = EvidenceTables
            .Where(table => !present.Contains(table))
            .ToArray();
        if (missingEvidence.Length != 0)
        {
            throw new InvalidOperationException(
                "The live evidence schema increment is missing required tables: "
                + $"{string.Join(", ", missingEvidence)}. Apply db/migrations/0004_phase_2b_live_evidence.sql explicitly.");
        }

        _ = databasePath;
    }

    public async Task StartCaptureSessionAsync(
        LiveCaptureSessionStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.LiveSessionId);
        if (start.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                start.QueueCapacity,
                "The queue capacity must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(start.ApplicationVersion);

        await using var connection = await OpenWritableAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                // Plain INSERT on purpose: a duplicate session id is a real defect and
                // must surface, never be ignored or replaced.
                command.CommandText = """
                    INSERT INTO live_capture_sessions (
                        live_session_id,
                        started_utc,
                        queue_capacity,
                        application_version)
                    VALUES (
                        $live_session_id,
                        $started_utc,
                        $queue_capacity,
                        $application_version);
                    """;
                command.Parameters.Add("$live_session_id", SqliteType.Text).Value =
                    start.LiveSessionId;
                command.Parameters.Add("$started_utc", SqliteType.Text).Value =
                    Format(start.StartedUtc);
                command.Parameters.Add("$queue_capacity", SqliteType.Integer).Value =
                    start.QueueCapacity;
                command.Parameters.Add("$application_version", SqliteType.Text).Value =
                    start.ApplicationVersion;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Explicit and symmetric with the other write paths. A rollback failure must
            // never replace the original exception, so it is swallowed on purpose here.
            try
            {
                await transaction
                    .RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                _ = rollbackFailure;
            }

            throw;
        }
    }

    public async Task AppendRecordsAsync(
        IReadOnlyList<LiveCaptureRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            throw new ArgumentException(
                "A batch must contain at least one record.",
                nameof(records));
        }

        if (records.Count > LiveMonitoringLimits.MaxCaptureBatchRecords)
        {
            throw new ArgumentException(
                $"A batch may hold at most {LiveMonitoringLimits.MaxCaptureBatchRecords} records.",
                nameof(records));
        }

        var sessionId = records[0].LiveSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException(
                "Every record must carry a live session id.",
                nameof(records));
        }

        // Everything below runs before a connection is opened, so a rejected batch never
        // reaches SQLite at all. The repository verifies the caller's claims rather than
        // trusting them: it never repairs or substitutes what it was handed.
        long previousSequence = 0;
        foreach (var record in records)
        {
            if (!string.Equals(record.LiveSessionId, sessionId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A batch may not span more than one live session.",
                    nameof(records));
            }

            if (record.ReceivedSequence <= previousSequence)
            {
                throw new ArgumentException(
                    "Records must be ordered by a strictly increasing received sequence.",
                    nameof(records));
            }

            // The evidence id is derived, not free-form: it must be exactly the session
            // and the receive position it claims. SQLite can only enforce uniqueness, so
            // this consistency is checked here.
            var expectedId = LiveEvidenceIdentity.Create(
                record.LiveSessionId,
                record.ReceivedSequence);
            if (!string.Equals(record.LiveEvidenceId, expectedId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A record's live evidence id does not match its session and received sequence.",
                    nameof(records));
            }

            // The digest must belong to the XML actually being stored. Recomputed here so
            // a wrong digest can never be committed alongside the evidence it describes.
            var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(record.RawXml));
            if (!CryptographicOperations.FixedTimeEquals(
                    record.RawXmlSha256 ?? [],
                    expectedDigest))
            {
                throw new ArgumentException(
                    "A record's raw XML digest does not match its raw XML.",
                    nameof(records));
            }

            previousSequence = record.ReceivedSequence;
        }

        await using var connection = await OpenWritableAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            foreach (var record in records)
            {
                using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                // Plain INSERT: a duplicate evidence id or sequence aborts the whole
                // batch. Neither OR IGNORE nor OR REPLACE is acceptable for evidence.
                command.CommandText = """
                    INSERT INTO live_capture_records (
                        live_evidence_id,
                        live_session_id,
                        received_sequence,
                        event_record_id,
                        provider_name,
                        channel_name,
                        machine_name,
                        time_created_utc,
                        observed_utc,
                        raw_xml,
                        raw_xml_sha256,
                        parser_raw_event_id,
                        parsed_event_id,
                        outcome,
                        error_code,
                        detail)
                    VALUES (
                        $live_evidence_id,
                        $live_session_id,
                        $received_sequence,
                        $event_record_id,
                        $provider_name,
                        $channel_name,
                        $machine_name,
                        $time_created_utc,
                        $observed_utc,
                        $raw_xml,
                        $raw_xml_sha256,
                        $parser_raw_event_id,
                        $parsed_event_id,
                        $outcome,
                        $error_code,
                        $detail);
                    """;
                command.Parameters.Add("$live_evidence_id", SqliteType.Text).Value =
                    record.LiveEvidenceId;
                command.Parameters.Add("$live_session_id", SqliteType.Text).Value =
                    record.LiveSessionId;
                command.Parameters.Add("$received_sequence", SqliteType.Integer).Value =
                    record.ReceivedSequence;
                command.Parameters.Add("$event_record_id", SqliteType.Integer).Value =
                    ToDb(record.EventRecordId);
                command.Parameters.Add("$provider_name", SqliteType.Text).Value =
                    ToDb(record.ProviderName);
                command.Parameters.Add("$channel_name", SqliteType.Text).Value =
                    record.ChannelName;
                command.Parameters.Add("$machine_name", SqliteType.Text).Value =
                    ToDb(record.MachineName);
                command.Parameters.Add("$time_created_utc", SqliteType.Text).Value =
                    record.TimeCreatedUtc is null
                        ? DBNull.Value
                        : Format(record.TimeCreatedUtc.Value);
                command.Parameters.Add("$observed_utc", SqliteType.Text).Value =
                    Format(record.ObservedUtc);
                command.Parameters.Add("$raw_xml", SqliteType.Text).Value = record.RawXml;
                command.Parameters.Add("$raw_xml_sha256", SqliteType.Blob).Value =
                    record.RawXmlSha256;
                command.Parameters.Add("$parser_raw_event_id", SqliteType.Text).Value =
                    ToDb(record.ParserRawEventId);
                command.Parameters.Add("$parsed_event_id", SqliteType.Integer).Value =
                    ToDb(record.ParsedEventId);
                command.Parameters.Add("$outcome", SqliteType.Text).Value =
                    ToStorageOutcome(record.Outcome);
                command.Parameters.Add("$error_code", SqliteType.Text).Value =
                    ToDb(LiveMonitoringLimits.TruncateErrorCode(record.ErrorCode));
                command.Parameters.Add("$detail", SqliteType.Text).Value =
                    ToDb(LiveMonitoringLimits.TruncateDetail(record.Detail));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // All or nothing: a half-written batch would leave evidence that claims a
            // continuity the capture never had.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteSessionAsync(
        LiveCaptureCompletion completion,
        LiveMonitoringSession session,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!string.Equals(
                completion.LiveSessionId,
                session.LiveSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The completion and the session summary must describe the same live session.",
                nameof(completion));
        }

        if (!completion.IsConsistent)
        {
            throw new ArgumentException(
                "The live capture completion counts are inconsistent; refusing to persist them.",
                nameof(completion));
        }

        if (!session.Counters.IsBalanced)
        {
            throw new ArgumentException(
                "The live monitoring counters are not balanced; refusing to persist an inconsistent session.",
                nameof(session));
        }

        await using var connection = await OpenWritableAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await RequireStartedAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    completion.LiveSessionId,
                    cancellationToken)
                .ConfigureAwait(false);

            await InsertCompletionAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    completion,
                    cancellationToken)
                .ConfigureAwait(false);

            // The Phase 2A summary is written in the same transaction, so evidence
            // completion and summary can never disagree about how the session ended.
            await InsertSummaryAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    session,
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RequireStartedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string liveSessionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM live_capture_sessions
            WHERE live_session_id = $live_session_id;
            """;
        command.Parameters.Add("$live_session_id", SqliteType.Text).Value = liveSessionId;
        var started = (long)(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L);
        if (started == 0)
        {
            throw new InvalidOperationException(
                $"No live capture session '{liveSessionId}' was started; refusing to complete it.");
        }
    }

    private static async Task InsertCompletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LiveCaptureCompletion completion,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The primary key makes a second completion for the same session an error rather
        // than an overwrite.
        command.CommandText = """
            INSERT INTO live_capture_completions (
                live_session_id,
                stopped_utc,
                final_state,
                received_count,
                delete_fact_count,
                process_context_count,
                security_evidence_count,
                ignored_count,
                error_count,
                dropped_count,
                late_discarded_count,
                suppressed_diagnostic_count,
                persisted_record_count)
            VALUES (
                $live_session_id,
                $stopped_utc,
                $final_state,
                $received_count,
                $delete_fact_count,
                $process_context_count,
                $security_evidence_count,
                $ignored_count,
                $error_count,
                $dropped_count,
                $late_discarded_count,
                $suppressed_diagnostic_count,
                $persisted_record_count);
            """;
        var counters = completion.Counters;
        command.Parameters.Add("$live_session_id", SqliteType.Text).Value =
            completion.LiveSessionId;
        command.Parameters.Add("$stopped_utc", SqliteType.Text).Value =
            Format(completion.StoppedUtc);
        command.Parameters.Add("$final_state", SqliteType.Text).Value =
            ToStorageState(completion.FinalState);
        command.Parameters.Add("$received_count", SqliteType.Integer).Value =
            counters.Received;
        command.Parameters.Add("$delete_fact_count", SqliteType.Integer).Value =
            counters.DeleteFact;
        command.Parameters.Add("$process_context_count", SqliteType.Integer).Value =
            counters.ProcessContext;
        command.Parameters.Add("$security_evidence_count", SqliteType.Integer).Value =
            counters.SecurityEvidence;
        command.Parameters.Add("$ignored_count", SqliteType.Integer).Value =
            counters.Ignored;
        command.Parameters.Add("$error_count", SqliteType.Integer).Value = counters.Error;
        command.Parameters.Add("$dropped_count", SqliteType.Integer).Value =
            counters.Dropped;
        command.Parameters.Add("$late_discarded_count", SqliteType.Integer).Value =
            counters.LateDiscarded;
        command.Parameters.Add("$suppressed_diagnostic_count", SqliteType.Integer).Value =
            counters.SuppressedDiagnostics;
        command.Parameters.Add("$persisted_record_count", SqliteType.Integer).Value =
            completion.PersistedRecordCount;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LiveMonitoringSession session,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO live_monitoring_sessions (
                    live_session_id,
                    started_utc,
                    stopped_utc,
                    final_state,
                    received_count,
                    delete_fact_count,
                    process_context_count,
                    security_evidence_count,
                    ignored_count,
                    error_count,
                    dropped_count,
                    late_discarded_count,
                    suppressed_diagnostic_count,
                    queue_capacity,
                    application_version)
                VALUES (
                    $live_session_id,
                    $started_utc,
                    $stopped_utc,
                    $final_state,
                    $received_count,
                    $delete_fact_count,
                    $process_context_count,
                    $security_evidence_count,
                    $ignored_count,
                    $error_count,
                    $dropped_count,
                    $late_discarded_count,
                    $suppressed_diagnostic_count,
                    $queue_capacity,
                    $application_version);
                """;
            command.Parameters.Add("$live_session_id", SqliteType.Text).Value =
                session.LiveSessionId;
            command.Parameters.Add("$started_utc", SqliteType.Text).Value =
                Format(session.StartedUtc);
            command.Parameters.Add("$stopped_utc", SqliteType.Text).Value =
                session.StoppedUtc is null
                    ? DBNull.Value
                    : Format(session.StoppedUtc.Value);
            command.Parameters.Add("$final_state", SqliteType.Text).Value =
                ToStorageState(session.FinalState);
            command.Parameters.Add("$received_count", SqliteType.Integer).Value =
                session.Counters.Received;
            command.Parameters.Add("$delete_fact_count", SqliteType.Integer).Value =
                session.Counters.DeleteFact;
            command.Parameters.Add("$process_context_count", SqliteType.Integer).Value =
                session.Counters.ProcessContext;
            command.Parameters.Add("$security_evidence_count", SqliteType.Integer).Value =
                session.Counters.SecurityEvidence;
            command.Parameters.Add("$ignored_count", SqliteType.Integer).Value =
                session.Counters.Ignored;
            command.Parameters.Add("$error_count", SqliteType.Integer).Value =
                session.Counters.Error;
            command.Parameters.Add("$dropped_count", SqliteType.Integer).Value =
                session.Counters.Dropped;
            command.Parameters.Add("$late_discarded_count", SqliteType.Integer).Value =
                session.Counters.LateDiscarded;
            command.Parameters.Add("$suppressed_diagnostic_count", SqliteType.Integer)
                .Value = session.Counters.SuppressedDiagnostics;
            command.Parameters.Add("$queue_capacity", SqliteType.Integer).Value =
                session.QueueCapacity;
            command.Parameters.Add("$application_version", SqliteType.Text).Value =
                session.ApplicationVersion;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var status in session.ChannelStatuses)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO live_monitoring_channels (
                    live_session_id,
                    channel_name,
                    availability,
                    detail)
                VALUES (
                    $live_session_id,
                    $channel_name,
                    $availability,
                    $detail);
                """;
            command.Parameters.Add("$live_session_id", SqliteType.Text).Value =
                session.LiveSessionId;
            command.Parameters.Add("$channel_name", SqliteType.Text).Value =
                status.ChannelName;
            command.Parameters.Add("$availability", SqliteType.Text).Value =
                ToStorageAvailability(status.Availability);
            command.Parameters.Add("$detail", SqliteType.Text).Value =
                ToDb(status.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var ordinal = 0;
        // Hard cap at the storage boundary as well, so a regression in the service can
        // never write an unbounded number of rows for one session.
        foreach (var diagnostic in diagnostics.Take(LiveMonitoringLimits.MaxDiagnostics))
        {
            ordinal++;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO live_monitoring_diagnostics (
                    live_diagnostic_id,
                    live_session_id,
                    stage,
                    severity,
                    code,
                    message,
                    occurred_utc)
                VALUES (
                    $live_diagnostic_id,
                    $live_session_id,
                    $stage,
                    $severity,
                    $code,
                    $message,
                    $occurred_utc);
                """;
            command.Parameters.Add("$live_diagnostic_id", SqliteType.Text).Value =
                $"{session.LiveSessionId}:{ordinal.ToString(CultureInfo.InvariantCulture)}";
            command.Parameters.Add("$live_session_id", SqliteType.Text).Value =
                session.LiveSessionId;
            command.Parameters.Add("$stage", SqliteType.Text).Value = diagnostic.Stage;
            command.Parameters.Add("$severity", SqliteType.Text).Value =
                ToStorageSeverity(diagnostic.Severity);
            command.Parameters.Add("$code", SqliteType.Text).Value = diagnostic.Code;
            command.Parameters.Add("$message", SqliteType.Text).Value =
                LiveMonitoringLimits.TruncateMessage(diagnostic.Message);
            command.Parameters.Add("$occurred_utc", SqliteType.Text).Value =
                Format(diagnostic.OccurredUtc);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenWritableAsync(
        CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabase();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // ReadWrite without Create: this type never brings a database into existence.
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string RequireDatabase()
    {
        var databasePath = _location.EnsureDatabasePath();
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "The viewer database does not exist. Schema creation is an explicit external step.");
        }

        return databasePath;
    }

    private static object ToDb(string? value) =>
        value is null ? DBNull.Value : value;

    private static object ToDb(long? value) =>
        value is null ? DBNull.Value : value.Value;

    private static object ToDb(int? value) =>
        value is null ? DBNull.Value : value.Value;

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string ToStorageOutcome(LiveEventOutcome outcome) => outcome switch
    {
        LiveEventOutcome.DeleteFact => "delete_fact",
        LiveEventOutcome.ProcessContext => "process_context",
        LiveEventOutcome.SecurityEvidence => "security_evidence",
        LiveEventOutcome.Ignored => "ignored",
        LiveEventOutcome.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToStorageState(LiveMonitoringState state) => state switch
    {
        LiveMonitoringState.Stopped => "stopped",
        LiveMonitoringState.Error => "error",
        _ => throw new ArgumentOutOfRangeException(
            nameof(state),
            state,
            "Only a finished session (stopped or error) can be persisted.")
    };

    private static string ToStorageAvailability(LiveChannelAvailability availability) =>
        availability switch
        {
            LiveChannelAvailability.Available => "available",
            LiveChannelAvailability.Unavailable => "unavailable",
            LiveChannelAvailability.AccessDenied => "access_denied",
            LiveChannelAvailability.Disabled => "disabled",
            LiveChannelAvailability.UnknownError => "unknown_error",
            _ => throw new ArgumentOutOfRangeException(
                nameof(availability),
                availability,
                null)
        };

    private static string ToStorageSeverity(ImportDiagnosticSeverity severity) =>
        severity switch
        {
            ImportDiagnosticSeverity.Information => "info",
            ImportDiagnosticSeverity.Warning => "warning",
            ImportDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
        };
}
