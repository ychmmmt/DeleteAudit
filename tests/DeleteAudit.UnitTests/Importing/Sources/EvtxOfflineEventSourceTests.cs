using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using DeleteAudit.Infrastructure.Importing.Sources;

namespace DeleteAudit.UnitTests.Importing.Sources;

[SupportedOSPlatform("windows")]
public sealed class EvtxOfflineEventSourceTests
{
    [Fact]
    public async Task ReadAsyncUsesOnlyNormalizedFilePathMode()
    {
        var directory = OfflineSourceTestSupport.CreateUniqueDirectory();
        var inputPath = await OfflineSourceTestSupport.WriteTextFileAsync(
            directory,
            "offline.evtx",
            "synthetic EVTX bytes for a fake reader");
        var fakeReader = new CapturingEvtxRecordXmlReader();
        var source = new EvtxOfflineEventSource(recordReader: fakeReader);

        var result = await source.ReadAsync(
            OfflineSourceTestSupport.Request(inputPath, directory));

        Assert.False(result.IsFatal);
        Assert.Equal(Path.GetFullPath(inputPath), Assert.Single(fakeReader.Paths));
        Assert.Equal(PathType.FilePath, WindowsEvtxRecordXmlReader.QueryPathType);
        var record = Assert.Single(result.Records);
        Assert.Equal(1, record.RecordNumber);
        Assert.NotNull(record.RawXml);
    }

    private sealed class CapturingEvtxRecordXmlReader : IEvtxRecordXmlReader
    {
        public List<string> Paths { get; } = [];

        public IReadOnlyList<EvtxRecordXmlReadResult> ReadRecords(
            string normalizedAbsolutePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(normalizedAbsolutePath);
            return
            [
                new EvtxRecordXmlReadResult(
                    1,
                    OfflineSourceTestSupport.SysmonDeleteXml(
                        1,
                        new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
                        @"C:\Lab\evtx.txt"),
                    null,
                    false)
            ];
        }
    }
}
