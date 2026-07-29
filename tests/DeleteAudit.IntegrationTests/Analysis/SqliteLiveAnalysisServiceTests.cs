using System.Security.Cryptography;
using System.Text;
using DeleteAudit.Application.Analysis;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Analysis;
using DeleteAudit.Infrastructure.LiveMonitoring;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.Analysis;

/// <summary>
/// The analysis reuses the offline parser, correlator, aggregator and risk rules. These
/// tests pin that reuse against a real temporary SQLite database holding synthetic
/// evidence; no real Windows event log is involved.
/// </summary>
public sealed class SqliteLiveAnalysisServiceTests
{
    private static readonly DateTimeOffset StartedUtc =
        new(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AProcessGuidMatchIsReportedAsHighConfidenceAndPointsAtItsEvidence()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync(
        [
            Record(session, 1, ProcessCreateXml(), LiveEventOutcome.ProcessContext),
            Record(session, 2, DeleteXml(1), LiveEventOutcome.DeleteFact)
        ]);

        var analysis = await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        var delete = Assert.Single(analysis.Deletes);
        Assert.Equal(CorrelationMethod.ProcessGuid, delete.Method);
        Assert.Equal(CorrelationConfidence.High, delete.Confidence);
        Assert.True(delete.IsCorrelated);
        Assert.False(delete.IsHeuristicOnly);
        // Provenance: the match points back at the stored record it came from.
        Assert.Equal(
            LiveEvidenceIdentity.Create(session, 1),
            delete.MatchedProcessLiveEvidenceId);
        Assert.Equal(LiveEvidenceIdentity.Create(session, 2), delete.LiveEvidenceId);
        Assert.Equal(1, analysis.ProcessContextCount);
        Assert.Equal(1, analysis.DeleteFactCount);
        Assert.Equal(0, analysis.UncorrelatedDeleteCount);
    }

    [Fact]
    public async Task ADeleteWithNoCorroborationIsKeptAndCountedAsUncorrelated()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync(
            [Record(session, 1, DeleteXml(1), LiveEventOutcome.DeleteFact)]);

        var analysis = await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        var delete = Assert.Single(analysis.Deletes);
        // Retained rather than discarded: an unattributed delete is still a delete.
        Assert.Equal(CorrelationMethod.None, delete.Method);
        Assert.Equal(CorrelationConfidence.None, delete.Confidence);
        Assert.Null(delete.MatchedProcessLiveEvidenceId);
        Assert.Equal(1, analysis.UncorrelatedDeleteCount);
        Assert.Contains("no_reliable_match", delete.Reasons);
    }

    [Fact]
    public async Task DeletesByOneProcessInOnePathAreGroupedIntoASession()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync(
        [
            Record(session, 1, ProcessCreateXml(), LiveEventOutcome.ProcessContext),
            Record(session, 2, DeleteXml(1), LiveEventOutcome.DeleteFact),
            Record(session, 3, DeleteXml(2), LiveEventOutcome.DeleteFact),
            Record(session, 4, DeleteXml(3), LiveEventOutcome.DeleteFact)
        ]);

        var analysis = await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        var deleteSession = Assert.Single(analysis.DeleteSessions);
        Assert.Equal(1, deleteSession.Ordinal);
        Assert.Equal(3, deleteSession.ConfirmedItemCount);
        Assert.All(analysis.Deletes, item => Assert.Equal(1, item.DeleteSessionOrdinal));
        // Three ordinary deletes stay informational under the existing thresholds.
        Assert.Equal(AuditRiskLevel.Informational, deleteSession.RiskLevel);
        Assert.Equal("single_delete", deleteSession.RiskRuleCode);
    }

    [Fact]
    public async Task RiskRisesToWarningOnlyWhenTheExistingThresholdIsReached()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        var records = new List<LiveCaptureRecord>
        {
            Record(session, 1, ProcessCreateXml(), LiveEventOutcome.ProcessContext)
        };
        // 30 is the configured WarningCount.
        for (var index = 1; index <= 30; index++)
        {
            records.Add(Record(
                session,
                index + 1,
                DeleteXml(index),
                LiveEventOutcome.DeleteFact));
        }

        await repository.AppendRecordsAsync(records.Take(31).ToArray());

        var analysis = await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        var deleteSession = Assert.Single(analysis.DeleteSessions);
        Assert.Equal(30, deleteSession.ConfirmedItemCount);
        Assert.Equal(AuditRiskLevel.Warning, deleteSession.RiskLevel);
        Assert.Equal("burst_30_in_window", deleteSession.RiskRuleCode);
        // Risk never goes backwards within a session.
        Assert.Equal(
            AuditRiskLevel.Warning,
            analysis.Deletes[^1].RiskLevel);
    }

    [Fact]
    public async Task ARecordThatNoLongerParsesIsCountedNotGuessedAt()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync(
        [
            Record(session, 1, "<Event", LiveEventOutcome.DeleteFact),
            Record(session, 2, DeleteXml(1), LiveEventOutcome.DeleteFact)
        ]);

        var analysis = await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        Assert.Equal(1, analysis.UnparsableRecordCount);
        Assert.Single(analysis.Deletes);
        Assert.Equal(LiveEvidenceIdentity.Create(session, 2), analysis.Deletes[0].LiveEvidenceId);
    }

    [Fact]
    public async Task AnalysingTheSameEvidenceTwiceProducesTheSameResult()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync(
        [
            Record(session, 1, ProcessCreateXml(), LiveEventOutcome.ProcessContext),
            Record(session, 2, DeleteXml(1), LiveEventOutcome.DeleteFact),
            Record(session, 3, DeleteXml(2), LiveEventOutcome.DeleteFact)
        ]);
        var service = new SqliteLiveAnalysisService(location);

        var first = await service.AnalyzeAsync(session);
        var second = await service.AnalyzeAsync(session);

        // Delete event ids are content derived, so a replay reproduces them exactly.
        Assert.Equal(
            first.Deletes.Select(item => item.DeleteEventId),
            second.Deletes.Select(item => item.DeleteEventId));
        Assert.Equal(
            first.Deletes.Select(item => item.DeleteSessionOrdinal),
            second.Deletes.Select(item => item.DeleteSessionOrdinal));
        Assert.Equal(
            first.DeleteSessions.Select(item => item.ConfirmedItemCount),
            second.DeleteSessions.Select(item => item.ConfirmedItemCount));
        Assert.Equal(first.DeleteFactCount, second.DeleteFactCount);
    }

    [Fact]
    public async Task AnalysisWritesNothingAtAll()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        await repository.AppendRecordsAsync(
        [
            Record(session, 1, ProcessCreateXml(), LiveEventOutcome.ProcessContext),
            Record(session, 2, DeleteXml(1), LiveEventOutcome.DeleteFact)
        ]);
        var before = await File.ReadAllBytesAsync(location.DatabasePath);

        await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        Assert.Equal(before, await File.ReadAllBytesAsync(location.DatabasePath));
        // Derived analysis never becomes evidence.
        Assert.Equal(0, await CountAsync(location, "delete_events"));
        Assert.Equal(0, await CountAsync(location, "raw_events"));
        Assert.Equal(0, await CountAsync(location, "delete_sessions"));
    }

    [Fact]
    public async Task AnEmptySessionAnalysesToAnEmptyResult()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);

        var analysis = await new SqliteLiveAnalysisService(location).AnalyzeAsync(session);

        Assert.Equal(0, analysis.AnalyzedRecordCount);
        Assert.False(analysis.HasDeletes);
        Assert.False(analysis.WasTruncated);
        Assert.Empty(analysis.DeleteSessions);
    }

    [Fact]
    public async Task ACancelledAnalysisDoesNotReturnAResult()
    {
        var location = await CreateDatabaseAsync();
        var repository = new SqliteLiveMonitoringRepository(location);
        var session = await StartCaptureAsync(repository);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SqliteLiveAnalysisService(location)
                .AnalyzeAsync(session, cancellation.Token));
    }

    private static async Task<long> CountAsync(ViewerDataLocation location, string table)
    {
        await using var connection = location.CreateReadOnlyConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        // The table name is a constant from this test, never user input.
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static string DeleteXml(int index) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Sysmon" />
             <EventID>26</EventID>
             <TimeCreated SystemTime="2026-07-25T09:00:01.0000000Z" />
             <EventRecordID>{100 + index}</EventRecordID>
             <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
             <Computer>LAB-PC</Computer>
           </System>
           <EventData>
             <Data Name="TargetFilename">C:\Work\item-{index}.txt</Data>
             <Data Name="Image">C:\Tools\cleanup.exe</Data>
             <Data Name="ProcessGuid">11111111-2222-3333-4444-555555555555</Data>
             <Data Name="UtcTime">2026-07-25 09:00:01.000</Data>
             <Data Name="ProcessId">4242</Data>
             <Data Name="User">LAB\Analyst</Data>
           </EventData>
         </Event>
         """;

    private static string ProcessCreateXml() =>
        """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Sysmon" />
            <EventID>1</EventID>
            <TimeCreated SystemTime="2026-07-25T09:00:00.0000000Z" />
            <EventRecordID>7</EventRecordID>
            <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
            <Computer>LAB-PC</Computer>
          </System>
          <EventData>
            <Data Name="Image">C:\Tools\cleanup.exe</Data>
            <Data Name="ProcessGuid">11111111-2222-3333-4444-555555555555</Data>
            <Data Name="CommandLine">cleanup.exe --all</Data>
            <Data Name="UtcTime">2026-07-25 09:00:00.000</Data>
            <Data Name="ProcessId">4242</Data>
            <Data Name="User">LAB\Analyst</Data>
          </EventData>
        </Event>
        """;

    private static LiveCaptureRecord Record(
        string sessionId,
        long sequence,
        string rawXml,
        LiveEventOutcome outcome) =>
        new(
            LiveEvidenceIdentity.Create(sessionId, sequence),
            sessionId,
            sequence,
            100 + sequence,
            LiveMonitoringChannels.SysmonProvider,
            LiveMonitoringChannels.SysmonOperational,
            "LAB-PC",
            StartedUtc,
            StartedUtc,
            rawXml,
            SHA256.HashData(Encoding.UTF8.GetBytes(rawXml)),
            null,
            outcome == LiveEventOutcome.ProcessContext ? 1 : 26,
            outcome,
            null,
            null);

    private static async Task<string> StartCaptureAsync(
        SqliteLiveMonitoringRepository repository)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        await repository.StartCaptureSessionAsync(
            new LiveCaptureSessionStart(sessionId, StartedUtc, 2048, "analysis-tests"));
        return sessionId;
    }

    private static async Task<ViewerDataLocation> CreateDatabaseAsync()
    {
        var directory = Path.Combine(
            ViewerDataLocation.DefaultRoot,
            "tests",
            $"analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var location = ViewerDataLocation.CreateForTesting(
            Path.Combine(directory, "viewer.db"),
            Path.Combine(directory, "jsonl"));

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = location.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var scripts = new[]
        {
            await File.ReadAllTextAsync(Path.Combine(fixtures, "schema.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0002_phase_1b_offline_import.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0003_phase_2a_live_monitoring.sql")),
            await File.ReadAllTextAsync(
                Path.Combine(fixtures, "0004_phase_2b_live_evidence.sql"))
        };
        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, scripts);
        await command.ExecuteNonQueryAsync();
        return location;
    }
}
