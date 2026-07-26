using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Correlation;

namespace DeleteAudit.UnitTests.Correlation;

public sealed class DeleteEventCorrelatorTests
{
    private static readonly DateTimeOffset OccurredUtc =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly DeleteEventCorrelator _correlator =
        new(new CorrelationOptions(TimeSpan.FromSeconds(3)));

    [Fact]
    public void CorrelateProcessGuidExactMatchEnrichesProcessContext()
    {
        var deleteEvent = TestSupport.DeleteEvent(1, OccurredUtc);
        var context = TestSupport.ProcessEvent("process-1", OccurredUtc - TimeSpan.FromMinutes(5));

        var result = _correlator.Correlate(deleteEvent, [context]);

        Assert.Equal(CorrelationMethod.ProcessGuid, result.Method);
        Assert.Equal(CorrelationConfidence.High, result.Confidence);
        Assert.True(result.IdentityFieldsEnriched);
        Assert.Equal("cleaner.exe --fixture", result.Event.CommandLine);
        Assert.Equal(2000, result.Event.ParentProcessId);
        Assert.Equal("process-1", result.MatchedProcessRawEventId);
    }

    [Fact]
    public void CorrelatePidReusePreventsOlderContextFromBeingAttributed()
    {
        var deleteEvent = TestSupport.DeleteEvent(
            2,
            OccurredUtc,
            processGuid: null,
            userName: "LAB\\Analyst");
        var oldContext = TestSupport.ProcessEvent(
            "old",
            OccurredUtc - TimeSpan.FromSeconds(2),
            processGuid: null,
            userName: "LAB\\Analyst",
            commandLine: "old.exe");
        var reusedPid = TestSupport.ProcessEvent(
            "reused",
            OccurredUtc - TimeSpan.FromSeconds(1),
            processGuid: null,
            userName: "LAB\\Other",
            commandLine: "other.exe");

        var result = _correlator.Correlate(deleteEvent, [oldContext, reusedPid]);

        Assert.Equal(CorrelationMethod.PathAndTimeHeuristic, result.Method);
        Assert.Equal(CorrelationConfidence.Low, result.Confidence);
        Assert.False(result.IdentityFieldsEnriched);
        Assert.Null(result.Event.CommandLine);
    }

    [Fact]
    public void CorrelateNoMatchDoesNotInventMissingIdentity()
    {
        var deleteEvent = TestSupport.DeleteEvent(
            3,
            OccurredUtc,
            processGuid: null,
            processId: null,
            userName: null,
            fullPath: @"C:\Lab\unmatched.txt",
            processPath: null);

        var result = _correlator.Correlate(deleteEvent, []);

        Assert.Equal(CorrelationMethod.None, result.Method);
        Assert.Equal(CorrelationConfidence.None, result.Confidence);
        Assert.Null(result.Event.CommandLine);
        Assert.Null(result.Event.ParentProcessPath);
        Assert.Null(result.Event.UserSid);
        Assert.Contains("no_reliable_match", result.Reasons);
    }

    [Fact]
    public void CorrelatePathTimeHeuristicIsRecordedButDoesNotEnrich()
    {
        var deleteEvent = TestSupport.DeleteEvent(
            4,
            OccurredUtc,
            processGuid: null,
            processId: null,
            userName: null);
        var context = TestSupport.ProcessEvent(
            "heuristic",
            OccurredUtc - TimeSpan.FromSeconds(1),
            processId: 9000,
            processGuid: null);

        var result = _correlator.Correlate(deleteEvent, [context]);

        Assert.Equal(CorrelationMethod.PathAndTimeHeuristic, result.Method);
        Assert.Equal(CorrelationConfidence.Low, result.Confidence);
        Assert.False(result.IdentityFieldsEnriched);
        Assert.Null(result.Event.CommandLine);
    }
}
