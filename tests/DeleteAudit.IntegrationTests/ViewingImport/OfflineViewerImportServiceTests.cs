using System.Text;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Importing.Output;
using DeleteAudit.Infrastructure.Importing.Sources;
using DeleteAudit.Infrastructure.Viewing;
using DeleteAudit.Infrastructure.ViewingImport;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.ViewingImport;

[CollectionDefinition("Phase 1C viewer import", DisableParallelization = true)]
public sealed class Phase1CViewerImportGroup
{
    public const string Name = "Phase 1C viewer import";
}

[Collection(Phase1CViewerImportGroup.Name)]
public sealed class OfflineViewerImportServiceTests
{
    private static readonly string ViewerDataRoot = ViewerDataLocation.DefaultRoot;
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 23, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task XmlImportUsesExistingSchemaAndLeavesInputUnchanged()
    {
        var scenario = await CreateScenarioAsync("xml-complete");
        var inputPath = await WriteXmlEnvelopeAsync(
            scenario.Root,
            SysmonDelete(3101, @"C:\Work\viewer-complete.txt"));
        var bytesBefore = await File.ReadAllBytesAsync(inputPath);
        var lastWriteBefore = File.GetLastWriteTimeUtc(inputPath);

        var result = await scenario.Service.ImportAsync(inputPath);

        Assert.Equal(ImportStatus.Completed, result.Status);
        Assert.True(result.DatabaseCommitted);
        Assert.Equal(1, result.Report.DeleteFactCount);
        Assert.StartsWith(
            scenario.Location.JsonlOutputDirectory,
            Assert.IsType<string>(result.JsonlFilePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            scenario.Location.JsonlOutputDirectory,
            Assert.IsType<string>(result.ManifestFilePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(inputPath));
        Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(inputPath));
    }

    [Fact]
    public async Task InjectedEvtxReaderUsesOnlySelectedFileAndLeavesItUnchanged()
    {
        var reader = new RecordingEvtxReader(
            SysmonDelete(3201, @"C:\Work\viewer-evtx.txt"));
        var sources = new IOfflineEventSource[]
        {
            new MultiEventXmlOfflineEventSource(),
            new EvtxOfflineEventSource(reader)
        };
        var scenario = await CreateScenarioAsync("evtx-injected", sources);
        var inputPath = await WriteNewFileAsync(
            scenario.Root,
            "selected.evtx",
            "offline fixture bytes");
        var bytesBefore = await File.ReadAllBytesAsync(inputPath);
        var lastWriteBefore = File.GetLastWriteTimeUtc(inputPath);

        var result = await scenario.Service.ImportAsync(inputPath);

        Assert.Equal(ImportStatus.Completed, result.Status);
        Assert.True(result.DatabaseCommitted);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal(Path.GetFullPath(inputPath), reader.LastPath);
        Assert.Equal(1, result.Report.EventIdCounts[26]);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(inputPath));
        Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(inputPath));
    }

    [Fact]
    public async Task RepeatedContentReturnsAlreadyImportedWithoutSecondOutput()
    {
        var scenario = await CreateScenarioAsync("already-imported");
        var inputPath = await WriteXmlEnvelopeAsync(
            scenario.Root,
            SysmonDelete(3301, @"C:\Work\viewer-duplicate.txt"));

        var first = await scenario.Service.ImportAsync(inputPath);
        var second = await scenario.Service.ImportAsync(inputPath);

        Assert.Equal(ImportStatus.Completed, first.Status);
        Assert.Equal(ImportStatus.AlreadyImported, second.Status);
        Assert.True(second.DatabaseCommitted);
        Assert.Null(second.JsonlFilePath);
        Assert.Null(second.ManifestFilePath);
        Assert.Contains(
            second.Report.Diagnostics,
            diagnostic => diagnostic.Code == "already_imported");
        Assert.Equal(
            1,
            await CountImportSessionsAsync(scenario.Location.DatabasePath));
    }

    [Fact]
    public async Task MixedValidAndMalformedRecordsReturnPartialFailure()
    {
        var scenario = await CreateScenarioAsync("partial");
        var inputPath = await WriteXmlEnvelopeAsync(
            scenario.Root,
            SysmonDelete(3401, @"C:\Work\viewer-partial.txt"),
            "<Event");

        var result = await scenario.Service.ImportAsync(inputPath);

        Assert.Equal(ImportStatus.PartialFailure, result.Status);
        Assert.True(result.DatabaseCommitted);
        Assert.Equal(1, result.Report.ParsedSuccessCount);
        Assert.Equal(1, result.Report.ParsedFailureCount);
        Assert.Contains(
            result.Report.Diagnostics,
            diagnostic => diagnostic.Code == "parse_malformedxml");
    }

    [Fact]
    public async Task AllMalformedRecordsReturnFailedWithCommittedJsonl()
    {
        var scenario = await CreateScenarioAsync("all-malformed");
        var inputPath = await WriteXmlEnvelopeAsync(scenario.Root, "<Event");

        var result = await scenario.Service.ImportAsync(inputPath);

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.True(result.DatabaseCommitted);
        Assert.Equal(0, result.Report.ParsedSuccessCount);
        Assert.Equal(1, result.Report.ParsedFailureCount);
        Assert.True(File.Exists(Assert.IsType<string>(result.JsonlFilePath)));
        Assert.True(File.Exists(Assert.IsType<string>(result.ManifestFilePath)));
        Assert.Equal(
            1,
            await CountImportSessionsAsync(scenario.Location.DatabasePath));
    }

    [Fact]
    public async Task MissingDatabaseReturnsStructuredFailureWithoutCreatingIt()
    {
        var scenarioId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(ViewerDataRoot, $"missing-{scenarioId}");
        var databasePath = Path.Combine(root, "missing.db");
        var location = ViewerDataLocation.CreateForTesting(
            databasePath,
            Path.Combine(root, "jsonl"));
        var service = new OfflineViewerImportService(location);
        var inputPath = Path.Combine(root, "selected.xml");

        var result = await service.ImportAsync(inputPath);

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.False(result.DatabaseCommitted);
        Assert.False(File.Exists(databasePath));
        Assert.Contains(
            result.Report.Diagnostics,
            diagnostic => diagnostic.Code == "viewer_database_unavailable");
    }

    [Fact]
    public async Task ProhibitedVolumeIsRejectedBeforeInjectedSourceRuns()
    {
        var source = new CountingSource();
        var scenarioId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(ViewerDataRoot, $"prohibited-{scenarioId}");
        var location = ViewerDataLocation.CreateForTesting(
            Path.Combine(root, "missing.db"),
            Path.Combine(root, "jsonl"));
        var service = CreateService(location, [source]);
        var prohibitedPath = string.Concat(
            'D',
            Path.VolumeSeparatorChar,
            Path.DirectorySeparatorChar,
            "selected.xml");

        var result = await service.ImportAsync(prohibitedPath);

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Equal(0, source.CallCount);
        Assert.Contains(
            result.Report.Diagnostics,
            diagnostic => diagnostic.Code == "prohibited_input_volume");
    }

    private static async Task<ViewerImportScenario> CreateScenarioAsync(
        string name,
        IReadOnlyList<IOfflineEventSource>? sources = null)
    {
        var scenarioId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(ViewerDataRoot, $"{name}-{scenarioId}");
        Directory.CreateDirectory(root);
        var location = ViewerDataLocation.CreateForTesting(
            Path.Combine(root, "deleteaudit.db"),
            Path.Combine(root, "jsonl"));
        await CreateSchemaAsync(location.DatabasePath);
        return new ViewerImportScenario(
            root,
            location,
            CreateService(location, sources));
    }

    private static OfflineViewerImportService CreateService(
        ViewerDataLocation location,
        IReadOnlyList<IOfflineEventSource>? sources) =>
        new(
            location,
            new OfflineImportOptions(
                4 * 1024 * 1024,
                location.JsonlOutputDirectory,
                2),
            "1.2.0-integration",
            new CorrelationOptions(TimeSpan.FromSeconds(3)),
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100),
            sources: sources,
            jsonlWriter: new FileImportJsonlWriter(),
            timeProvider: new FixedTimeProvider(FixedUtc));

    private static async Task CreateSchemaAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema.sql"));
        var migration = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "0002_phase_1b_offline_import.sql"));
        using var command = connection.CreateCommand();
        command.CommandText = $"{schema}{Environment.NewLine}{migration}";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountImportSessionsAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM import_sessions;";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string> WriteXmlEnvelopeAsync(
        string root,
        params string[] records)
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <DeleteAuditOfflineEvents xmlns="urn:deleteaudit:offline:v1" formatVersion="1">
            """);
        foreach (var record in records)
        {
            builder.Append("<Record><![CDATA[");
            builder.Append(record);
            builder.Append("]]></Record>");
        }

        builder.Append("</DeleteAuditOfflineEvents>");
        return await WriteNewFileAsync(
            root,
            $"selected-{Guid.NewGuid():N}.xml",
            builder.ToString());
    }

    private static async Task<string> WriteNewFileAsync(
        string root,
        string fileName,
        string content)
    {
        var path = Path.Combine(root, fileName);
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
        return path;
    }

    private static string SysmonDelete(long recordId, string targetPath) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Sysmon" />
             <EventID>26</EventID>
             <TimeCreated SystemTime="2026-07-23T18:00:01.0000000Z" />
             <EventRecordID>{recordId}</EventRecordID>
             <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="TargetFilename">{targetPath}</Data>
             <Data Name="User">Alice</Data>
             <Data Name="Image">C:\Tools\cleanup.exe</Data>
             <Data Name="ProcessGuid">11111111-2222-3333-4444-555555555555</Data>
             <Data Name="UtcTime">2026-07-23 18:00:01.000</Data>
             <Data Name="ProcessId">4242</Data>
           </EventData>
         </Event>
         """;

    private sealed record ViewerImportScenario(
        string Root,
        ViewerDataLocation Location,
        OfflineViewerImportService Service);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingEvtxReader(string rawXml) : IEvtxRecordXmlReader
    {
        public int CallCount { get; private set; }

        public string? LastPath { get; private set; }

        public IReadOnlyList<EvtxRecordXmlReadResult> ReadRecords(
            string normalizedAbsolutePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastPath = normalizedAbsolutePath;
            return [new EvtxRecordXmlReadResult(1, rawXml, null, false)];
        }
    }

    private sealed class CountingSource : IOfflineEventSource
    {
        public int CallCount { get; private set; }

        public string SupportedFileExtension => ".xml";

        public Task<OfflineEventSourceResult> ReadAsync(
            ImportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException(
                "The prohibited path must be rejected before the source runs.");
        }
    }
}
