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
    private const string SummaryMigration =
        "db/migrations/0003_phase_2a_live_monitoring.sql";

    private const string EvidenceMigration =
        "db/migrations/0004_phase_2b_live_evidence.sql";

    /// <summary>Phase 2A summary tables, from 0003.</summary>
    private static readonly TableRequirement[] SummaryTables =
    [
        new(
            "live_monitoring_sessions",
            SummaryMigration,
            IsWithoutRowId: false,
            [
                C("live_session_id", "TEXT", true, 1),
                C("started_utc", "TEXT", true),
                C("stopped_utc", "TEXT", false),
                C("final_state", "TEXT", true),
                C("received_count", "INTEGER", true),
                C("delete_fact_count", "INTEGER", true),
                C("process_context_count", "INTEGER", true),
                C("security_evidence_count", "INTEGER", true),
                C("ignored_count", "INTEGER", true),
                C("error_count", "INTEGER", true),
                C("dropped_count", "INTEGER", true),
                C("late_discarded_count", "INTEGER", true),
                C("suppressed_diagnostic_count", "INTEGER", true),
                C("queue_capacity", "INTEGER", true),
                C("application_version", "TEXT", true)
            ],
            [],
            []),
        new(
            "live_monitoring_channels",
            SummaryMigration,
            IsWithoutRowId: true,
            [
                C("live_session_id", "TEXT", true, 1),
                C("channel_name", "TEXT", true, 2),
                C("availability", "TEXT", true),
                C("detail", "TEXT", false)
            ],
            [
                new ForeignKeyRequirement(
                    "live_session_id",
                    "live_monitoring_sessions",
                    "live_session_id")
            ],
            []),
        new(
            "live_monitoring_diagnostics",
            SummaryMigration,
            IsWithoutRowId: false,
            [
                C("live_diagnostic_id", "TEXT", true, 1),
                C("live_session_id", "TEXT", true),
                C("stage", "TEXT", true),
                C("severity", "TEXT", true),
                C("code", "TEXT", true),
                C("message", "TEXT", true),
                C("occurred_utc", "TEXT", true)
            ],
            [
                new ForeignKeyRequirement(
                    "live_session_id",
                    "live_monitoring_sessions",
                    "live_session_id")
            ],
            [])
    ];

    /// <summary>Phase 2B.1 live evidence tables, from 0004.</summary>
    private static readonly TableRequirement[] EvidenceTables =
    [
        new(
            "live_capture_sessions",
            EvidenceMigration,
            IsWithoutRowId: false,
            [
                C("live_session_id", "TEXT", true, 1),
                C("started_utc", "TEXT", true),
                C("queue_capacity", "INTEGER", true),
                C("application_version", "TEXT", true)
            ],
            [],
            []),
        new(
            "live_capture_records",
            EvidenceMigration,
            IsWithoutRowId: false,
            [
                C("live_evidence_id", "TEXT", true, 1),
                C("live_session_id", "TEXT", true),
                C("received_sequence", "INTEGER", true),
                C("event_record_id", "INTEGER", false),
                C("provider_name", "TEXT", false),
                C("channel_name", "TEXT", true),
                C("machine_name", "TEXT", false),
                C("time_created_utc", "TEXT", false),
                C("observed_utc", "TEXT", true),
                C("raw_xml", "TEXT", true),
                C("raw_xml_sha256", "BLOB", true),
                C("parser_raw_event_id", "TEXT", false),
                C("parsed_event_id", "INTEGER", false),
                C("outcome", "TEXT", true),
                C("error_code", "TEXT", false),
                C("detail", "TEXT", false)
            ],
            [
                new ForeignKeyRequirement(
                    "live_session_id",
                    "live_capture_sessions",
                    "live_session_id")
            ],
            [
                new UniqueRequirement(
                    ["live_session_id", "received_sequence"])
            ]),
        new(
            "live_capture_completions",
            EvidenceMigration,
            IsWithoutRowId: false,
            [
                C("live_session_id", "TEXT", true, 1),
                C("stopped_utc", "TEXT", true),
                C("final_state", "TEXT", true),
                C("received_count", "INTEGER", true),
                C("delete_fact_count", "INTEGER", true),
                C("process_context_count", "INTEGER", true),
                C("security_evidence_count", "INTEGER", true),
                C("ignored_count", "INTEGER", true),
                C("error_count", "INTEGER", true),
                C("dropped_count", "INTEGER", true),
                C("late_discarded_count", "INTEGER", true),
                C("suppressed_diagnostic_count", "INTEGER", true),
                C("persisted_record_count", "INTEGER", true)
            ],
            [
                new ForeignKeyRequirement(
                    "live_session_id",
                    "live_capture_sessions",
                    "live_session_id")
            ],
            [])
    ];

    private static readonly TriggerRequirement[] EvidenceTriggers =
    [
        new("live_capture_sessions_no_update", "live_capture_sessions", "UPDATE"),
        new("live_capture_sessions_no_delete", "live_capture_sessions", "DELETE"),
        new("live_capture_records_no_update", "live_capture_records", "UPDATE"),
        new("live_capture_records_no_delete", "live_capture_records", "DELETE"),
        new("live_capture_completions_no_update", "live_capture_completions", "UPDATE"),
        new("live_capture_completions_no_delete", "live_capture_completions", "DELETE")
    ];

    private readonly ViewerDataLocation _location;

    public SqliteLiveMonitoringRepository(ViewerDataLocation location)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public async Task ValidateSchemaAsync(CancellationToken cancellationToken = default)
    {
        _ = RequireDatabase();

        await using var connection = _location.CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var table in SummaryTables)
        {
            await ValidateTableAsync(connection, table, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var table in EvidenceTables)
        {
            await ValidateTableAsync(connection, table, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var trigger in EvidenceTriggers)
        {
            await ValidateTriggerAsync(connection, trigger, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ValidateTableAsync(
        SqliteConnection connection,
        TableRequirement requirement,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT type
                FROM main.sqlite_master
                WHERE name = $name COLLATE NOCASE;
                """;
            command.Parameters.Add("$name", SqliteType.Text).Value = requirement.Name;
            var type = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) as string;
            if (!string.Equals(type, "table", StringComparison.OrdinalIgnoreCase))
            {
                throw SchemaNotReady(
                    requirement,
                    type is null
                        ? "the required table is missing"
                        : $"the object is type '{type}', not a table");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT type, wr, strict
                FROM pragma_table_list($table)
                WHERE schema = 'main';
                """;
            command.Parameters.Add("$table", SqliteType.Text).Value = requirement.Name;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw SchemaNotReady(
                    requirement,
                    "table metadata is unavailable");
            }

            if (!string.Equals(
                    reader.GetString(0),
                    "table",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw SchemaNotReady(
                    requirement,
                    "table metadata does not describe a normal table");
            }

            var withoutRowId = reader.GetInt64(1) != 0;
            if (withoutRowId != requirement.IsWithoutRowId)
            {
                throw SchemaNotReady(
                    requirement,
                    $"WITHOUT ROWID flag is {(withoutRowId ? "enabled" : "disabled")}");
            }

            if (reader.GetInt64(2) != 1)
            {
                throw SchemaNotReady(
                    requirement,
                    "STRICT table enforcement is missing");
            }
        }

        var columns = await ReadColumnsAsync(
                connection,
                requirement.Name,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var expected in requirement.Columns)
        {
            if (!columns.TryGetValue(expected.Name, out var actual))
            {
                throw SchemaNotReady(
                    requirement,
                    $"required column '{expected.Name}' is missing");
            }

            if (!string.Equals(
                    actual.DeclaredType,
                    expected.DeclaredType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw SchemaNotReady(
                    requirement,
                    $"column '{expected.Name}' has declared type/affinity "
                    + $"'{actual.DeclaredType}', expected '{expected.DeclaredType}'");
            }

            if (actual.IsNotNull != expected.IsNotNull)
            {
                throw SchemaNotReady(
                    requirement,
                    $"column '{expected.Name}' NOT NULL flag is "
                    + $"{(actual.IsNotNull ? "enabled" : "missing")}");
            }

            if (actual.PrimaryKeyOrdinal != expected.PrimaryKeyOrdinal)
            {
                throw SchemaNotReady(
                    requirement,
                    $"column '{expected.Name}' has primary-key ordinal "
                    + $"{actual.PrimaryKeyOrdinal}, expected "
                    + $"{expected.PrimaryKeyOrdinal}");
            }
        }

        if (columns.Count != requirement.Columns.Count)
        {
            throw SchemaNotReady(
                requirement,
                $"expected exactly {requirement.Columns.Count} columns but found "
                + $"{columns.Count}");
        }

        var foreignKeys = await ReadForeignKeysAsync(
                connection,
                requirement.Name,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var expected in requirement.ForeignKeys)
        {
            if (!foreignKeys.Contains(expected))
            {
                throw SchemaNotReady(
                    requirement,
                    $"required foreign key '{expected.FromColumn}' to "
                    + $"'{expected.ToTable}.{expected.ToColumn}' is missing or malformed");
            }
        }

        if (foreignKeys.Count != requirement.ForeignKeys.Count)
        {
            throw SchemaNotReady(
                requirement,
                $"expected exactly {requirement.ForeignKeys.Count} foreign key(s) "
                + $"but found {foreignKeys.Count}");
        }

        foreach (var unique in requirement.UniqueConstraints)
        {
            if (!await HasUniqueIndexAsync(
                    connection,
                    requirement.Name,
                    unique.Columns,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw SchemaNotReady(
                    requirement,
                    "required UNIQUE index on "
                    + $"({string.Join(", ", unique.Columns)}) is missing");
            }
        }
    }

    private static async Task<Dictionary<string, ColumnMetadata>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type, "notnull", pk
            FROM pragma_table_info($table, $schema)
            ORDER BY cid;
            """;
        command.Parameters.Add("$table", SqliteType.Text).Value = tableName;
        command.Parameters.Add("$schema", SqliteType.Text).Value = "main";
        var columns = new Dictionary<string, ColumnMetadata>(
            StringComparer.OrdinalIgnoreCase);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            columns.Add(
                name,
                new ColumnMetadata(
                    reader.GetString(1).Trim(),
                    reader.GetInt64(2) != 0,
                    reader.GetInt32(3)));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<ForeignKeyRequirement>> ReadForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "table", "from", "to", on_update, on_delete, match
            FROM pragma_foreign_key_list($table, $schema)
            ORDER BY id, seq;
            """;
        command.Parameters.Add("$table", SqliteType.Text).Value = tableName;
        command.Parameters.Add("$schema", SqliteType.Text).Value = "main";
        var foreignKeys = new List<ForeignKeyRequirement>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var onUpdate = reader.GetString(3);
            var onDelete = reader.GetString(4);
            var match = reader.GetString(5);
            if (!string.Equals(onUpdate, "NO ACTION", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(onDelete, "NO ACTION", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(match, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreignKeys.Add(new ForeignKeyRequirement(
                reader.GetString(1),
                reader.GetString(0),
                reader.GetString(2)));
        }

        return foreignKeys;
    }

    private static async Task<bool> HasUniqueIndexAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name
                FROM pragma_index_list($table, $schema)
                WHERE "unique" = 1
                  AND partial = 0
                  AND origin = 'u';
                """;
            command.Parameters.Add("$table", SqliteType.Text).Value = tableName;
            command.Parameters.Add("$schema", SqliteType.Text).Value = "main";
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(reader.GetString(0));
            }
        }

        foreach (var candidate in candidates)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM pragma_index_info($index, $schema)
                ORDER BY seqno;
                """;
            command.Parameters.Add("$index", SqliteType.Text).Value = candidate;
            command.Parameters.Add("$schema", SqliteType.Text).Value = "main";
            var actualColumns = new List<string>();
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    actualColumns.Add(reader.GetString(0));
                }
            }

            if (actualColumns.SequenceEqual(
                    expectedColumns,
                    StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ValidateTriggerAsync(
        SqliteConnection connection,
        TriggerRequirement requirement,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, tbl_name, COALESCE(sql, '')
            FROM main.sqlite_master
            WHERE name = $name COLLATE NOCASE;
            """;
        command.Parameters.Add("$name", SqliteType.Text).Value = requirement.Name;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw TriggerNotReady(requirement, "the required trigger is missing");
        }

        if (!string.Equals(
                reader.GetString(0),
                "trigger",
                StringComparison.OrdinalIgnoreCase))
        {
            throw TriggerNotReady(requirement, "the object is not a trigger");
        }

        if (!string.Equals(
                reader.GetString(1),
                requirement.TableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw TriggerNotReady(
                requirement,
                $"it is bound to '{reader.GetString(1)}', not "
                + $"'{requirement.TableName}'");
        }

        var tokens = TokenizeTriggerSql(reader.GetString(2));
        if (!ContainsTokenSequence(
                tokens,
                ["BEFORE", requirement.EventName, "ON", requirement.TableName])
            || tokens.Contains("WHEN", StringComparer.OrdinalIgnoreCase)
            || !ContainsTokenSequence(tokens, ["SELECT", "RAISE", "ABORT"]))
        {
            throw TriggerNotReady(
                requirement,
                $"definition must be unconditional {requirement.EventName} "
                + "and fail closed with SELECT RAISE(ABORT, ...)");
        }
    }

    private static string[] TokenizeTriggerSql(string sql)
    {
        var normalized = new StringBuilder(sql.Length);
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (character == '\'')
            {
                normalized.Append(' ');
                while (++index < sql.Length)
                {
                    if (sql[index] != '\'')
                    {
                        continue;
                    }

                    if (index + 1 < sql.Length && sql[index + 1] == '\'')
                    {
                        index++;
                        continue;
                    }

                    break;
                }

                continue;
            }

            if (character == '-'
                && index + 1 < sql.Length
                && sql[index + 1] == '-')
            {
                normalized.Append(' ');
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (character == '/'
                && index + 1 < sql.Length
                && sql[index + 1] == '*')
            {
                normalized.Append(' ');
                index += 2;
                while (index + 1 < sql.Length
                       && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }

                if (index + 1 < sql.Length)
                {
                    index++;
                }

                continue;
            }

            normalized.Append(
                char.IsAsciiLetterOrDigit(character) || character == '_'
                    ? character
                    : ' ');
        }

        return normalized
            .ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool ContainsTokenSequence(
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> expected)
    {
        for (var start = 0; start <= tokens.Count - expected.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < expected.Count; offset++)
            {
                if (!string.Equals(
                        tokens[start + offset],
                        expected[offset],
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static InvalidOperationException SchemaNotReady(
        TableRequirement requirement,
        string detail) =>
        new(
            $"Runtime structural readiness check failed for table "
            + $"'{requirement.Name}': {detail}. Apply {requirement.Migration} "
            + "explicitly. This runtime structural readiness check does not prove "
            + "database integrity, tamper resistance, or migration authenticity.");

    private static InvalidOperationException TriggerNotReady(
        TriggerRequirement requirement,
        string detail) =>
        new(
            $"Runtime structural readiness check failed for trigger "
            + $"'{requirement.Name}' on table '{requirement.TableName}': {detail}. "
            + $"Apply {EvidenceMigration} explicitly. This runtime structural "
            + "readiness check does not prove database integrity, tamper resistance, "
            + "or migration authenticity.");

    private static ColumnRequirement C(
        string name,
        string declaredType,
        bool isNotNull,
        int primaryKeyOrdinal = 0) =>
        new(name, declaredType, isNotNull, primaryKeyOrdinal);

    private sealed record TableRequirement(
        string Name,
        string Migration,
        bool IsWithoutRowId,
        IReadOnlyList<ColumnRequirement> Columns,
        IReadOnlyList<ForeignKeyRequirement> ForeignKeys,
        IReadOnlyList<UniqueRequirement> UniqueConstraints);

    private sealed record ColumnRequirement(
        string Name,
        string DeclaredType,
        bool IsNotNull,
        int PrimaryKeyOrdinal);

    private sealed record ColumnMetadata(
        string DeclaredType,
        bool IsNotNull,
        int PrimaryKeyOrdinal);

    private sealed record ForeignKeyRequirement(
        string FromColumn,
        string ToTable,
        string ToColumn);

    private sealed record UniqueRequirement(IReadOnlyList<string> Columns);

    private sealed record TriggerRequirement(
        string Name,
        string TableName,
        string EventName);

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
