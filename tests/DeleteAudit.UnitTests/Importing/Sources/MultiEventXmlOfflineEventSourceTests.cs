using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Importing.Sources;
using DeleteAudit.Infrastructure.Parsing;

namespace DeleteAudit.UnitTests.Importing.Sources;

public sealed class MultiEventXmlOfflineEventSourceTests
{
    [Fact]
    public async Task ReadAsyncReadsSingleXmlRecord()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var rawXml = OfflineSourceTestSupport.SysmonDeleteXml(
            1,
            new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
            @"C:\Lab\one.txt");
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "single.xml",
            OfflineSourceTestSupport.Envelope(rawXml));
        var source = new MultiEventXmlOfflineEventSource();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));

        Assert.False(result.IsFatal);
        var record = Assert.Single(result.Records);
        Assert.Equal(1, record.RecordNumber);
        Assert.Equal(OfflineRecordState.Available, record.State);
        Assert.Equal(rawXml, record.RawXml);
        Assert.Empty(record.Diagnostics);
        Assert.Equal(Path.GetFullPath(inputPath), result.InputFile!.NormalizedAbsolutePath);
    }

    [Fact]
    public async Task ReadAsyncPreservesPhysicalOrderWhenEventTimesAreOutOfOrder()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var later = OfflineSourceTestSupport.SysmonDeleteXml(
            20,
            new DateTimeOffset(2026, 2, 3, 4, 5, 20, TimeSpan.Zero),
            @"C:\Lab\later.txt");
        var earlier = OfflineSourceTestSupport.SysmonDeleteXml(
            10,
            new DateTimeOffset(2026, 2, 3, 4, 5, 10, TimeSpan.Zero),
            @"C:\Lab\earlier.txt");
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "out-of-order.xml",
            OfflineSourceTestSupport.Envelope(later, earlier));
        var source = new MultiEventXmlOfflineEventSource();
        var parser = new WindowsEventXmlParser();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));
        var parsedTimes = result.Records
            .Select(record => parser.Parse(record.RawXml!).RawEvent!.EventTimeUtc)
            .ToArray();

        Assert.False(result.IsFatal);
        Assert.Equal(
            new long[] { 1, 2 },
            result.Records.Select(record => record.RecordNumber).ToArray());
        Assert.True(parsedTimes[0] > parsedTimes[1]);
        Assert.Equal(later, result.Records[0].RawXml);
        Assert.Equal(earlier, result.Records[1].RawXml);
    }

    [Fact]
    public async Task ReadAsyncLeavesMalformedInnerRecordForParserAndContinues()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var first = OfflineSourceTestSupport.SysmonDeleteXml(
            1,
            new DateTimeOffset(2026, 2, 3, 4, 5, 1, TimeSpan.Zero),
            @"C:\Lab\first.txt");
        const string malformed = "<Event><System><EventID>26</Event>";
        var third = OfflineSourceTestSupport.SysmonDeleteXml(
            3,
            new DateTimeOffset(2026, 2, 3, 4, 5, 3, TimeSpan.Zero),
            @"C:\Lab\third.txt");
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "partial-malformed.xml",
            OfflineSourceTestSupport.Envelope(first, malformed, third));
        var source = new MultiEventXmlOfflineEventSource();
        var parser = new WindowsEventXmlParser();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));
        var parsed = result.Records
            .Select(record => parser.Parse(record.RawXml!))
            .ToArray();

        Assert.False(result.IsFatal);
        Assert.Equal(3, result.Records.Count);
        Assert.True(parsed[0].IsSuccess);
        Assert.False(parsed[1].IsSuccess);
        Assert.Equal(ParseErrorCode.MalformedXml, parsed[1].Error!.Code);
        Assert.True(parsed[2].IsSuccess);
        Assert.Equal(@"C:\Lab\third.txt", parsed[2].DeleteEvent!.FullPath);
    }

    [Fact]
    public async Task ReadAsyncRejectsMalformedOuterEnvelope()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "invalid-envelope.xml",
            """
            <DeleteAuditOfflineEvents xmlns="urn:deleteaudit:offline:v1" formatVersion="1">
              <Record><![CDATA[<Event />]]></Record>
            """);
        var source = new MultiEventXmlOfflineEventSource();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));

        Assert.True(result.IsFatal);
        Assert.Empty(result.Records);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "invalid_xml_envelope");
    }

    [Fact]
    public async Task ReadAsyncRejectsEmptyFile()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "empty.xml",
            string.Empty);
        var source = new MultiEventXmlOfflineEventSource();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));

        Assert.True(result.IsFatal);
        Assert.Empty(result.Records);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "empty_input_file");
    }

    [Fact]
    public async Task ReadAsyncRejectsFileOverConfiguredSize()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "too-large.xml",
            OfflineSourceTestSupport.Envelope("<Event />"));
        var source = new MultiEventXmlOfflineEventSource();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory, maximumFileSizeBytes: 1));

        Assert.True(result.IsFatal);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "input_file_too_large");
    }

    [Fact]
    public async Task ReadAsyncRejectsUnsupportedExtension()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "events.txt",
            OfflineSourceTestSupport.Envelope("<Event />"));
        var source = new MultiEventXmlOfflineEventSource();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));

        Assert.True(result.IsFatal);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "unsupported_file_extension");
    }

    [Fact]
    public async Task ReadAsyncDoesNotChangeInputBytesOrLastWriteTime()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var rawXml = OfflineSourceTestSupport.SysmonDeleteXml(
            1,
            new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
            @"C:\Lab\unchanged.txt");
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "unchanged.xml",
            OfflineSourceTestSupport.Envelope(rawXml));
        File.SetLastWriteTimeUtc(
            inputPath,
            new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var bytesBefore = await File.ReadAllBytesAsync(inputPath);
        var lastWriteBefore = File.GetLastWriteTimeUtc(inputPath);
        var source = new MultiEventXmlOfflineEventSource();

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));
        var bytesAfter = await File.ReadAllBytesAsync(inputPath);
        var lastWriteAfter = File.GetLastWriteTimeUtc(inputPath);

        Assert.False(result.IsFatal);
        Assert.Equal(bytesBefore, bytesAfter);
        Assert.Equal(lastWriteBefore, lastWriteAfter);
    }
}
