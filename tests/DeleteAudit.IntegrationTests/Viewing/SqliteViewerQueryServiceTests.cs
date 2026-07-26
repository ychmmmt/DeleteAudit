using System.Globalization;
using System.Security.Cryptography;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.Viewing;

public sealed class SqliteViewerQueryServiceTests
{
    [Fact]
    public void LocationRejectsPathsOutsideViewerDataRoot()
    {
        var outside = Path.Combine(
            RepositoryRoot.ArtifactsDirectory,
            "viewer-data-sibling",
            "outside.db");

        Assert.Throws<ArgumentException>(
            () => ViewerDataLocation.CreateForTesting(
                outside,
                ViewerDataLocation.DefaultJsonlDirectory));
        Assert.Equal(
            ViewerDataLocation.DefaultDatabasePath,
            ViewerDataLocation.Default.DatabasePath);
        Assert.Equal(
            ViewerDataLocation.DefaultJsonlDirectory,
            ViewerDataLocation.Default.JsonlOutputDirectory);
    }

    [Fact]
    public async Task MissingDatabaseIsReportedWithoutCreatingFile()
    {
        var location = CreateLocation();
        var service = new SqliteViewerQueryService(location);

        var status = await service.GetDatabaseStatusAsync();

        Assert.Equal(ViewerDatabaseState.MissingDatabase, status.State);
        Assert.False(File.Exists(location.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetDashboardAsync());
        Assert.False(File.Exists(location.DatabasePath));
    }

    [Fact]
    public async Task MissingSchemaIsReportedWithoutCreatingTables()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: false);
        var service = new SqliteViewerQueryService(location);

        var status = await service.GetDatabaseStatusAsync();

        Assert.Equal(ViewerDatabaseState.MissingSchema, status.State);
        Assert.Contains("import_sessions", status.MissingObjects);
        Assert.Contains("v_delete_audit", status.MissingObjects);
        Assert.Equal(0, await CountUserObjectsAsync(location));
    }

    [Fact]
    public async Task CompleteEmptySchemaReturnsZeroAndEmptyPages()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        var service = new SqliteViewerQueryService(location);

        var status = await service.GetDatabaseStatusAsync();
        var dashboard = await service.GetDashboardAsync();
        var imports = await service.GetImportsAsync(AllImports());
        var sessions = await service.GetSessionsAsync(AllAudit());
        var events = await service.GetEventsAsync(AllAudit());
        var diagnostics = await service.GetDiagnosticsAsync(AllDiagnostics());

        Assert.True(status.IsReady);
        Assert.Equal(new DashboardSummary(0, 0, 0, 0, 0, 0, 0, null), dashboard);
        Assert.Empty(imports.Items);
        Assert.Empty(sessions.Items);
        Assert.Empty(events.Items);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(0, imports.TotalCount);
        Assert.Equal(0, sessions.TotalCount);
        Assert.Equal(0, events.TotalCount);
        Assert.Equal(0, diagnostics.TotalCount);
    }

    [Fact]
    public async Task QueriesApplyFiltersAndLoadRawXmlOnlyOnDemand()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        await ExecuteWriteAsync(location, SeedSql);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));
        var service = new SqliteViewerQueryService(location);

        var critical = await service.GetEventsAsync(
            AllAudit(risk: AuditRiskLevel.Critical));
        var byTime = await service.GetEventsAsync(
            AllAudit(
                fromUtc: Utc(10, 30),
                toUtc: Utc(11, 30)));
        var literalPath = await service.GetEventsAsync(AllAudit(path: "100%_done"));
        var processPath = await service.GetEventsAsync(AllAudit(process: "eraser.exe"));
        var processId = await service.GetEventsAsync(AllAudit(process: "202"));
        var warningSessions = await service.GetSessionsAsync(
            AllAudit(
                risk: AuditRiskLevel.Warning,
                path: "100%_done",
                process: "202"));
        var rawXml = await service.GetDeleteEventRawXmlAsync("event-critical");
        var after = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));

        Assert.Equal("event-critical", Assert.Single(critical.Items).DeleteEventId);
        Assert.Equal("event-warning", Assert.Single(byTime.Items).DeleteEventId);
        Assert.Equal("event-warning", Assert.Single(literalPath.Items).DeleteEventId);
        Assert.Equal("event-critical", Assert.Single(processPath.Items).DeleteEventId);
        Assert.Equal("event-warning", Assert.Single(processId.Items).DeleteEventId);
        Assert.Equal("session-warning", Assert.Single(warningSessions.Items).DeleteSessionId);
        Assert.NotNull(rawXml);
        Assert.True(rawXml.IsReadOnly);
        Assert.True(rawXml.IsAvailable);
        Assert.Equal("""<Event id="critical">&amp;</Event>""", rawXml.PreviewText);
        Assert.False(rawXml.IsTruncated);
        Assert.Equal(rawXml.PreviewText!.Length, rawXml.OriginalLength);
        Assert.Equal(rawXml.PreviewText.Length, rawXml.PreviewLength);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, rawXml.PreviewLimit);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RawXmlAtExactPreviewLimitIsCompleteAndNotTruncated()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        var content = CreateDeterministicXml(RawXmlDocument.MaxPreviewCharacters);
        await SeedSingleRawXmlEventAsync(location, content);
        var service = new SqliteViewerQueryService(location);

        var document = await service.GetDeleteEventRawXmlAsync("preview-event");

        Assert.NotNull(document);
        Assert.True(document.IsAvailable);
        Assert.False(document.IsTruncated);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, document.OriginalLength);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, document.PreviewLength);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, document.PreviewLimit);
        Assert.Equal(content, document.PreviewText);
    }

    [Fact]
    public async Task OversizedRawXmlIsTruncatedAtTheDatabaseLayer()
    {
        const int originalLength = RawXmlDocument.MaxPreviewCharacters * 4 + 17;
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        var content = CreateDeterministicXml(originalLength);
        await SeedSingleRawXmlEventAsync(location, content);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));
        var service = new SqliteViewerQueryService(location);

        var document = await service.GetDeleteEventRawXmlAsync("preview-event");
        var after = SHA256.HashData(await File.ReadAllBytesAsync(location.DatabasePath));

        Assert.NotNull(document);
        Assert.True(document.IsAvailable);
        Assert.True(document.IsTruncated);
        Assert.Equal(originalLength, document.OriginalLength);
        Assert.NotNull(document.PreviewText);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, document.PreviewText.Length);
        Assert.Equal(RawXmlDocument.MaxPreviewCharacters, document.PreviewLength);
        Assert.Equal(
            content[..RawXmlDocument.MaxPreviewCharacters],
            document.PreviewText);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task MissingRawXmlEventReturnsNull()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        await ExecuteWriteAsync(location, SeedSql);
        var service = new SqliteViewerQueryService(location);

        var document = await service.GetDeleteEventRawXmlAsync("no-such-event");

        Assert.Null(document);
    }

    [Fact]
    public async Task ImportsAndDiagnosticsFilterPersistedStates()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        await ExecuteWriteAsync(location, SeedSql);
        var service = new SqliteViewerQueryService(location);

        var failed = await service.GetImportsAsync(
            AllImports(status: "failed", sourcePath: "FAILED.XML"));
        var warnings = await service.GetDiagnosticsAsync(
            AllDiagnostics(
                severity: ImportDiagnosticSeverity.Warning,
                text: "partial"));
        var dashboard = await service.GetDashboardAsync();

        Assert.Equal("import-failed", Assert.Single(failed.Items).ImportSessionId);
        Assert.Equal("diagnostic-partial", Assert.Single(warnings.Items).DiagnosticId);
        Assert.Equal(3, dashboard.ImportCount);
        Assert.Equal(1, dashboard.WarningDiagnosticCount);
        Assert.Equal(1, dashboard.ErrorDiagnosticCount);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetImportsAsync(AllImports(status: "already_imported")));
    }

    [Fact]
    public async Task PaginationIsStableAndBoundedForLargeData()
    {
        const int rowCount = 225;
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);
        await SeedLargeEventSetAsync(location, rowCount);
        var service = new SqliteViewerQueryService(location);

        var first = await service.GetEventsAsync(
            AllAudit(page: new PageRequest(0, 25)));
        var second = await service.GetEventsAsync(
            AllAudit(page: new PageRequest(25, 25)));

        Assert.Equal(rowCount, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(25, second.Items.Count);
        Assert.True(first.HasNext);
        Assert.Empty(first.Items.Select(item => item.DeleteEventId)
            .Intersect(second.Items.Select(item => item.DeleteEventId)));
        Assert.Equal("bulk-event-0225", first.Items[0].DeleteEventId);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PageRequest(0, PageRequest.MaximumLimit + 1).Validate());
    }

    [Fact]
    public async Task ReadOnlyConnectionRejectsWrites()
    {
        var location = CreateLocation();
        await CreateDatabaseAsync(location, applySchema: true);

        await using var connection = location.CreateReadOnlyConnection();
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);
        Assert.Equal(SqliteCacheMode.Private, builder.Cache);
        Assert.False(builder.Pooling);

        await connection.OpenAsync();
        using var command = connection.CreateCommand();
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
                'forbidden',
                'multi_xml',
                'forbidden.xml',
                'forbidden.xml',
                0,
                '2026-07-01T00:00:00.0000000+00:00',
                randomblob(32),
                '2026-07-01T00:00:00.0000000+00:00',
                '2026-07-01T00:00:00.0000000+00:00',
                0,
                0,
                0,
                0,
                'test',
                2,
                'completed');
            """;

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(8, exception.SqliteErrorCode);
    }

    private static ViewerDataLocation CreateLocation()
    {
        var directory = Path.Combine(
            ViewerDataLocation.DefaultRoot,
            "tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return ViewerDataLocation.CreateForTesting(
            Path.Combine(directory, "viewer.db"),
            Path.Combine(directory, "jsonl"));
    }

    private static async Task CreateDatabaseAsync(
        ViewerDataLocation location,
        bool applySchema)
    {
        await using var connection = CreateWritableConnection(location.DatabasePath);
        await connection.OpenAsync();
        if (!applySchema)
        {
            return;
        }

        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var schema = await File.ReadAllTextAsync(
            Path.Combine(fixtureDirectory, "schema.sql"));
        var migration = await File.ReadAllTextAsync(
            Path.Combine(fixtureDirectory, "0002_phase_1b_offline_import.sql"));
        using var command = connection.CreateCommand();
        command.CommandText = $"{schema}{Environment.NewLine}{migration}";
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection CreateWritableConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task ExecuteWriteAsync(
        ViewerDataLocation location,
        string sql)
    {
        await using var connection = CreateWritableConnection(location.DatabasePath);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountUserObjectsAsync(ViewerDataLocation location)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%';
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static AuditQuery AllAudit(
        AuditRiskLevel? risk = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        string? path = null,
        string? process = null,
        PageRequest? page = null) =>
        new(risk, fromUtc, toUtc, path, process, page ?? new PageRequest());

    private static ImportHistoryQuery AllImports(
        string? status = null,
        string? sourcePath = null) =>
        new(status, null, null, sourcePath, new PageRequest());

    private static DiagnosticQuery AllDiagnostics(
        ImportDiagnosticSeverity? severity = null,
        string? text = null) =>
        new(severity, null, null, text, new PageRequest());

    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 7, 1, hour, minute, 0, TimeSpan.Zero);

    private static string CreateDeterministicXml(int totalLength)
    {
        const string prefix = "<Event id=\"preview\">";
        const string suffix = "</Event>";
        var builder = new System.Text.StringBuilder(totalLength);
        builder.Append(prefix);
        for (var index = 0; index < totalLength - prefix.Length - suffix.Length; index++)
        {
            builder.Append((char)('a' + (index % 26)));
        }

        builder.Append(suffix);
        return builder.ToString();
    }

    private static async Task SeedSingleRawXmlEventAsync(
        ViewerDataLocation location,
        string rawXml)
    {
        await using var connection = CreateWritableConnection(location.DatabasePath);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_epochs (
                channel_epoch_id, computer_name, channel_name, provider_name,
                started_utc, first_record_id, start_reason, coverage_gap)
            VALUES (
                'epoch-preview', 'LAB-PC', 'Microsoft-Windows-Sysmon/Operational',
                'Microsoft-Windows-Sysmon', '2026-07-04T00:00:00.0000000+00:00',
                1, 'initial', 0);

            INSERT INTO raw_events (
                raw_event_id, channel_epoch_id, source, computer_name, channel_name,
                provider_name, event_id, event_record_id, event_utc, event_local,
                local_utc_offset_minutes, windows_time_zone_id, observed_utc,
                raw_xml, raw_xml_sha256, ingest_sequence, previous_entry_hash,
                entry_hash, format_version)
            VALUES (
                'raw-preview', 'epoch-preview', 'sysmon_delete', 'LAB-PC',
                'Microsoft-Windows-Sysmon/Operational', 'Microsoft-Windows-Sysmon',
                26, 1, '2026-07-04T00:00:00.0000000+00:00',
                '2026-07-04T00:00:00.0000000+00:00', 0, 'UTC',
                '2026-07-04T00:00:00.0000000+00:00', $raw_xml, randomblob(32), 1,
                NULL, randomblob(32), 1);

            INSERT INTO delete_sessions (
                delete_session_id, opened_utc, last_event_utc, sealed_utc,
                process_identity, process_id, process_guid, user_sid, path_scope,
                confirmed_item_count, protected_item_count, current_risk,
                warning_emitted, critical_emitted, integrity_hash)
            VALUES (
                'session-preview', '2026-07-04T00:00:00.0000000+00:00',
                '2026-07-04T00:00:00.0000000+00:00',
                '2026-07-04T00:00:00.0000000+00:00', 'pid:LAB-PC:404', 404, NULL,
                NULL, 'C:\Preview\big.txt', 1, 0, 'informational', 0, 0,
                randomblob(32));

            INSERT INTO delete_events (
                delete_event_id, primary_raw_event_id, delete_session_id,
                occurred_utc, occurred_local, local_utc_offset_minutes,
                windows_time_zone_id, event_record_id, source, source_event_id,
                full_path, normalized_path, object_kind, process_id, process_path,
                process_guid, command_line, parent_process_id, parent_process_path,
                parent_process_guid, user_name, user_sid, delete_permission_type,
                initial_risk, attribution_confidence, archive_expected,
                archive_reference, missing_fields_json, content_sha256,
                integrity_hash)
            VALUES (
                'preview-event', 'raw-preview', 'session-preview',
                '2026-07-04T00:00:00.0000000+00:00',
                '2026-07-04T00:00:00.0000000+00:00', 0, 'UTC', 1, 'sysmon_26', 26,
                'C:\Preview\big.txt', 'C:\Preview\big.txt', 'file', 404,
                'C:\Windows\notepad.exe', NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                'not_observed', 'informational', 50, 0, NULL, '[]', randomblob(32),
                randomblob(32));
            """;
        command.Parameters.Add("$raw_xml", SqliteType.Text).Value = rawXml;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedLargeEventSetAsync(
        ViewerDataLocation location,
        int rowCount)
    {
        var sql = LargeSeedSql.Replace(
            "$ROW_COUNT$",
            rowCount.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        await ExecuteWriteAsync(location, sql);
    }

    private const string SeedSql = """
        INSERT INTO channel_epochs (
            channel_epoch_id, computer_name, channel_name, provider_name,
            started_utc, first_record_id, start_reason, coverage_gap)
        VALUES (
            'epoch-viewer', 'LAB-PC', 'Microsoft-Windows-Sysmon/Operational',
            'Microsoft-Windows-Sysmon', '2026-07-01T00:00:00.0000000+00:00',
            1, 'initial', 0);

        INSERT INTO raw_events (
            raw_event_id, channel_epoch_id, source, computer_name, channel_name,
            provider_name, event_id, event_record_id, event_utc, event_local,
            local_utc_offset_minutes, windows_time_zone_id, observed_utc, raw_xml,
            raw_xml_sha256, ingest_sequence, previous_entry_hash, entry_hash,
            format_version)
        VALUES
            ('raw-critical', 'epoch-viewer', 'sysmon_delete', 'LAB-PC',
             'Microsoft-Windows-Sysmon/Operational', 'Microsoft-Windows-Sysmon',
             26, 1, '2026-07-01T10:00:00.0000000+00:00',
             '2026-07-01T10:00:00.0000000+00:00', 0, 'UTC',
             '2026-07-01T10:00:00.0000000+00:00',
             '<Event id="critical">&amp;</Event>', randomblob(32), 1, NULL,
             randomblob(32), 1),
            ('raw-warning', 'epoch-viewer', 'sysmon_delete', 'LAB-PC',
             'Microsoft-Windows-Sysmon/Operational', 'Microsoft-Windows-Sysmon',
             26, 2, '2026-07-01T11:00:00.0000000+00:00',
             '2026-07-01T11:00:00.0000000+00:00', 0, 'UTC',
             '2026-07-01T11:00:00.0000000+00:00',
             '<Event id="warning" />', randomblob(32), 2, NULL,
             randomblob(32), 1),
            ('raw-info', 'epoch-viewer', 'sysmon_delete', 'LAB-PC',
             'Microsoft-Windows-Sysmon/Operational', 'Microsoft-Windows-Sysmon',
             26, 3, '2026-07-01T12:00:00.0000000+00:00',
             '2026-07-01T12:00:00.0000000+00:00', 0, 'UTC',
             '2026-07-01T12:00:00.0000000+00:00',
             '<Event id="info" />', randomblob(32), 3, NULL,
             randomblob(32), 1);

        INSERT INTO delete_sessions (
            delete_session_id, opened_utc, last_event_utc, sealed_utc,
            process_identity, process_id, process_guid, user_sid, path_scope,
            confirmed_item_count, protected_item_count, current_risk,
            warning_emitted, critical_emitted, integrity_hash)
        VALUES
            ('session-critical', '2026-07-01T10:00:00.0000000+00:00',
             '2026-07-01T10:00:00.0000000+00:00',
             '2026-07-01T10:00:00.0000000+00:00',
             'guid:{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}', 101,
             '{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}', NULL,
             'C:\Critical\alpha.txt', 1, 0, 'critical', 0, 0, randomblob(32)),
            ('session-warning', '2026-07-01T11:00:00.0000000+00:00',
             '2026-07-01T11:00:00.0000000+00:00',
             '2026-07-01T11:00:00.0000000+00:00',
             'guid:{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}', 202,
             '{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}', NULL,
             'C:\Percent\100%_done.txt', 1, 0, 'warning', 0, 0, randomblob(32)),
            ('session-info', '2026-07-01T12:00:00.0000000+00:00',
             '2026-07-01T12:00:00.0000000+00:00',
             '2026-07-01T12:00:00.0000000+00:00',
             'pid:LAB-PC:303', 303, NULL, NULL,
             'C:\Other\notes.txt', 1, 0, 'informational', 0, 0, randomblob(32));

        INSERT INTO delete_events (
            delete_event_id, primary_raw_event_id, delete_session_id,
            occurred_utc, occurred_local, local_utc_offset_minutes,
            windows_time_zone_id, event_record_id, source, source_event_id,
            full_path, normalized_path, object_kind, process_id, process_path,
            process_guid, command_line, parent_process_id, parent_process_path,
            parent_process_guid, user_name, user_sid, delete_permission_type,
            initial_risk, attribution_confidence, archive_expected,
            archive_reference, missing_fields_json, content_sha256,
            integrity_hash)
        VALUES
            ('event-critical', 'raw-critical', 'session-critical',
             '2026-07-01T10:00:00.0000000+00:00',
             '2026-07-01T10:00:00.0000000+00:00', 0, 'UTC', 1,
             'sysmon_26', 26, 'C:\Critical\alpha.txt',
             'C:\Critical\alpha.txt', 'file', 101,
             'C:\Tools\Eraser.exe',
             '{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}', NULL, NULL, NULL, NULL,
             NULL, NULL, 'not_observed', 'critical', 100, 0, NULL, '[]',
             randomblob(32), randomblob(32)),
            ('event-warning', 'raw-warning', 'session-warning',
             '2026-07-01T11:00:00.0000000+00:00',
             '2026-07-01T11:00:00.0000000+00:00', 0, 'UTC', 2,
             'sysmon_26', 26, 'C:\Percent\100%_done.txt',
             'C:\Percent\100%_done.txt', 'file', 202, NULL,
             '{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}', NULL, NULL, NULL, NULL,
             NULL, NULL, 'not_observed', 'warning', 75, 0, NULL,
             '["processPath"]', randomblob(32), randomblob(32)),
            ('event-info', 'raw-info', 'session-info',
             '2026-07-01T12:00:00.0000000+00:00',
             '2026-07-01T12:00:00.0000000+00:00', 0, 'UTC', 3,
             'sysmon_26', 26, 'C:\Other\notes.txt',
             'C:\Other\notes.txt', 'file', 303,
             'C:\Windows\notepad.exe', NULL, NULL, NULL, NULL, NULL, NULL, NULL,
             'not_observed', 'informational', 50, 0, NULL, '[]',
             randomblob(32), randomblob(32));

        INSERT INTO import_sessions (
            import_session_id, source_kind, original_file_name,
            normalized_source_path, file_size_bytes, source_last_write_utc,
            source_sha256, started_utc, completed_utc, total_record_count,
            success_record_count, ignored_record_count, error_record_count,
            application_version, schema_version, status, output_status)
        VALUES
            ('import-complete', 'multi_xml', 'complete.xml',
             'C:\Fixtures\complete.xml', 128,
             '2026-07-02T08:00:00.0000000+00:00', randomblob(32),
             '2026-07-02T08:00:00.0000000+00:00',
             '2026-07-02T08:01:00.0000000+00:00', 1, 1, 0, 0,
             'phase-1c-test', 2, 'completed', 'complete'),
            ('import-partial', 'multi_xml', 'partial.xml',
             'C:\Fixtures\partial.xml', 128,
             '2026-07-02T09:00:00.0000000+00:00', randomblob(32),
             '2026-07-02T09:00:00.0000000+00:00',
             '2026-07-02T09:01:00.0000000+00:00', 2, 1, 0, 1,
             'phase-1c-test', 2, 'partial_failure', 'complete'),
            ('import-failed', 'multi_xml', 'failed.xml',
             'C:\Fixtures\failed.xml', 128,
             '2026-07-02T10:00:00.0000000+00:00', randomblob(32),
             '2026-07-02T10:00:00.0000000+00:00',
             '2026-07-02T10:01:00.0000000+00:00', 1, 0, 0, 1,
             'phase-1c-test', 2, 'failed', 'complete');

        INSERT INTO import_diagnostics (
            import_diagnostic_id, import_session_id, record_ordinal, stage,
            severity, code, message, details_json, occurred_utc)
        VALUES
            ('diagnostic-partial', 'import-partial', NULL, 'parse', 'warning',
             'partial_diagnostic', 'partial diagnostic message', '{}',
             '2026-07-02T09:00:30.0000000+00:00'),
            ('diagnostic-failed', 'import-failed', NULL, 'parse', 'error',
             'failed_diagnostic', 'failed diagnostic message', '{}',
             '2026-07-02T10:00:30.0000000+00:00');
        """;

    private const string LargeSeedSql = """
        INSERT INTO channel_epochs (
            channel_epoch_id, computer_name, channel_name, provider_name,
            started_utc, first_record_id, start_reason, coverage_gap)
        VALUES (
            'epoch-bulk', 'LAB-PC', 'Microsoft-Windows-Sysmon/Operational',
            'Microsoft-Windows-Sysmon', '2026-07-03T00:00:00.0000000+00:00',
            1, 'initial', 0);

        INSERT INTO delete_sessions (
            delete_session_id, opened_utc, last_event_utc, sealed_utc,
            process_identity, process_id, process_guid, user_sid, path_scope,
            confirmed_item_count, protected_item_count, current_risk,
            warning_emitted, critical_emitted, integrity_hash)
        VALUES (
            'bulk-session', '2026-07-03T00:00:00.0000000+00:00',
            '2026-07-03T00:10:00.0000000+00:00',
            '2026-07-03T00:10:00.0000000+00:00', 'pid:LAB-PC:999', 999,
            NULL, NULL, 'C:\Bulk', $ROW_COUNT$, 0, 'critical', 0, 0,
            randomblob(32));

        WITH RECURSIVE sequence(value) AS (
            VALUES (1)
            UNION ALL
            SELECT value + 1
            FROM sequence
            WHERE value < $ROW_COUNT$
        )
        INSERT INTO raw_events (
            raw_event_id, channel_epoch_id, source, computer_name, channel_name,
            provider_name, event_id, event_record_id, event_utc, event_local,
            local_utc_offset_minutes, windows_time_zone_id, observed_utc,
            raw_xml, raw_xml_sha256, ingest_sequence, previous_entry_hash,
            entry_hash, format_version)
        SELECT
            printf('bulk-raw-%04d', value),
            'epoch-bulk',
            'sysmon_delete',
            'LAB-PC',
            'Microsoft-Windows-Sysmon/Operational',
            'Microsoft-Windows-Sysmon',
            26,
            value,
            strftime(
                '%Y-%m-%dT%H:%M:%f+00:00',
                '2026-07-03T00:00:00',
                printf('+%d seconds', value)),
            strftime(
                '%Y-%m-%dT%H:%M:%f+00:00',
                '2026-07-03T00:00:00',
                printf('+%d seconds', value)),
            0,
            'UTC',
            strftime(
                '%Y-%m-%dT%H:%M:%f+00:00',
                '2026-07-03T00:00:00',
                printf('+%d seconds', value)),
            printf('<Event id="%d" />', value),
            randomblob(32),
            value,
            NULL,
            randomblob(32),
            1
        FROM sequence;

        INSERT INTO delete_events (
            delete_event_id, primary_raw_event_id, delete_session_id,
            occurred_utc, occurred_local, local_utc_offset_minutes,
            windows_time_zone_id, event_record_id, source, source_event_id,
            full_path, normalized_path, object_kind, process_id, process_path,
            process_guid, command_line, parent_process_id, parent_process_path,
            parent_process_guid, user_name, user_sid, delete_permission_type,
            initial_risk, attribution_confidence, archive_expected,
            archive_reference, missing_fields_json, content_sha256,
            integrity_hash)
        SELECT
            printf('bulk-event-%04d', event_record_id),
            raw_event_id,
            'bulk-session',
            event_utc,
            event_local,
            0,
            'UTC',
            event_record_id,
            'sysmon_26',
            26,
            printf('C:\Bulk\file-%04d.txt', event_record_id),
            printf('C:\Bulk\file-%04d.txt', event_record_id),
            'file',
            999,
            'C:\Tools\bulk.exe',
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            'not_observed',
            'critical',
            100,
            0,
            NULL,
            '[]',
            randomblob(32),
            randomblob(32)
        FROM raw_events
        WHERE raw_event_id LIKE 'bulk-raw-%';
        """;
}
