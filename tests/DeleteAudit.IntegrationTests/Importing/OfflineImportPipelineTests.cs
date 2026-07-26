using System.Text;
using System.Text.Json;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;
using DeleteAudit.Infrastructure.Importing;
using DeleteAudit.Infrastructure.Importing.Output;
using DeleteAudit.Infrastructure.Importing.Persistence;
using DeleteAudit.Infrastructure.Importing.Sources;
using DeleteAudit.Infrastructure.Integrity;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.Importing;

[CollectionDefinition("Phase 1B file output", DisableParallelization = true)]
public sealed class Phase1BFileOutputGroup
{
    public const string Name = "Phase 1B file output";
}

[Collection(Phase1BFileOutputGroup.Name)]
public sealed class OfflineImportPipelineTests
{
    private static readonly string OutputRoot =
        Path.Combine(RepositoryRoot.ArtifactsDirectory, "test-output");
    private const string ProcessGuid = "{11111111-2222-3333-4444-555555555555}";
    private const string ParentProcessGuid = "{11111111-2222-3333-4444-000000000800}";
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ImportOutOfOrderEvidencePersistsCompleteGraphAndAccurateReport()
    {
        var inputPath = await WriteEnvelopeAsync(
            "complete-graph",
            SysmonDelete(2002, @"C:\Protected\alpha.txt"),
            SysmonProcess(2001),
            SecurityDelete(2003, @"C:\Protected\alpha.txt"));
        var bytesBefore = await File.ReadAllBytesAsync(inputPath);
        var lastWriteBefore = File.GetLastWriteTimeUtc(inputPath);

        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var pipeline = CreatePipeline(
            repository,
            new FileImportJsonlWriter(),
            [new ProtectedPathRule("protected", @"C:\Protected")]);

        var result = await pipeline.ImportAsync(CreateRequest(inputPath));

        Assert.Equal(ImportStatus.Completed, result.Status);
        Assert.True(result.DatabaseCommitted);
        var session = Assert.IsType<ImportSession>(result.Session);
        Assert.Equal(3, session.TotalRecordCount);
        Assert.Equal(3, session.SuccessCount);
        Assert.Equal(0, session.IgnoredCount);
        Assert.Equal(0, session.ErrorCount);

        var expectedCounts = new DatabaseCounts(
            ChannelEpochs: 2,
            ImportSessions: 1,
            ImportRecords: 3,
            ImportDiagnostics: 0,
            RawEvents: 3,
            ProcessObservations: 1,
            DeleteSessions: 1,
            DeleteEvents: 1,
            EventCorrelations: 1,
            EventEvidence: 1,
            SessionMembers: 1,
            RiskAssessments: 1,
            RiskSubjectLinks: 1);
        Assert.Equal(expectedCounts, await ReadCountsAsync(connection));

        Assert.Equal(3, result.Report.ParsedSuccessCount);
        Assert.Equal(0, result.Report.ParsedFailureCount);
        Assert.Equal(3, result.Report.EventIdCounts.Count);
        Assert.Equal(1, result.Report.EventIdCounts[1]);
        Assert.Equal(1, result.Report.EventIdCounts[26]);
        Assert.Equal(1, result.Report.EventIdCounts[4663]);
        Assert.Equal(1, result.Report.DeleteFactCount);
        Assert.Equal(1, result.Report.CorrelationConfidenceCounts[CorrelationConfidence.High]);
        Assert.Equal(0, result.Report.CorrelationConfidenceCounts[CorrelationConfidence.Medium]);
        Assert.Equal(0, result.Report.CorrelationConfidenceCounts[CorrelationConfidence.Low]);
        Assert.Equal(0, result.Report.CorrelationConfidenceCounts[CorrelationConfidence.None]);
        Assert.Equal(0, result.Report.WarningSessionCount);
        Assert.Equal(1, result.Report.CriticalSessionCount);
        var topPath = Assert.Single(result.Report.TopHighRiskPaths);
        Assert.Equal(@"C:\Protected\alpha.txt", topPath.Path);
        Assert.Equal(AuditRiskLevel.Critical, topPath.RiskLevel);
        Assert.Equal(1, topPath.DeleteFactCount);
        Assert.Empty(result.Report.Diagnostics);

        Assert.Equal(
            "process_guid",
            await ScalarTextAsync(
                connection,
                "SELECT method FROM event_correlations;"));
        Assert.Equal(
            "high",
            await ScalarTextAsync(
                connection,
                "SELECT confidence FROM event_correlations;"));
        Assert.Equal(
            "critical",
            await ScalarTextAsync(
                connection,
                "SELECT current_risk FROM delete_sessions;"));
        Assert.Equal(
            "delete",
            await ScalarTextAsync(
                connection,
                "SELECT delete_permission_type FROM delete_events;"));
        Assert.Equal(
            "S-1-5-21-1000",
            await ScalarTextAsync(
                connection,
                "SELECT user_sid FROM delete_events;"));

        var jsonlPath = Assert.IsType<string>(result.JsonlFilePath);
        var manifestPath = Assert.IsType<string>(result.ManifestFilePath);
        Assert.True(File.Exists(jsonlPath));
        Assert.True(File.Exists(manifestPath));
        var lines = await File.ReadAllLinesAsync(jsonlPath);
        Assert.Equal(3, lines.Length);
        var verification = JsonlHashChain.Verify(lines);
        Assert.True(verification.IsValid, verification.FailureReason);
        Assert.Equal(3, verification.VerifiedEntryCount);

        using (var manifest = JsonDocument.Parse(
                   await File.ReadAllTextAsync(manifestPath)))
        {
            Assert.Equal(
                "success",
                manifest.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                3,
                manifest.RootElement.GetProperty("entryCount").GetInt32());
            Assert.Equal(
                session.ImportSessionId,
                manifest.RootElement.GetProperty("importSessionId").GetString());
        }

        var bytesAfter = await File.ReadAllBytesAsync(inputPath);
        var lastWriteAfter = File.GetLastWriteTimeUtc(inputPath);
        Assert.Equal(bytesBefore, bytesAfter);
        Assert.Equal(lastWriteBefore, lastWriteAfter);
    }

    [Fact]
    public async Task ImportMalformedInnerRecordContinuesAndCountsPartialFailure()
    {
        var inputPath = await WriteEnvelopeAsync(
            "partial-malformed",
            SysmonDelete(2102, @"C:\Work\partial.txt"),
            "<Event",
            SysmonProcess(2101));
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new SuccessfulRecordingWriter();
        var pipeline = CreatePipeline(repository, writer);

        var result = await pipeline.ImportAsync(CreateRequest(inputPath));

        Assert.Equal(ImportStatus.PartialFailure, result.Status);
        Assert.True(result.DatabaseCommitted);
        var session = Assert.IsType<ImportSession>(result.Session);
        Assert.Equal(3, session.TotalRecordCount);
        Assert.Equal(2, session.SuccessCount);
        Assert.Equal(0, session.IgnoredCount);
        Assert.Equal(1, session.ErrorCount);
        Assert.Equal(2, result.Report.ParsedSuccessCount);
        Assert.Equal(1, result.Report.ParsedFailureCount);
        Assert.Equal(1, result.Report.EventIdCounts[1]);
        Assert.Equal(1, result.Report.EventIdCounts[26]);
        Assert.Equal(1, result.Report.DeleteFactCount);
        var diagnostic = Assert.Single(
            result.Report.Diagnostics,
            item =>
                item.Code == "parse_malformedxml"
                && item.RecordNumber == 2);
        Assert.Equal("parse", diagnostic.Stage);
        Assert.Equal(ImportDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(1, writer.CallCount);

        var counts = await ReadCountsAsync(connection);
        Assert.Equal(1, counts.ImportSessions);
        Assert.Equal(3, counts.ImportRecords);
        Assert.Equal(1, counts.ImportDiagnostics);
        Assert.Equal(2, counts.RawEvents);
        Assert.Equal(1, counts.ProcessObservations);
        Assert.Equal(1, counts.DeleteEvents);
        Assert.Equal(
            "2,0,1",
            await ScalarTextAsync(
                connection,
                """
                SELECT
                    SUM(CASE WHEN outcome = 'success' THEN 1 ELSE 0 END)
                    || ',' ||
                    SUM(CASE WHEN outcome = 'ignored' THEN 1 ELSE 0 END)
                    || ',' ||
                    SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END)
                FROM import_records;
                """));
    }

    [Fact]
    public async Task ImportSameShaTwiceReturnsAlreadyImportedWithoutRowsOrSecondJsonl()
    {
        var inputPath = await WriteEnvelopeAsync(
            "duplicate-sha",
            SysmonDelete(2201, @"C:\Work\duplicate.txt"));
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new CountingJsonlWriter(new FileImportJsonlWriter());
        var pipeline = CreatePipeline(repository, writer);

        var first = await pipeline.ImportAsync(CreateRequest(inputPath));
        var countsAfterFirst = await ReadCountsAsync(connection);
        var second = await pipeline.ImportAsync(CreateRequest(inputPath));
        var countsAfterSecond = await ReadCountsAsync(connection);

        Assert.Equal(ImportStatus.Completed, first.Status);
        var firstJsonlPath = Assert.IsType<string>(first.JsonlFilePath);
        Assert.True(File.Exists(firstJsonlPath));
        Assert.Equal(ImportStatus.AlreadyImported, second.Status);
        Assert.True(second.DatabaseCommitted);
        Assert.Contains(
            second.Report.Diagnostics,
            item =>
                item.Code == "already_imported"
                && item.Severity == ImportDiagnosticSeverity.Information);
        Assert.Null(second.JsonlFilePath);
        Assert.Null(second.ManifestFilePath);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(countsAfterFirst, countsAfterSecond);
        Assert.Equal(1, countsAfterSecond.ImportSessions);
        Assert.Equal(1, countsAfterSecond.ImportRecords);
        Assert.Equal(1, countsAfterSecond.RawEvents);
        Assert.Equal(1, countsAfterSecond.DeleteEvents);
    }

    [Fact]
    public async Task ImportSameFileNameWithDifferentContentCreatesDistinctSessions()
    {
        var scenarioId = Guid.NewGuid().ToString("N");
        var firstDirectory = Path.Combine(OutputRoot, $"same-name-a-{scenarioId}");
        var secondDirectory = Path.Combine(OutputRoot, $"same-name-b-{scenarioId}");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstPath = Path.Combine(firstDirectory, "same-name.xml");
        var secondPath = Path.Combine(secondDirectory, "same-name.xml");
        await WriteNewFileAsync(
            firstPath,
            CreateEnvelope(SysmonDelete(2301, @"C:\Work\first.txt")));
        await WriteNewFileAsync(
            secondPath,
            CreateEnvelope(SysmonDelete(2302, @"C:\Work\second.txt")));

        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new SuccessfulRecordingWriter();
        var pipeline = CreatePipeline(repository, writer);

        var first = await pipeline.ImportAsync(CreateRequest(firstPath));
        var second = await pipeline.ImportAsync(CreateRequest(secondPath));

        Assert.Equal(ImportStatus.Completed, first.Status);
        Assert.Equal(ImportStatus.Completed, second.Status);
        var firstSession = Assert.IsType<ImportSession>(first.Session);
        var secondSession = Assert.IsType<ImportSession>(second.Session);
        Assert.Equal("same-name.xml", firstSession.OriginalFileName);
        Assert.Equal("same-name.xml", secondSession.OriginalFileName);
        Assert.NotEqual(firstSession.Sha256, secondSession.Sha256);
        Assert.NotEqual(firstSession.ImportSessionId, secondSession.ImportSessionId);
        Assert.Equal(2, writer.CallCount);

        var counts = await ReadCountsAsync(connection);
        Assert.Equal(2, counts.ImportSessions);
        Assert.Equal(2, counts.ImportRecords);
        Assert.Equal(2, counts.RawEvents);
        Assert.Equal(2, counts.DeleteEvents);
        Assert.Equal(
            "2,1",
            await ScalarTextAsync(
                connection,
                """
                SELECT
                    COUNT(DISTINCT normalized_source_path)
                    || ',' ||
                    COUNT(DISTINCT original_file_name)
                FROM import_sessions;
                """));
    }

    [Fact]
    public async Task ImportOverlappingFileOnlyAggregatesNewDeleteFact()
    {
        var firstEvent = SysmonDelete(
            2351,
            @"C:\Work\overlap-first.txt");
        var secondEvent = SysmonDelete(
            2352,
            @"C:\Work\overlap-second.txt");
        var firstPath = await WriteEnvelopeAsync(
            "overlap-first",
            firstEvent);
        var secondPath = await WriteEnvelopeAsync(
            "overlap-second",
            firstEvent,
            secondEvent);

        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new SuccessfulRecordingWriter();
        var pipeline = CreatePipeline(repository, writer);

        var first = await pipeline.ImportAsync(CreateRequest(firstPath));
        var second = await pipeline.ImportAsync(CreateRequest(secondPath));

        Assert.Equal(ImportStatus.Completed, first.Status);
        Assert.Equal(ImportStatus.Completed, second.Status);
        Assert.True(first.DatabaseCommitted);
        Assert.True(second.DatabaseCommitted);
        var firstSession = Assert.IsType<ImportSession>(first.Session);
        var secondSession = Assert.IsType<ImportSession>(second.Session);
        Assert.NotEqual(firstSession.Sha256, secondSession.Sha256);
        Assert.Equal(1, firstSession.TotalRecordCount);
        Assert.Equal(1, firstSession.SuccessCount);
        Assert.Equal(2, secondSession.TotalRecordCount);
        Assert.Equal(1, secondSession.SuccessCount);
        Assert.Equal(1, secondSession.IgnoredCount);
        Assert.Equal(0, secondSession.ErrorCount);
        Assert.Equal(1, second.Report.DeleteFactCount);
        Assert.Equal(0, second.Report.WarningSessionCount);
        Assert.Equal(0, second.Report.CriticalSessionCount);
        var duplicateDiagnostic = Assert.Single(
            second.Report.Diagnostics,
            item =>
                item.Code == "delete_fact_already_imported"
                && item.RecordNumber == 1);
        Assert.Equal("normalize", duplicateDiagnostic.Stage);
        Assert.Equal(
            ImportDiagnosticSeverity.Information,
            duplicateDiagnostic.Severity);
        Assert.Equal(2, writer.CallCount);

        Assert.Equal(
            "ignored",
            await ScalarTextAsync(
                connection,
                """
                SELECT outcome
                FROM import_records
                WHERE import_session_id = $import_session_id
                  AND record_ordinal = 1;
                """,
                ("$import_session_id", secondSession.ImportSessionId)));
        Assert.Equal(
            "delete_fact_already_imported,1",
            await ScalarTextAsync(
                connection,
                """
                SELECT code || ',' || record_ordinal
                FROM import_diagnostics
                WHERE import_session_id = $import_session_id
                  AND code = 'delete_fact_already_imported';
                """,
                ("$import_session_id", secondSession.ImportSessionId)));
        Assert.Equal(
            "completed,1,1,0,complete",
            await ScalarTextAsync(
                connection,
                """
                SELECT status
                    || ',' || success_record_count
                    || ',' || ignored_record_count
                    || ',' || error_record_count
                    || ',' || output_status
                FROM import_sessions
                WHERE import_session_id = $import_session_id;
                """,
                ("$import_session_id", secondSession.ImportSessionId)));
        Assert.Equal(
            "1,1,informational",
            await ScalarTextAsync(
                connection,
                """
                SELECT ds.confirmed_item_count
                    || ',' || COUNT(sm.delete_event_id)
                    || ',' || ds.current_risk
                FROM import_records ir
                JOIN delete_events de
                  ON de.primary_raw_event_id = ir.raw_event_id
                JOIN delete_sessions ds
                  ON ds.delete_session_id = de.delete_session_id
                LEFT JOIN session_members sm
                  ON sm.delete_session_id = ds.delete_session_id
                WHERE ir.import_session_id = $import_session_id
                  AND ir.record_ordinal = 2
                GROUP BY ds.delete_session_id,
                         ds.confirmed_item_count,
                         ds.current_risk;
                """,
                ("$import_session_id", secondSession.ImportSessionId)));
        Assert.Equal(
            "0,0",
            await ScalarTextAsync(
                connection,
                """
                SELECT SUM(CASE WHEN current_risk = 'warning' THEN 1 ELSE 0 END)
                    || ',' ||
                    SUM(CASE WHEN current_risk = 'critical' THEN 1 ELSE 0 END)
                FROM delete_sessions;
                """));

        var counts = await ReadCountsAsync(connection);
        Assert.Equal(2, counts.ImportSessions);
        Assert.Equal(3, counts.ImportRecords);
        Assert.Equal(1, counts.ImportDiagnostics);
        Assert.Equal(2, counts.RawEvents);
        Assert.Equal(2, counts.DeleteSessions);
        Assert.Equal(2, counts.DeleteEvents);
        Assert.Equal(2, counts.EventCorrelations);
        Assert.Equal(2, counts.EventEvidence);
        Assert.Equal(2, counts.SessionMembers);
        Assert.Equal(2, counts.RiskAssessments);
        Assert.Equal(2, counts.RiskSubjectLinks);
    }

    [Fact]
    public async Task ImportOnlyMalformedRecordStillWritesJsonlMetadataAfterCommit()
    {
        var inputPath = await WriteEnvelopeAsync(
            "only-malformed",
            "<Event");
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new SuccessfulRecordingWriter();
        var pipeline = CreatePipeline(repository, writer);

        var result = await pipeline.ImportAsync(CreateRequest(inputPath));

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.True(result.DatabaseCommitted);
        var session = Assert.IsType<ImportSession>(result.Session);
        Assert.Equal(1, session.TotalRecordCount);
        Assert.Equal(0, session.SuccessCount);
        Assert.Equal(0, session.IgnoredCount);
        Assert.Equal(1, session.ErrorCount);
        Assert.Equal(0, result.Report.ParsedSuccessCount);
        Assert.Equal(1, result.Report.ParsedFailureCount);
        Assert.Equal(0, result.Report.DeleteFactCount);
        var parseDiagnostic = Assert.Single(
            result.Report.Diagnostics,
            item =>
                item.Code == "parse_malformedxml"
                && item.RecordNumber == 1);
        Assert.Equal("parse", parseDiagnostic.Stage);
        Assert.Equal(ImportDiagnosticSeverity.Error, parseDiagnostic.Severity);
        Assert.Equal(1, writer.CallCount);

        var counts = await ReadCountsAsync(connection);
        Assert.Equal(1, counts.ImportSessions);
        Assert.Equal(1, counts.ImportRecords);
        Assert.Equal(1, counts.ImportDiagnostics);
        Assert.Equal(0, counts.RawEvents);
        Assert.Equal(0, counts.DeleteSessions);
        Assert.Equal(0, counts.DeleteEvents);
        Assert.Equal(0, counts.SessionMembers);
        Assert.Equal(0, counts.RiskAssessments);
        Assert.Equal(
            "failed,complete",
            await ScalarTextAsync(
                connection,
                """
                SELECT status || ',' || output_status
                FROM import_sessions
                WHERE import_session_id = $import_session_id;
                """,
                ("$import_session_id", session.ImportSessionId)));
    }

    [Fact]
    public async Task RiskInsertFailureRollsBackEntireImportAndDoesNotInvokeWriter()
    {
        var inputPath = await WriteEnvelopeAsync(
            "transaction-rollback",
            SysmonDelete(2401, @"C:\Protected\rollback.txt"));
        await using var connection = await CreateDatabaseAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TRIGGER integration_fail_risk_insert
            BEFORE INSERT ON risk_assessments
            BEGIN
                SELECT RAISE(ABORT, 'injected risk assessment failure');
            END;
            """);
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new SuccessfulRecordingWriter();
        var pipeline = CreatePipeline(
            repository,
            writer,
            [new ProtectedPathRule("protected", @"C:\Protected")]);

        var result = await pipeline.ImportAsync(CreateRequest(inputPath));

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.False(result.DatabaseCommitted);
        Assert.Null(result.JsonlFilePath);
        Assert.Null(result.ManifestFilePath);
        Assert.Equal(0, writer.CallCount);
        Assert.Contains(
            result.Report.Diagnostics,
            item =>
                item.Code == "database_commit_failed"
                && item.Stage == "persist");
        Assert.Equal(DatabaseCounts.Empty, await ReadCountsAsync(connection));
    }

    [Fact]
    public async Task JsonlWriterFailureReturnsPartialFailureAfterDatabaseCommit()
    {
        var inputPath = await WriteEnvelopeAsync(
            "jsonl-failure",
            SysmonDelete(2501, @"C:\Work\jsonl-failure.txt"));
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: new FixedTimeProvider(FixedUtc));
        var writer = new FailingJsonlWriter();
        var pipeline = CreatePipeline(repository, writer);

        var result = await pipeline.ImportAsync(CreateRequest(inputPath));

        Assert.Equal(ImportStatus.PartialFailure, result.Status);
        Assert.True(result.DatabaseCommitted);
        Assert.Null(result.JsonlFilePath);
        Assert.Null(result.ManifestFilePath);
        Assert.Equal(1, writer.CallCount);
        Assert.Contains(
            result.Report.Diagnostics,
            item =>
                item.Code == "injected_jsonl_failure"
                && item.Stage == "jsonl");

        var counts = await ReadCountsAsync(connection);
        Assert.Equal(1, counts.ImportSessions);
        Assert.Equal(1, counts.ImportRecords);
        Assert.Equal(1, counts.RawEvents);
        Assert.Equal(1, counts.DeleteSessions);
        Assert.Equal(1, counts.DeleteEvents);
        Assert.Equal(1, counts.RiskAssessments);
        Assert.Equal(1, counts.ImportDiagnostics);
        Assert.Equal(
            "partial_failure,failed,injected_jsonl_failure",
            await ScalarTextAsync(
                connection,
                """
                SELECT status || ',' || output_status || ',' || output_error_code
                FROM import_sessions;
                """));
    }

    private static OfflineImportPipeline CreatePipeline(
        IOfflineImportRepository repository,
        IImportJsonlWriter writer,
        IEnumerable<ProtectedPathRule>? protectedRules = null) =>
        new(
            repository,
            writer,
            new CorrelationOptions(TimeSpan.FromSeconds(3)),
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100),
            protectedRules,
            [new MultiEventXmlOfflineEventSource()],
            new FixedTimeProvider(FixedUtc));

    private static ImportRequest CreateRequest(string inputPath) =>
        new(
            inputPath,
            4 * 1024 * 1024,
            OutputRoot,
            "1.1.0-integration",
            2);

    private static async Task<string> WriteEnvelopeAsync(
        string scenario,
        params string[] records)
    {
        Directory.CreateDirectory(OutputRoot);
        var path = Path.Combine(
            OutputRoot,
            $"{scenario}-{Guid.NewGuid():N}.xml");
        await WriteNewFileAsync(path, CreateEnvelope(records));
        return path;
    }

    private static string CreateEnvelope(params string[] records)
    {
        var output = new StringBuilder();
        output.Append(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <DeleteAuditOfflineEvents xmlns="urn:deleteaudit:offline:v1" formatVersion="1">
            """);
        foreach (var record in records)
        {
            output.Append("<Record><![CDATA[");
            output.Append(record);
            output.Append("]]></Record>");
        }

        output.Append("</DeleteAuditOfflineEvents>");
        return output.ToString();
    }

    private static async Task WriteNewFileAsync(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(content);
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static string SysmonDelete(long recordId, string targetPath) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Sysmon" />
             <EventID>26</EventID>
             <TimeCreated SystemTime="2026-07-23T12:00:01.0000000Z" />
             <EventRecordID>{recordId}</EventRecordID>
             <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="TargetFilename">{targetPath}</Data>
             <Data Name="User">Alice</Data>
             <Data Name="Image">C:\Tools\cleanup.exe</Data>
             <Data Name="ProcessGuid">{ProcessGuid}</Data>
             <Data Name="UtcTime">2026-07-23 12:00:01.000</Data>
             <Data Name="ProcessId">4242</Data>
           </EventData>
         </Event>
         """;

    private static string SysmonProcess(long recordId) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Sysmon" />
             <EventID>1</EventID>
             <TimeCreated SystemTime="2026-07-23T12:00:00.0000000Z" />
             <EventRecordID>{recordId}</EventRecordID>
             <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="ParentImage">C:\Windows\explorer.exe</Data>
             <Data Name="CommandLine">&quot;C:\Tools\cleanup.exe&quot; --fixture</Data>
             <Data Name="ProcessId">4242</Data>
             <Data Name="User">Alice</Data>
             <Data Name="ParentProcessId">800</Data>
             <Data Name="Image">C:\Tools\cleanup.exe</Data>
             <Data Name="UtcTime">2026-07-23 12:00:00.000</Data>
             <Data Name="ParentProcessGuid">{ParentProcessGuid}</Data>
             <Data Name="ProcessGuid">{ProcessGuid}</Data>
           </EventData>
         </Event>
         """;

    private static string SecurityDelete(long recordId, string targetPath) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Security-Auditing" />
             <EventID>4663</EventID>
             <TimeCreated SystemTime="2026-07-23T12:00:01.1000000Z" />
             <EventRecordID>{recordId}</EventRecordID>
             <Channel>Security</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="AccessMask">0x10000</Data>
             <Data Name="SubjectUserSid">S-1-5-21-1000</Data>
             <Data Name="ProcessName">C:\Tools\cleanup.exe</Data>
             <Data Name="ObjectName">{targetPath}</Data>
             <Data Name="SubjectUserName">Alice</Data>
             <Data Name="ProcessId">0x1092</Data>
             <Data Name="AccessList">DELETE</Data>
           </EventData>
         </Event>
         """;

    private static async Task<SqliteConnection> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema.sql"));
        var migration = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "0002_phase_1b_offline_import.sql"));
        await ExecuteAsync(connection, schema);
        await ExecuteAsync(connection, migration);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalarResult = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(scalarResult);
    }

    private static async Task<DatabaseCounts> ReadCountsAsync(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM channel_epochs),
                (SELECT COUNT(*) FROM import_sessions),
                (SELECT COUNT(*) FROM import_records),
                (SELECT COUNT(*) FROM import_diagnostics),
                (SELECT COUNT(*) FROM raw_events),
                (SELECT COUNT(*) FROM process_observations),
                (SELECT COUNT(*) FROM delete_sessions),
                (SELECT COUNT(*) FROM delete_events),
                (SELECT COUNT(*) FROM event_correlations),
                (SELECT COUNT(*) FROM event_evidence),
                (SELECT COUNT(*) FROM session_members),
                (SELECT COUNT(*) FROM risk_assessments),
                (SELECT COUNT(*) FROM risk_assessment_subject_links);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DatabaseCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12));
    }

    private sealed record DatabaseCounts(
        long ChannelEpochs,
        long ImportSessions,
        long ImportRecords,
        long ImportDiagnostics,
        long RawEvents,
        long ProcessObservations,
        long DeleteSessions,
        long DeleteEvents,
        long EventCorrelations,
        long EventEvidence,
        long SessionMembers,
        long RiskAssessments,
        long RiskSubjectLinks)
    {
        public static DatabaseCounts Empty { get; } =
            new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class SuccessfulRecordingWriter : IImportJsonlWriter
    {
        public int CallCount { get; private set; }

        public Task<ImportJsonlWriteResult> WriteAsync(
            ImportSession importSession,
            IReadOnlyCollection<ImportJsonlRecord> records,
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ImportJsonlWriteResult(
                true,
                null,
                null,
                records.Count,
                null,
                null,
                null,
                null));
        }
    }

    private sealed class CountingJsonlWriter(IImportJsonlWriter inner) : IImportJsonlWriter
    {
        public int CallCount { get; private set; }

        public async Task<ImportJsonlWriteResult> WriteAsync(
            ImportSession importSession,
            IReadOnlyCollection<ImportJsonlRecord> records,
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return await inner
                .WriteAsync(importSession, records, outputDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class FailingJsonlWriter : IImportJsonlWriter
    {
        public int CallCount { get; private set; }

        public Task<ImportJsonlWriteResult> WriteAsync(
            ImportSession importSession,
            IReadOnlyCollection<ImportJsonlRecord> records,
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ImportJsonlWriteResult(
                false,
                null,
                null,
                0,
                null,
                null,
                null,
                new ImportDiagnostic(
                    "injected_jsonl_failure",
                    "Injected JSONL writer failure.",
                    ImportDiagnosticSeverity.Error,
                    "jsonl")));
        }
    }
}
