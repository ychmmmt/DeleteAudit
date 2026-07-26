using System.Globalization;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Viewing;

public sealed class SqliteViewerQueryService : IViewerQueryService
{
    private const string SessionFilter = """
        FROM delete_sessions
        WHERE ($risk IS NULL OR current_risk = $risk)
          AND ($from_utc IS NULL OR last_event_utc >= $from_utc)
          AND ($to_utc IS NULL OR last_event_utc < $to_utc)
          AND ($path IS NULL
               OR path_scope COLLATE NOCASE LIKE $path ESCAPE '\')
          AND ($process IS NULL
               OR process_identity COLLATE NOCASE LIKE $process ESCAPE '\'
               OR process_guid COLLATE NOCASE LIKE $process ESCAPE '\'
               OR CAST(process_id AS TEXT) = $process_exact)
        """;

    private const string EventFilter = """
        FROM v_delete_audit
        WHERE ($risk IS NULL OR risk_level = $risk)
          AND ($from_utc IS NULL OR occurred_utc >= $from_utc)
          AND ($to_utc IS NULL OR occurred_utc < $to_utc)
          AND ($path IS NULL
               OR full_path COLLATE NOCASE LIKE $path ESCAPE '\')
          AND ($process IS NULL
               OR process_path COLLATE NOCASE LIKE $process ESCAPE '\'
               OR process_guid COLLATE NOCASE LIKE $process ESCAPE '\'
               OR CAST(process_id AS TEXT) = $process_exact)
        """;

    private const string ImportFilter = """
        FROM import_sessions
        WHERE ($status IS NULL OR status = $status)
          AND ($from_utc IS NULL OR started_utc >= $from_utc)
          AND ($to_utc IS NULL OR started_utc < $to_utc)
          AND ($source_path IS NULL
               OR normalized_source_path COLLATE NOCASE
                  LIKE $source_path ESCAPE '\')
        """;

    private const string DiagnosticFilter = """
        FROM import_diagnostics AS d
        INNER JOIN import_sessions AS i
            ON i.import_session_id = d.import_session_id
        WHERE ($severity IS NULL OR d.severity = $severity)
          AND ($from_utc IS NULL OR d.occurred_utc >= $from_utc)
          AND ($to_utc IS NULL OR d.occurred_utc < $to_utc)
          AND ($text IS NULL
               OR d.code COLLATE NOCASE LIKE $text ESCAPE '\'
               OR d.message COLLATE NOCASE LIKE $text ESCAPE '\')
        """;

    private static readonly (string Type, string Name)[] RequiredObjects =
    [
        ("table", "schema_migrations"),
        ("table", "channel_epochs"),
        ("table", "raw_events"),
        ("table", "process_observations"),
        ("table", "delete_sessions"),
        ("table", "delete_events"),
        ("table", "event_evidence"),
        ("table", "session_members"),
        ("table", "risk_assessments"),
        ("table", "alerts"),
        ("table", "protected_roots"),
        ("table", "usn_checkpoints"),
        ("table", "integrity_checkpoints"),
        ("table", "import_sessions"),
        ("table", "import_records"),
        ("table", "import_diagnostics"),
        ("table", "event_correlations"),
        ("table", "risk_assessment_subject_links"),
        ("view", "v_delete_audit")
    ];

    private readonly ViewerDataLocation _location;

    public SqliteViewerQueryService(ViewerDataLocation location)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public async Task<ViewerDatabaseStatus> GetDatabaseStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = _location.EnsureDatabasePath();
        if (!File.Exists(databasePath))
        {
            return new ViewerDatabaseStatus(
                ViewerDatabaseState.MissingDatabase,
                "The viewer database does not exist. Schema creation is an explicit external step.",
                []);
        }

        try
        {
            await using var connection = _location.CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var missingObjects = await FindMissingObjectsAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return missingObjects.Count == 0
                ? new ViewerDatabaseStatus(
                    ViewerDatabaseState.Ready,
                    "The viewer database is ready.",
                    [])
                : new ViewerDatabaseStatus(
                    ViewerDatabaseState.MissingSchema,
                    $"The viewer database is missing required schema objects: {string.Join(", ", missingObjects)}.",
                    missingObjects);
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

    public async Task<DashboardSummary> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM import_sessions),
                (SELECT COUNT(*) FROM delete_sessions),
                (SELECT COUNT(*) FROM delete_events),
                (SELECT COUNT(*) FROM delete_sessions WHERE current_risk = 'warning'),
                (SELECT COUNT(*) FROM delete_sessions WHERE current_risk = 'critical'),
                (SELECT COUNT(*) FROM import_diagnostics WHERE severity = 'warning'),
                (SELECT COUNT(*) FROM import_diagnostics WHERE severity = 'error'),
                (SELECT MAX(started_utc) FROM import_sessions);
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The dashboard query returned no row.");
        }

        return new DashboardSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            ReadNullableTimestamp(reader, 7));
    }

    public async Task<PageResult<ImportHistoryRow>> GetImportsAsync(
        ImportHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var status = NormalizeImportStatus(query.Status);

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) {ImportFilter};";
        AddImportParameters(countCommand, query, status);
        var totalCount = await ReadCountAsync(countCommand, cancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                import_session_id,
                source_kind,
                original_file_name,
                normalized_source_path,
                file_size_bytes,
                started_utc,
                completed_utc,
                total_record_count,
                success_record_count,
                ignored_record_count,
                error_record_count,
                application_version,
                schema_version,
                status,
                output_status,
                output_error_code,
                output_error_message
            {ImportFilter}
            ORDER BY started_utc DESC, import_session_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddImportParameters(command, query, status);
        AddPageParameters(command, query.Page);

        var rows = new List<ImportHistoryRow>(query.Page.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ImportHistoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                ParseTimestamp(reader.GetString(5)),
                ReadNullableTimestamp(reader, 6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetString(13),
                ReadNullableString(reader, 14),
                ReadNullableString(reader, 15),
                ReadNullableString(reader, 16)));
        }

        return new PageResult<ImportHistoryRow>(
            rows,
            totalCount,
            query.Page.Offset,
            query.Page.Limit);
    }

    public async Task<PageResult<DeleteSessionRow>> GetSessionsAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) {SessionFilter};";
        AddAuditParameters(countCommand, query);
        var totalCount = await ReadCountAsync(countCommand, cancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
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
                current_risk
            {SessionFilter}
            ORDER BY last_event_utc DESC, delete_session_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddAuditParameters(command, query);
        AddPageParameters(command, query.Page);

        var rows = new List<DeleteSessionRow>(query.Page.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DeleteSessionRow(
                reader.GetString(0),
                ParseTimestamp(reader.GetString(1)),
                ParseTimestamp(reader.GetString(2)),
                ReadNullableTimestamp(reader, 3),
                ReadNullableString(reader, 4),
                ReadNullableInt32(reader, 5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                ParseRisk(reader.GetString(11))));
        }

        return new PageResult<DeleteSessionRow>(
            rows,
            totalCount,
            query.Page.Offset,
            query.Page.Limit);
    }

    public async Task<PageResult<DeleteEventRow>> GetEventsAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) {EventFilter};";
        AddAuditParameters(countCommand, query);
        var totalCount = await ReadCountAsync(countCommand, cancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                delete_event_id,
                occurred_utc,
                occurred_local,
                source_event_id,
                full_path,
                object_kind,
                process_id,
                process_path,
                process_guid,
                user_name,
                user_sid,
                delete_session_id,
                risk_level,
                attribution_confidence,
                missing_fields_json
            {EventFilter}
            ORDER BY occurred_utc DESC, delete_event_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddAuditParameters(command, query);
        AddPageParameters(command, query.Page);

        var rows = new List<DeleteEventRow>(query.Page.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DeleteEventRow(
                reader.GetString(0),
                ParseTimestamp(reader.GetString(1)),
                ReadNullableString(reader, 2),
                reader.GetInt32(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableInt32(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                ReadNullableString(reader, 10),
                reader.GetString(11),
                ParseRisk(reader.GetString(12)),
                reader.GetInt32(13),
                ReadNullableString(reader, 14)));
        }

        return new PageResult<DeleteEventRow>(
            rows,
            totalCount,
            query.Page.Offset,
            query.Page.Limit);
    }

    public async Task<PageResult<DiagnosticRow>> GetDiagnosticsAsync(
        DiagnosticQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) {DiagnosticFilter};";
        AddDiagnosticParameters(countCommand, query);
        var totalCount = await ReadCountAsync(countCommand, cancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                d.import_diagnostic_id,
                d.import_session_id,
                d.record_ordinal,
                i.original_file_name,
                d.stage,
                d.severity,
                d.code,
                d.message,
                d.details_json,
                d.occurred_utc
            {DiagnosticFilter}
            ORDER BY d.occurred_utc DESC, d.import_diagnostic_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddDiagnosticParameters(command, query);
        AddPageParameters(command, query.Page);

        var rows = new List<DiagnosticRow>(query.Page.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DiagnosticRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseSeverity(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                ReadNullableString(reader, 8),
                ParseTimestamp(reader.GetString(9))));
        }

        return new PageResult<DiagnosticRow>(
            rows,
            totalCount,
            query.Page.Offset,
            query.Page.Limit);
    }

    public async Task<RawXmlDocument?> GetDeleteEventRawXmlAsync(
        string deleteEventId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deleteEventId))
        {
            throw new ArgumentException(
                "A delete event ID is required.",
                nameof(deleteEventId));
        }

        await using var connection = await OpenReadyConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                length(r.raw_xml),
                substr(r.raw_xml, 1, $preview_limit)
            FROM delete_events AS d
            INNER JOIN raw_events AS r
                ON r.raw_event_id = d.primary_raw_event_id
            WHERE d.delete_event_id = $delete_event_id
            LIMIT 1;
            """;
        command.Parameters.Add("$delete_event_id", SqliteType.Text).Value = deleteEventId;
        command.Parameters.Add("$preview_limit", SqliteType.Integer).Value =
            RawXmlDocument.MaxPreviewCharacters;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return RawXmlDocument.CreateUnavailable(
                deleteEventId,
                "The raw XML is unavailable.");
        }

        return RawXmlDocument.CreatePreview(
            deleteEventId,
            reader.GetString(1),
            reader.GetInt64(0));
    }

    private static ViewerDatabaseStatus Inaccessible(string message) =>
        new(
            ViewerDatabaseState.Inaccessible,
            $"The viewer database cannot be opened read-only: {message}",
            []);

    private async Task<SqliteConnection> OpenReadyConnectionAsync(
        CancellationToken cancellationToken)
    {
        var status = await GetDatabaseStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsReady)
        {
            throw new InvalidOperationException(status.Message);
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

    private static async Task<IReadOnlyList<string>> FindMissingObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name
            FROM sqlite_master
            WHERE type IN ('table', 'view');
            """;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            present.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }

        return RequiredObjects
            .Where(item => !present.Contains($"{item.Type}:{item.Name}"))
            .Select(item => item.Name)
            .ToArray();
    }

    private static async Task<long> ReadCountAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddAuditParameters(SqliteCommand command, AuditQuery query)
    {
        AddNullableText(command, "$risk", query.Risk is null ? null : ToStorageRisk(query.Risk.Value));
        AddNullableText(command, "$from_utc", FormatNullable(query.FromUtcInclusive));
        AddNullableText(command, "$to_utc", FormatNullable(query.ToUtcExclusive));
        AddNullableText(command, "$path", BuildContainsPattern(query.PathContains));
        AddNullableText(command, "$process", BuildContainsPattern(query.ProcessContains));
        AddNullableText(command, "$process_exact", NormalizeSearchTerm(query.ProcessContains));
    }

    private static void AddImportParameters(
        SqliteCommand command,
        ImportHistoryQuery query,
        string? status)
    {
        AddNullableText(command, "$status", status);
        AddNullableText(command, "$from_utc", FormatNullable(query.FromUtcInclusive));
        AddNullableText(command, "$to_utc", FormatNullable(query.ToUtcExclusive));
        AddNullableText(
            command,
            "$source_path",
            BuildContainsPattern(query.SourcePathContains));
    }

    private static void AddDiagnosticParameters(
        SqliteCommand command,
        DiagnosticQuery query)
    {
        AddNullableText(
            command,
            "$severity",
            query.Severity is null ? null : ToStorageSeverity(query.Severity.Value));
        AddNullableText(command, "$from_utc", FormatNullable(query.FromUtcInclusive));
        AddNullableText(command, "$to_utc", FormatNullable(query.ToUtcExclusive));
        AddNullableText(command, "$text", BuildContainsPattern(query.TextContains));
    }

    private static void AddPageParameters(SqliteCommand command, PageRequest page)
    {
        command.Parameters.Add("$limit", SqliteType.Integer).Value = page.Limit;
        command.Parameters.Add("$offset", SqliteType.Integer).Value = page.Offset;
    }

    private static void AddNullableText(
        SqliteCommand command,
        string name,
        string? value)
    {
        command.Parameters.Add(name, SqliteType.Text).Value =
            value is null ? DBNull.Value : value;
    }

    private static string? NormalizeImportStatus(string? status)
    {
        var normalized = NormalizeSearchTerm(status)?.ToLowerInvariant();
        return normalized switch
        {
            null => null,
            "in_progress" => normalized,
            "completed" => normalized,
            "partial_failure" => normalized,
            "failed" => normalized,
            _ => throw new ArgumentException(
                "Status must be in_progress, completed, partial_failure, failed, or empty.",
                nameof(status))
        };
    }

    private static string? BuildContainsPattern(string? value)
    {
        var normalized = NormalizeSearchTerm(value);
        if (normalized is null)
        {
            return null;
        }

        return $"%{EscapeLikePattern(normalized)}%";
    }

    private static string? NormalizeSearchTerm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string ToStorageRisk(AuditRiskLevel risk) => risk switch
    {
        AuditRiskLevel.Informational => "informational",
        AuditRiskLevel.Warning => "warning",
        AuditRiskLevel.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };

    private static AuditRiskLevel ParseRisk(string risk) => risk switch
    {
        "informational" => AuditRiskLevel.Informational,
        "warning" => AuditRiskLevel.Warning,
        "critical" => AuditRiskLevel.Critical,
        _ => throw new InvalidOperationException($"Unsupported risk level '{risk}'.")
    };

    private static string ToStorageSeverity(ImportDiagnosticSeverity severity) =>
        severity switch
        {
            ImportDiagnosticSeverity.Information => "info",
            ImportDiagnosticSeverity.Warning => "warning",
            ImportDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
        };

    private static ImportDiagnosticSeverity ParseSeverity(string severity) =>
        severity switch
        {
            "info" => ImportDiagnosticSeverity.Information,
            "warning" => ImportDiagnosticSeverity.Warning,
            "error" => ImportDiagnosticSeverity.Error,
            _ => throw new InvalidOperationException(
                $"Unsupported diagnostic severity '{severity}'.")
        };
}
