using System.Globalization;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Viewing;

/// <summary>
/// Reads what a live capture session recorded. Every connection is opened ReadOnly, so
/// this type cannot create a database, apply a migration, or alter a single stored byte.
/// </summary>
/// <remarks>
/// Its readiness check is intentionally independent of
/// <see cref="SqliteViewerQueryService"/>'s: a database that predates the live evidence
/// migration must keep every offline page working and fail closed only on this page.
/// </remarks>
public sealed class SqliteLiveHistoryQueryService : ILiveHistoryQueryService
{
    /// <summary>Tables from 0004, plus the 0003 table that holds session diagnostics.</summary>
    private static readonly string[] RequiredTables =
    [
        "live_capture_sessions",
        "live_capture_records",
        "live_capture_completions",
        "live_monitoring_diagnostics"
    ];

    private const string SessionFilter = """
        FROM live_capture_sessions AS s
        LEFT JOIN live_capture_completions AS c
            ON c.live_session_id = s.live_session_id
        WHERE ($from_utc IS NULL OR s.started_utc >= $from_utc)
          AND ($to_utc IS NULL OR s.started_utc < $to_utc)
          AND ($state IS NULL
               OR ($state = 'incomplete' AND c.live_session_id IS NULL)
               OR ($state <> 'incomplete' AND c.final_state = $state))
        """;

    private const string RecordFilter = """
        FROM live_capture_records AS r
        WHERE r.live_session_id = $session
          AND ($from_utc IS NULL OR r.observed_utc >= $from_utc)
          AND ($to_utc IS NULL OR r.observed_utc < $to_utc)
          AND ($outcome IS NULL OR r.outcome = $outcome)
          AND ($errors_only = 0 OR r.outcome = 'error' OR r.error_code IS NOT NULL)
          AND ($provider IS NULL
               OR r.provider_name COLLATE NOCASE LIKE $provider ESCAPE '\')
          AND ($channel IS NULL
               OR r.channel_name COLLATE NOCASE LIKE $channel ESCAPE '\')
          AND ($error_code IS NULL
               OR r.error_code COLLATE NOCASE LIKE $error_code ESCAPE '\')
          AND ($parsed_event_id IS NULL OR r.parsed_event_id = $parsed_event_id)
          AND ($event_record_id IS NULL OR r.event_record_id = $event_record_id)
          AND ($min_sequence IS NULL OR r.received_sequence >= $min_sequence)
          AND ($max_sequence IS NULL OR r.received_sequence <= $max_sequence)
        """;

    private readonly ViewerDataLocation _location;

    public SqliteLiveHistoryQueryService(ViewerDataLocation location)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public async Task<LiveHistoryAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = _location.EnsureDatabasePath();
        if (!File.Exists(databasePath))
        {
            return new LiveHistoryAvailability(
                LiveHistoryState.MissingDatabase,
                "查看器数据库尚不存在。创建数据库与应用 migration 是明确的手动步骤。",
                []);
        }

        try
        {
            await using var connection = _location.CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name FROM main.sqlite_master WHERE type = 'table';
                """;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    present.Add(reader.GetString(0));
                }
            }

            var missing = RequiredTables
                .Where(table => !present.Contains(table))
                .ToArray();
            return missing.Length == 0
                ? new LiveHistoryAvailability(
                    LiveHistoryState.Ready,
                    "实时接入历史可以查看。",
                    [])
                : new LiveHistoryAvailability(
                    LiveHistoryState.MissingSchema,
                    "实时接入历史需要的表尚未创建："
                    + $"{string.Join("、", missing)}。请显式应用 "
                    + "db/migrations/0003_phase_2a_live_monitoring.sql 与 "
                    + "db/migrations/0004_phase_2b_live_evidence.sql。",
                    missing);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return Inaccessible(exception.Message);
        }
        catch (IOException exception)
        {
            return Inaccessible(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Inaccessible(exception.Message);
        }
    }

    public async Task<PageResult<LiveCaptureSessionRow>> GetSessionsAsync(
        LiveHistorySessionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        long totalCount;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) {SessionFilter};";
            AddSessionParameters(command, query);
            totalCount = await ReadCountAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }

        var rows = new List<LiveCaptureSessionRow>();
        using (var command = connection.CreateCommand())
        {
            // Newest first, then by id so equal timestamps still produce a total order
            // and a page boundary can never repeat or skip a session.
            command.CommandText = $"""
                SELECT
                    s.live_session_id,
                    s.started_utc,
                    s.queue_capacity,
                    s.application_version,
                    c.stopped_utc,
                    c.final_state,
                    c.received_count,
                    c.delete_fact_count,
                    c.process_context_count,
                    c.security_evidence_count,
                    c.ignored_count,
                    c.error_count,
                    c.dropped_count,
                    c.late_discarded_count,
                    c.suppressed_diagnostic_count,
                    c.persisted_record_count,
                    (SELECT COUNT(*)
                       FROM live_capture_records AS r
                      WHERE r.live_session_id = s.live_session_id)
                {SessionFilter}
                ORDER BY s.started_utc DESC, s.live_session_id DESC
                LIMIT $limit OFFSET $offset;
                """;
            AddSessionParameters(command, query);
            command.Parameters.Add("$limit", SqliteType.Integer).Value = query.Page.Limit;
            command.Parameters.Add("$offset", SqliteType.Integer).Value = query.Page.Offset;

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new LiveCaptureSessionRow(
                    reader.GetString(0),
                    ParseTimestamp(reader.GetString(1)),
                    ReadNullableTimestamp(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(3),
                    reader.GetInt64(2),
                    ReadCount(reader, 6),
                    ReadCount(reader, 7),
                    ReadCount(reader, 8),
                    ReadCount(reader, 9),
                    ReadCount(reader, 10),
                    ReadCount(reader, 11),
                    ReadCount(reader, 12),
                    ReadCount(reader, 13),
                    ReadCount(reader, 14),
                    ReadCount(reader, 15),
                    reader.GetInt64(16)));
            }
        }

        return new PageResult<LiveCaptureSessionRow>(
            rows,
            totalCount,
            query.Page.Offset,
            query.Page.Limit);
    }

    public async Task<PageResult<LiveCaptureRecordRow>> GetRecordsAsync(
        LiveHistoryRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        long totalCount;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) {RecordFilter};";
            AddRecordParameters(command, query);
            totalCount = await ReadCountAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }

        var rows = new List<LiveCaptureRecordRow>();
        using (var command = connection.CreateCommand())
        {
            // length() and hex() keep the XML itself out of the result set: a list page
            // must never carry up to a megabyte of evidence per row.
            var direction = query.Descending ? "DESC" : "ASC";
            command.CommandText = $"""
                SELECT
                    r.live_evidence_id,
                    r.live_session_id,
                    r.received_sequence,
                    r.event_record_id,
                    r.provider_name,
                    r.channel_name,
                    r.machine_name,
                    r.time_created_utc,
                    r.observed_utc,
                    hex(r.raw_xml_sha256),
                    length(r.raw_xml),
                    r.parser_raw_event_id,
                    r.parsed_event_id,
                    r.outcome,
                    r.error_code,
                    r.detail
                {RecordFilter}
                ORDER BY r.received_sequence {direction}
                LIMIT $limit OFFSET $offset;
                """;
            AddRecordParameters(command, query);
            command.Parameters.Add("$limit", SqliteType.Integer).Value = query.Page.Limit;
            command.Parameters.Add("$offset", SqliteType.Integer).Value = query.Page.Offset;

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new LiveCaptureRecordRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    ReadNullableTimestamp(reader, 7),
                    ParseTimestamp(reader.GetString(8)),
                    reader.GetString(9),
                    reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15)));
            }
        }

        return new PageResult<LiveCaptureRecordRow>(
            rows,
            totalCount,
            query.Page.Offset,
            query.Page.Limit);
    }

    public async Task<IReadOnlyList<LiveCaptureDiagnosticRow>> GetSessionDiagnosticsAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveSessionId);

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // Bounded by the same per-session cap the writer enforces, so one session can
        // never flood the page.
        command.CommandText = """
            SELECT
                live_diagnostic_id,
                live_session_id,
                stage,
                severity,
                code,
                message,
                occurred_utc
            FROM live_monitoring_diagnostics
            WHERE live_session_id = $session
            ORDER BY occurred_utc ASC, live_diagnostic_id ASC
            LIMIT $limit;
            """;
        command.Parameters.Add("$session", SqliteType.Text).Value = liveSessionId;
        command.Parameters.Add("$limit", SqliteType.Integer).Value =
            LiveMonitoringLimits.MaxDiagnostics;

        var rows = new List<LiveCaptureDiagnosticRow>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new LiveCaptureDiagnosticRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseSeverity(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                ParseTimestamp(reader.GetString(6))));
        }

        return rows;
    }

    public async Task<RawXmlDocument?> GetRecordRawXmlAsync(
        string liveEvidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveEvidenceId);

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // substr() truncates inside SQLite, so an oversized record is never fully
        // materialised in managed memory just to show a preview.
        command.CommandText = """
            SELECT length(raw_xml), substr(raw_xml, 1, $preview_limit)
            FROM live_capture_records
            WHERE live_evidence_id = $id
            LIMIT 1;
            """;
        command.Parameters.Add("$id", SqliteType.Text).Value = liveEvidenceId;
        command.Parameters.Add("$preview_limit", SqliteType.Integer).Value =
            RawXmlDocument.MaxPreviewCharacters;

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return reader.IsDBNull(0) || reader.IsDBNull(1)
            ? RawXmlDocument.CreateUnavailable(liveEvidenceId, "原始 XML 不可用。")
            : RawXmlDocument.CreatePreview(
                liveEvidenceId,
                reader.GetString(1),
                reader.GetInt64(0));
    }

    private static LiveHistoryAvailability Inaccessible(string message) =>
        new(
            LiveHistoryState.Inaccessible,
            $"无法以只读方式打开查看器数据库：{message}",
            []);

    private async Task<SqliteConnection> OpenReadyConnectionAsync(
        CancellationToken cancellationToken)
    {
        var availability = await GetAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!availability.IsReady)
        {
            throw new InvalidOperationException(availability.Message);
        }

        var connection = _location.CreateReadOnlyConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddSessionParameters(
        SqliteCommand command,
        LiveHistorySessionQuery query)
    {
        AddNullableText(command, "$from_utc", FormatNullable(query.FromUtcInclusive));
        AddNullableText(command, "$to_utc", FormatNullable(query.ToUtcExclusive));
        AddNullableText(command, "$state", ToStorageState(query.State));
    }

    private static void AddRecordParameters(
        SqliteCommand command,
        LiveHistoryRecordQuery query)
    {
        command.Parameters.Add("$session", SqliteType.Text).Value = query.LiveSessionId;
        AddNullableText(command, "$from_utc", FormatNullable(query.FromUtcInclusive));
        AddNullableText(command, "$to_utc", FormatNullable(query.ToUtcExclusive));
        AddNullableText(command, "$outcome", query.Outcome);
        AddNullableText(command, "$provider", BuildContainsPattern(query.ProviderContains));
        AddNullableText(command, "$channel", BuildContainsPattern(query.ChannelContains));
        AddNullableText(
            command,
            "$error_code",
            BuildContainsPattern(query.ErrorCodeContains));
        command.Parameters.Add("$errors_only", SqliteType.Integer).Value =
            query.ErrorsOnly ? 1 : 0;
        AddNullableInteger(command, "$parsed_event_id", query.ParsedEventId);
        AddNullableInteger(command, "$event_record_id", query.EventRecordId);
        AddNullableInteger(command, "$min_sequence", query.MinReceivedSequence);
        AddNullableInteger(command, "$max_sequence", query.MaxReceivedSequence);
    }

    private static string? ToStorageState(LiveHistorySessionState? state) => state switch
    {
        null => null,
        LiveHistorySessionState.Stopped => "stopped",
        LiveHistorySessionState.Error => "error",
        LiveHistorySessionState.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static ImportDiagnosticSeverity ParseSeverity(string value) => value switch
    {
        "info" => ImportDiagnosticSeverity.Information,
        "warning" => ImportDiagnosticSeverity.Warning,
        "error" => ImportDiagnosticSeverity.Error,
        _ => throw new InvalidOperationException($"Unsupported severity '{value}'.")
    };

    private static void AddNullableText(SqliteCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqliteType.Text).Value =
            value is null ? DBNull.Value : value;

    private static void AddNullableInteger(
        SqliteCommand command,
        string name,
        long? value) =>
        command.Parameters.Add(name, SqliteType.Integer).Value =
            value is null ? DBNull.Value : value.Value;

    private static async Task<long> ReadCountAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A completion column is null for a session that never wrote one. Zero is the
    /// honest reading there: the counts are simply not on record.
    /// </summary>
    private static long ReadCount(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

    private static string? BuildContainsPattern(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null ? null : $"%{EscapeLikePattern(normalized)}%";
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? FormatNullable(DateTimeOffset? value) =>
        value is null ? null : Format(value.Value);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
}
