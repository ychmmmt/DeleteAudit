using DeleteAudit.Domain;

namespace DeleteAudit.UnitTests.Projection;

public sealed class LiveProjectionIdentityTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProjectionIdentityIsDeterministicAndLiveOwned()
    {
        var first = LiveProjectionIdentity.CreateProjectionId("session-1:42");
        var replay = LiveProjectionIdentity.CreateProjectionId("session-1:42");

        Assert.Equal("live-projection:session-1:42", first);
        Assert.Equal(first, replay);
        Assert.DoesNotContain("import", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpochIdentityDistinguishesMissingAndEmptyMachine()
    {
        var missing = LiveProjectionIdentity.CreateEpochId(
            "session-1",
            "Security",
            null);
        var empty = LiveProjectionIdentity.CreateEpochId(
            "session-1",
            "Security",
            string.Empty);

        Assert.NotEqual(missing, empty);
        Assert.StartsWith("live-epoch:", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadHashIsStableAcrossMissingFieldOrder()
    {
        var first = Payload(["processGuid", "userSid"]);
        var reordered = Payload(["userSid", "processGuid"]);

        Assert.Equal(
            Convert.ToHexString(LiveProjectionIdentity.ComputePayloadHash(first)),
            Convert.ToHexString(LiveProjectionIdentity.ComputePayloadHash(reordered)));
    }

    [Fact]
    public void PayloadHashDistinguishesNullFromEmpty()
    {
        var missing = Payload([]);
        var empty = Payload([]) with { ProcessGuid = string.Empty };

        Assert.NotEqual(
            Convert.ToHexString(LiveProjectionIdentity.ComputePayloadHash(missing)),
            Convert.ToHexString(LiveProjectionIdentity.ComputePayloadHash(empty)));
    }

    [Fact]
    public void EntryHashCoversPayloadDigestAndPreviousEntry()
    {
        var rawDigest = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var firstPayload = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var changedPayload = Enumerable.Repeat((byte)0x23, 32).ToArray();
        var first = LiveProjectionIdentity.ComputeEntryHash(
            LiveProjectionIdentity.ChainAnchor,
            "session-1:1",
            "live-epoch:fixture",
            1,
            rawDigest,
            firstPayload);
        var payloadChanged = LiveProjectionIdentity.ComputeEntryHash(
            LiveProjectionIdentity.ChainAnchor,
            "session-1:1",
            "live-epoch:fixture",
            1,
            rawDigest,
            changedPayload);
        var second = LiveProjectionIdentity.ComputeEntryHash(
            first,
            "session-1:2",
            "live-epoch:fixture",
            2,
            rawDigest,
            firstPayload);

        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(payloadChanged));
        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(second));
    }

    [Theory]
    [InlineData(LiveEventOutcome.DeleteFact, "sysmon_delete", true)]
    [InlineData(LiveEventOutcome.ProcessContext, "sysmon_process", true)]
    [InlineData(LiveEventOutcome.SecurityEvidence, "security_4663", true)]
    [InlineData(LiveEventOutcome.Ignored, "", false)]
    [InlineData(LiveEventOutcome.Error, "", false)]
    public void OnlyClassifiedEvidenceMapsToProjectionSource(
        LiveEventOutcome outcome,
        string expectedSource,
        bool expectedResult)
    {
        var mapped = LiveProjectionIdentity.TryGetSource(outcome, out var source);

        Assert.Equal(expectedResult, mapped);
        Assert.Equal(expectedSource, source);
    }

    private static LiveProjectionPayload Payload(IReadOnlyList<string> missingFields) =>
        new(
            "sysmon_delete",
            42,
            "Microsoft-Windows-Sysmon",
            LiveMonitoringChannels.SysmonOperational,
            "LAB-PC",
            ObservedUtc.AddSeconds(-1),
            ObservedUtc,
            "raw-1",
            26,
            @"C:\Fixture\item.txt",
            "unknown",
            123,
            @"C:\Fixture\deleter.exe",
            null,
            null,
            null,
            null,
            null,
            "LAB\\analyst",
            null,
            "not_observed",
            false,
            missingFields);
}
