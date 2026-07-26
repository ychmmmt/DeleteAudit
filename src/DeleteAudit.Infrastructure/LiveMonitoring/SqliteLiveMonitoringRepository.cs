using System.Globalization;
using DeleteAudit.Application.LiveMonitoring;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.LiveMonitoring;

/// <summary>
/// Appends live monitoring session records. Opens the database ReadWrite without
/// Create: a missing database is a visible failure, never a silently created file.
/// </summary>
public sealed class SqliteLiveMonitoringRepository : ILiveMonitoringRepository
{
    private static readonly string[] RequiredTables =
    [
        "live_monitoring_sessions",
        "live_monitoring_channels",
        "live_monitoring_diagnostics"
    ];

    private readonly ViewerDataLocation _location;

    public SqliteLiveMonitoringRepository(ViewerDataLocation location)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public async Task ValidateSchemaAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = _location.EnsureDatabasePath();
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "The viewer database does not exist. Schema creation is an explicit external step.");
        }

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

        var missing = RequiredTables
            .Where(table => !present.Contains(table))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "The live monitoring schema increment is missing required tables: "
                + $"{string.Join(", ", missing)}. Apply db/migrations/0003_phase_2a_live_monitoring.sql explicitly.");
        }
    }

    public async Task SaveSessionAsync(
        LiveMonitoringSession session,
        IReadOnlyList<LiveMonitoringDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!session.Counters.IsBalanced)
        {
            throw new ArgumentException(
                "The live monitoring counters are not balanced; refusing to persist an inconsistent session.",
                nameof(session));
        }

        var databasePath = _location.EnsureDatabasePath();
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "The viewer database does not exist. Schema creation is an explicit external step.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
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
            command.Transaction = (SqliteTransaction)transaction;
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
                status.Detail is null ? DBNull.Value : status.Detail;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var ordinal = 0;
        // Hard cap at the storage boundary as well, so a regression in the service can
        // never write an unbounded number of rows for one session.
        foreach (var diagnostic in diagnostics.Take(LiveMonitoringLimits.MaxDiagnostics))
        {
            ordinal++;
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

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
