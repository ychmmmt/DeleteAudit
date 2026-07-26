using System.Text;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;

namespace DeleteAudit.UnitTests.Importing.Sources;

internal static class OfflineSourceTestSupport
{
    private static readonly string TestOutputRoot =
        Path.Combine(RepositoryRoot.ArtifactsDirectory, "test-output");

    public static string CreateUniqueDirectory()
    {
        var path = Path.Combine(
            TestOutputRoot,
            $"offline-source-{Guid.NewGuid():D}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task<string> WriteTextFileAsync(
        string directory,
        string fileName,
        string content)
    {
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        return path;
    }

    public static ImportRequest Request(
        string inputPath,
        string outputDirectory,
        long maximumFileSizeBytes = 1024 * 1024) =>
        new(
            inputPath,
            maximumFileSizeBytes,
            outputDirectory,
            "1.1.0-test",
            2);

    public static string Envelope(params string[] rawEvents)
    {
        var records = string.Join(
            "\n",
            rawEvents.Select(rawXml => $"  <Record><![CDATA[{rawXml}]]></Record>"));
        return $"""
                <DeleteAuditOfflineEvents xmlns="urn:deleteaudit:offline:v1" formatVersion="1">
                {records}
                </DeleteAuditOfflineEvents>
                """;
    }

    public static string SysmonDeleteXml(
        long recordId,
        DateTimeOffset eventTimeUtc,
        string targetPath) =>
        $"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-Sysmon" />
             <EventID>26</EventID>
             <EventRecordID>{recordId}</EventRecordID>
             <Channel>Microsoft-Windows-Sysmon/Operational</Channel>
             <Computer>LAB-PC</Computer>
             <TimeCreated SystemTime="{eventTimeUtc.ToUniversalTime():O}" />
           </System>
           <EventData>
             <Data Name="TargetFilename">{targetPath}</Data>
             <Data Name="ProcessId">4242</Data>
             <Data Name="Image">C:\Lab\Tools\fixture.exe</Data>
              <Data Name="ProcessGuid">&#123;11111111-1111-1111-1111-111111111111&#125;</Data>
             <Data Name="User">LAB\Analyst</Data>
           </EventData>
         </Event>
         """;
}
