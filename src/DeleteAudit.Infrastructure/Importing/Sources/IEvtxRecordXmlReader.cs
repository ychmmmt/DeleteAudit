using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Sources;

public sealed record EvtxRecordXmlReadResult(
    long RecordNumber,
    string? RawXml,
    ImportDiagnostic? Diagnostic,
    bool IsFatal);

public interface IEvtxRecordXmlReader
{
    IReadOnlyList<EvtxRecordXmlReadResult> ReadRecords(
        string normalizedAbsolutePath,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsEvtxRecordXmlReader : IEvtxRecordXmlReader
{
    [SupportedOSPlatform("windows")]
    public static PathType QueryPathType => PathType.FilePath;

    public IReadOnlyList<EvtxRecordXmlReadResult> ReadRecords(
        string normalizedAbsolutePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAbsolutePath);

        if (!OperatingSystem.IsWindows())
        {
            return
            [
                Fatal(
                    0,
                    "platform_not_supported",
                    "EVTX file reading is supported only on Windows.")
            ];
        }

        return ReadWindowsFile(normalizedAbsolutePath, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static List<EvtxRecordXmlReadResult> ReadWindowsFile(
        string normalizedAbsolutePath,
        CancellationToken cancellationToken)
    {
        var results = new List<EvtxRecordXmlReadResult>();
        long recordNumber = 0;

        try
        {
            var query = new EventLogQuery(normalizedAbsolutePath, QueryPathType)
            {
                ReverseDirection = false,
                TolerateQueryErrors = false
            };

            using var reader = new EventLogReader(query);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EventRecord? eventRecord;
                try
                {
                    eventRecord = reader.ReadEvent();
                }
                catch (EventLogException exception)
                {
                    results.Add(Fatal(
                        recordNumber + 1,
                        "evtx_read_failed",
                        exception.Message));
                    break;
                }

                if (eventRecord is null)
                {
                    break;
                }

                recordNumber++;
                using (eventRecord)
                {
                    try
                    {
                        results.Add(new EvtxRecordXmlReadResult(
                            recordNumber,
                            eventRecord.ToXml(),
                            null,
                            false));
                    }
                    catch (EventLogException exception)
                    {
                        results.Add(RecordFailure(recordNumber, exception.Message));
                    }
                    catch (InvalidOperationException exception)
                    {
                        results.Add(RecordFailure(recordNumber, exception.Message));
                    }
                }
            }
        }
        catch (EventLogException exception)
        {
            results.Add(Fatal(
                recordNumber + 1,
                "evtx_open_failed",
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            results.Add(Fatal(
                recordNumber + 1,
                "evtx_access_denied",
                exception.Message));
        }

        return results;
    }

    private static EvtxRecordXmlReadResult RecordFailure(
        long recordNumber,
        string message)
    {
        var diagnostic = new ImportDiagnostic(
            "evtx_record_xml_failed",
            message,
            ImportDiagnosticSeverity.Error,
            "read",
            recordNumber);
        return new EvtxRecordXmlReadResult(recordNumber, null, diagnostic, false);
    }

    private static EvtxRecordXmlReadResult Fatal(
        long recordNumber,
        string code,
        string message)
    {
        var diagnostic = new ImportDiagnostic(
            code,
            message,
            ImportDiagnosticSeverity.Error,
            "read",
            recordNumber == 0 ? null : recordNumber);
        return new EvtxRecordXmlReadResult(recordNumber, null, diagnostic, true);
    }
}
