using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Sessions;

namespace DeleteAudit.UnitTests.Sessions;

public sealed class DeleteSessionAggregatorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Theory]
    [InlineData(29, AuditRiskLevel.Informational)]
    [InlineData(30, AuditRiskLevel.Warning)]
    [InlineData(99, AuditRiskLevel.Warning)]
    [InlineData(100, AuditRiskLevel.Critical)]
    public void AddAppliesConfiguredCountBoundaries(int count, AuditRiskLevel expected)
    {
        var clock = new ManualTimeProvider(Start);
        var aggregator = new DeleteSessionAggregator(
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100),
            timeProvider: clock);
        SessionAggregationResult? result = null;

        for (var index = 0; index < count; index++)
        {
            result = aggregator.Add(
                TestSupport.DeleteEvent(index, Start + TimeSpan.FromMilliseconds(index)));
        }

        Assert.Equal(expected, result!.Assessment.RiskLevel);
        Assert.Equal(count, result.Session.ConfirmedItemCount);
        Assert.Equal(Start, result.Assessment.AssessedUtc);
    }

    [Fact]
    public void AddProtectedPathIsImmediatelyCritical()
    {
        var aggregator = new DeleteSessionAggregator(
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100),
            [new ProtectedPathRule("protected", @"C:\Lab\Protected")],
            new ManualTimeProvider(Start));
        var deleteEvent = TestSupport.DeleteEvent(
            1,
            Start,
            fullPath: @"C:\Lab\Protected\first.txt");

        var result = aggregator.Add(deleteEvent);

        Assert.Equal(AuditRiskLevel.Critical, result.Assessment.RiskLevel);
        Assert.Equal("protected_root", result.Assessment.RuleCode);
        Assert.Equal(1, result.Session.ProtectedItemCount);
    }

    [Fact]
    public void ParseOptionsReadsThresholdsFromConfiguration()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "appsettings.example.json"));

        var options = RiskOptionsJsonParser.Parse(json);

        Assert.Equal(TimeSpan.FromSeconds(10), options.SessionWindow);
        Assert.Equal(30, options.WarningCount);
        Assert.Equal(100, options.CriticalCount);
    }

    [Fact]
    public void AddDuplicateEventDoesNotIncreaseSessionCount()
    {
        var aggregator = new DeleteSessionAggregator(
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100));
        var deleteEvent = TestSupport.DeleteEvent(1, Start);

        var first = aggregator.Add(deleteEvent);
        var second = aggregator.Add(deleteEvent);

        Assert.True(first.EventAdded);
        Assert.False(second.EventAdded);
        Assert.Equal(1, second.Session.ConfirmedItemCount);
    }
}
