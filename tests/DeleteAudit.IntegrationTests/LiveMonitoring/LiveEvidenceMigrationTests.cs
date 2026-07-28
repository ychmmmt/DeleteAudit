using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.LiveMonitoring;

/// <summary>
/// Database-level guarantees of db/migrations/0004_phase_2b_live_evidence.sql. Every test
/// builds a throwaway database under the repository's artifacts directory from the
/// shipped scripts; nothing here reads a real Windows event log or touches a path outside
/// the checkout. Server and machine names in the fixtures are invented.
/// </summary>
public sealed class LiveEvidenceMigrationTests
{
    private const string Session = "11111111-1111-1111-1111-111111111111";
    private const string StartedUtc = "2026-07-25T09:00:00.0000000+00:00";

    private static readonly string[] NewTables =
    [
        "live_capture_sessions",
        "live_capture_records",
        "live_capture_completions"
    ];

    [Fact]
    public async Task MigrationAppliesOnTopOfSchemaAndEarlierIncrements()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);

        var tables = await QueryNamesAsync(
            path,
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'live_capture_%' ORDER BY name;");

        Assert.Equal(
            ["live_capture_completions", "live_capture_records", "live_capture_sessions"],
            tables);
    }

    [Fact]
    public async Task MigrationContainsNoDestructiveStatement()
    {
        var migration = await File.ReadAllTextAsync(MigrationPath);

        foreach (var forbidden in new[]
                 {
                     "DROP ", "DELETE FROM", "TRUNCATE", "VACUUM",
                     "ATTACH", "DETACH", "ALTER TABLE"
                 })
        {
            Assert.DoesNotContain(forbidden, migration, StringComparison.OrdinalIgnoreCase);
        }

        // It also writes no business data of its own.
        Assert.DoesNotContain("INSERT INTO", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrationLeavesEveryPreexistingSchemaObjectUntouched()
    {
        var before = await CreateDatabaseAsync(applyEvidence: false);
        var beforeObjects = await ReadSchemaAsync(before);

        var after = await CreateDatabaseAsync(applyEvidence: true);
        var afterObjects = await ReadSchemaAsync(after);

        // Everything that existed before 0004 must be byte-identical afterwards.
        foreach (var (name, sql) in beforeObjects)
        {
            Assert.True(afterObjects.ContainsKey(name), name);
            Assert.Equal(sql, afterObjects[name]);
        }

        // And 0004 adds only its own objects: the three tables, their indexes and their
        // append-only triggers all name the tables they belong to.
        var added = afterObjects.Keys.Except(beforeObjects.Keys).ToArray();
        Assert.NotEmpty(added);
        Assert.All(added, name => Assert.Contains(
            "live_capture_",
            name,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryNewTableIsStrict()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        var schema = await ReadSchemaAsync(path);

        foreach (var table in NewTables)
        {
            Assert.Contains("STRICT", schema[table], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("live_capture_sessions")]
    [InlineData("live_capture_records")]
    [InlineData("live_capture_completions")]
    public async Task NewTablesRejectUpdate(string table)
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, $"UPDATE {table} SET live_session_id = 'moved';"));

        Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("live_capture_sessions")]
    [InlineData("live_capture_records")]
    [InlineData("live_capture_completions")]
    public async Task NewTablesRejectDelete(string table)
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, $"DELETE FROM {table};"));

        Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateReceivedSequenceIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, RecordInsert(evidenceId: $"{Session}:1-again", sequence: 1)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task UnknownOutcomeIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, RecordInsert(sequence: 2, outcome: "delete_session")));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task WrongDigestLengthIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, RecordInsert(sequence: 3, digestBytes: 31)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task NonPositiveReceivedSequenceIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, RecordInsert(evidenceId: $"{Session}:0", sequence: 0)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task OverlongRawXmlIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        // 1 UTF-16 code unit above the documented per-event ceiling.
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                path,
                RecordInsert(sequence: 4, rawXml: new string('x', 1_048_577))));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task UnbalancedCompletionCountsAreRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path, includeCompletion: false);

        // received (10) does not equal the classified plus dropped total (2).
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, CompletionInsert(received: 10, ignored: 2)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task PersistedCountAboveTheClassifiedTotalIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path, includeCompletion: false);

        // Two records were classified, but three claim to have been stored.
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                path,
                CompletionInsert(received: 2, ignored: 2, persisted: 3)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task ASecondCompletionForTheSameSessionIsRejected()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, CompletionInsert()));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task AStartWithoutCompletionIsLegalAndQueryable()
    {
        // This is exactly the abnormal-termination shape: the capture was running and
        // never finished. It must stay readable and must not look like a normal stop.
        var path = await CreateDatabaseAsync(applyEvidence: true);
        await SeedAsync(path, includeCompletion: false);

        var unfinished = await QueryNamesAsync(
            path,
            """
            SELECT s.live_session_id
            FROM live_capture_sessions s
            LEFT JOIN live_capture_completions c
                   ON c.live_session_id = s.live_session_id
            WHERE c.live_session_id IS NULL;
            """);

        Assert.Equal([Session], unfinished);
        Assert.Equal(1, await ScalarAsync(path, "SELECT COUNT(*) FROM live_capture_records;"));
        Assert.Equal(0, await ScalarAsync(path, "SELECT COUNT(*) FROM live_capture_completions;"));
    }

    [Fact]
    public async Task CompletionWithoutAStartIsRejectedByTheForeignKey()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, CompletionInsert(received: 0, ignored: 0, persisted: 0)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task RecordWithoutAStartIsRejectedByTheForeignKey()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(path, RecordInsert(sequence: 1)));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task EarlierPhaseTablesAndViewSurviveTheIncrement()
    {
        var path = await CreateDatabaseAsync(applyEvidence: true);
        var schema = await ReadSchemaAsync(path);

        foreach (var name in new[]
                 {
                     "raw_events", "process_observations", "delete_events",
                     "event_evidence", "delete_sessions", "risk_assessments",
                     "channel_epochs", "v_delete_audit",
                     "live_monitoring_sessions", "live_monitoring_channels",
                     "live_monitoring_diagnostics"
                 })
        {
            Assert.True(schema.ContainsKey(name), name);
        }

        Assert.Equal(
            1,
            await ScalarAsync(
                path,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='raw_events_no_update';"));
    }

    private static string MigrationPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "0004_phase_2b_live_evidence.sql");

    private static string RecordInsert(
        string? evidenceId = null,
        long sequence = 1,
        string outcome = "ignored",
        int digestBytes = 32,
        string rawXml = "<Event />") =>
        $"""
         INSERT INTO live_capture_records (
             live_evidence_id, live_session_id, received_sequence, event_record_id,
             provider_name, channel_name, machine_name, time_created_utc, observed_utc,
             raw_xml, raw_xml_sha256, parser_raw_event_id, parsed_event_id, outcome,
             error_code, detail)
         VALUES (
             '{evidenceId ?? $"{Session}:{sequence}"}', '{Session}', {sequence}, 41,
             'Microsoft-Windows-Sysmon', 'Microsoft-Windows-Sysmon/Operational',
             'LAB-PC', '{StartedUtc}', '{StartedUtc}',
             '{rawXml}', zeroblob({digestBytes}), 'raw-1', 26, '{outcome}',
             NULL, NULL);
         """;

    private static string CompletionInsert(
        long received = 1,
        long ignored = 1,
        long persisted = 1) =>
        $"""
         INSERT INTO live_capture_completions (
             live_session_id, stopped_utc, final_state, received_count,
             delete_fact_count, process_context_count, security_evidence_count,
             ignored_count, error_count, dropped_count, late_discarded_count,
             suppressed_diagnostic_count, persisted_record_count)
         VALUES ('{Session}', '{StartedUtc}', 'stopped', {received},
                 0, 0, 0, {ignored}, 0, 0, 0, 0, {persisted});
         """;

    private static async Task SeedAsync(string path, bool includeCompletion = true)
    {
        await ExecuteAsync(
            path,
            $"""
             INSERT INTO live_capture_sessions (
                 live_session_id, started_utc, queue_capacity, application_version)
             VALUES ('{Session}', '{StartedUtc}', 2048, '0.1.0-alpha');
             """);
        await ExecuteAsync(path, RecordInsert(sequence: 1));
        if (includeCompletion)
        {
            await ExecuteAsync(path, CompletionInsert());
        }
    }

    private static async Task<string> CreateDatabaseAsync(bool applyEvidence)
    {
        var directory = Path.Combine(
            ViewerDataLocation.DefaultRoot,
            "tests",
            $"live-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "viewer.db");

        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var scripts = new List<string>
        {
            await File.ReadAllTextAsync(Path.Combine(fixtures, "schema.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0002_phase_1b_offline_import.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0003_phase_2a_live_monitoring.sql"))
        };
        if (applyEvidence)
        {
            scripts.Add(await File.ReadAllTextAsync(MigrationPath));
        }

        await using var connection = Open(path, create: true);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, scripts);
        await command.ExecuteNonQueryAsync();
        return path;
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = Open(path, create: false);
        await connection.OpenAsync();
        using (var pragma = connection.CreateCommand())
        {
            // Foreign keys are per-connection in SQLite; the tests state it explicitly.
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = Open(path, create: false);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string[]> QueryNamesAsync(string path, string sql)
    {
        await using var connection = Open(path, create: false);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task<Dictionary<string, string>> ReadSchemaAsync(string path)
    {
        await using var connection = Open(path, create: false);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, COALESCE(sql, '')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%';
            """;
        var schema = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schema[reader.GetString(0)] = reader.GetString(1);
        }

        return schema;
    }

    private static SqliteConnection Open(string path, bool create) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
}
