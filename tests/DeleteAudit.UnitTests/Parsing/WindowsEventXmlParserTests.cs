using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Parsing;

namespace DeleteAudit.UnitTests.Parsing;

public sealed class WindowsEventXmlParserTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 1, 2, 3, 5, 0, TimeSpan.Zero);
    private readonly WindowsEventXmlParser _parser = new(new ManualTimeProvider(ObservedUtc));

    [Theory]
    [InlineData("Sysmon23.xml", 23)]
    [InlineData("Sysmon26.xml", 26)]
    public void ParseSysmonDeleteReturnsDeleteFact(string fixture, int eventId)
    {
        var result = _parser.Parse(TestSupport.ReadFixture(fixture));

        Assert.True(result.IsSuccess);
        Assert.Equal(eventId, result.RawEvent!.EventId);
        Assert.Equal(eventId, result.DeleteEvent!.SourceEventId);
        Assert.Null(result.ProcessContext);
        Assert.Null(result.SecurityEvidence);
    }

    [Fact]
    public void ParseSysmonProcessReturnsContextOnly()
    {
        var result = _parser.Parse(TestSupport.ReadFixture("Sysmon1.xml"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ProcessContext);
        Assert.Null(result.DeleteEvent);
        Assert.Equal(
            "\"C:\\Lab\\Tools\\cleaner.exe\" --offline-fixture",
            result.ProcessContext!.CommandLine);
    }

    [Fact]
    public void ParseSecurity4663DeleteReturnsSupportingEvidenceOnly()
    {
        var result = _parser.Parse(TestSupport.ReadFixture("Security4663.xml"));

        Assert.True(result.IsSuccess);
        Assert.Null(result.DeleteEvent);
        Assert.NotNull(result.SecurityEvidence);
        Assert.Equal(DeletePermissionType.Delete, result.SecurityEvidence!.DeletePermission);
        Assert.Equal(4242, result.SecurityEvidence.ProcessId);
    }

    [Fact]
    public void ParseUsesDataNameRatherThanElementOrderAndPreservesUnknownFields()
    {
        var result = _parser.Parse(TestSupport.ReadFixture("Sysmon23.xml"));

        Assert.Equal(
            @"C:\Lab\A&B<final>.txt",
            result.DeleteEvent!.FullPath);
        Assert.Equal("preserved", result.RawEvent!.EventData["UnexpectedFutureField"]);
        Assert.True(result.DeleteEvent.ArchiveExpected);
    }

    [Fact]
    public void ParseMissingFieldsRemainNullAndAreReported()
    {
        var xml = EventXml(
            26,
            """
            <Data Name="UtcTime">2026-01-02 03:04:06.000</Data>
            <Data Name="TargetFilename">C:\Lab\missing-context.txt</Data>
            """);

        var result = _parser.Parse(xml);

        Assert.True(result.IsSuccess);
        Assert.Null(result.DeleteEvent!.ProcessGuid);
        Assert.Null(result.DeleteEvent.UserName);
        Assert.Contains("processGuid", result.DeleteEvent.MissingFields);
        Assert.Contains("userSid", result.DeleteEvent.MissingFields);
    }

    [Fact]
    public void ParseMalformedXmlReturnsStructuredError()
    {
        const string malformed = "<Event><System><EventID>23</Event>";

        var result = _parser.Parse(malformed);

        Assert.False(result.IsSuccess);
        Assert.Equal(ParseErrorCode.MalformedXml, result.Error!.Code);
        Assert.Equal(malformed, result.Error.RawXml);
    }

    [Fact]
    public void ParseXmlSpecialCharactersAreDecodedAsText()
    {
        var result = _parser.Parse(TestSupport.ReadFixture("Sysmon23.xml"));

        Assert.Equal(@"C:\Lab\A&B<final>.txt", result.DeleteEvent!.FullPath);
        Assert.Contains("A&amp;B&lt;final&gt;", result.RawEvent!.RawXml, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSecurity4663NonDeleteAccessDoesNotCreateEvidence()
    {
        var xml = EventXml(
            4663,
            """
            <Data Name="ObjectName">C:\Lab\read-only.txt</Data>
            <Data Name="ProcessId">0x1092</Data>
            <Data Name="AccessMask">0x1</Data>
            <Data Name="AccessList">%%4416</Data>
            """,
            "Microsoft-Windows-Security-Auditing",
            "Security");

        var result = _parser.Parse(xml);

        Assert.True(result.IsSuccess);
        Assert.Null(result.DeleteEvent);
        Assert.Null(result.SecurityEvidence);
    }

    [Fact]
    public void ParseSecurity4663DeleteChildMaskCreatesDeleteChildEvidence()
    {
        var xml = EventXml(
            4663,
            """
            <Data Name="ObjectName">C:\Lab\folder</Data>
            <Data Name="ProcessId">0x1092</Data>
            <Data Name="AccessMask">0x40</Data>
            """,
            "Microsoft-Windows-Security-Auditing",
            "Security");

        var result = _parser.Parse(xml);

        Assert.Equal(DeletePermissionType.DeleteChild, result.SecurityEvidence!.DeletePermission);
    }

    private static string EventXml(
        int eventId,
        string eventData,
        string provider = "Microsoft-Windows-Sysmon",
        string channel = "Microsoft-Windows-Sysmon/Operational") =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="{provider}" />
             <EventID>{eventId}</EventID>
             <EventRecordID>42</EventRecordID>
             <Channel>{channel}</Channel>
             <Computer>LAB-PC</Computer>
             <TimeCreated SystemTime="2026-01-02T03:04:06Z" />
           </System>
           <EventData>
             {eventData}
           </EventData>
         </Event>
         """;
}
