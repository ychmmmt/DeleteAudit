using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Sources;

public sealed class EvtxOfflineEventSource : IOfflineEventSource
{
    private readonly IEvtxRecordXmlReader _recordReader;

    public EvtxOfflineEventSource(IEvtxRecordXmlReader? recordReader = null)
    {
        _recordReader = recordReader ?? new WindowsEvtxRecordXmlReader();
    }

    public string SupportedFileExtension => ".evtx";

    public async Task<OfflineEventSourceResult> ReadAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var openResult = await OfflineInputFileValidator
            .TryOpenAsync(request, SupportedFileExtension, cancellationToken)
            .ConfigureAwait(false);
        if (!openResult.IsSuccess)
        {
            return Fatal(null, openResult.Diagnostic!);
        }

        await using var inputFile = openResult.File!;
        var records = new List<OfflineEventRecord>();
        var diagnostics = new List<ImportDiagnostic>();
        var isFatal = false;

        if (inputFile.Snapshot.FileSize == 0)
        {
            diagnostics.Add(new ImportDiagnostic(
                "empty_input_file",
                "The selected EVTX input file is empty.",
                ImportDiagnosticSeverity.Error,
                "read"));
            isFatal = true;
        }
        else
        {
            var readResults = _recordReader.ReadRecords(
                inputFile.Snapshot.NormalizedAbsolutePath,
                cancellationToken);
            foreach (var result in readResults)
            {
                records.Add(new OfflineEventRecord(
                    result.RecordNumber,
                    result.RawXml,
                    result.RawXml is null
                        ? OfflineRecordState.Unavailable
                        : OfflineRecordState.Available,
                    result.Diagnostic is null ? [] : [result.Diagnostic]));
                isFatal |= result.IsFatal;
            }
        }

        var verificationDiagnostic = await inputFile
            .VerifyUnchangedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (verificationDiagnostic is not null)
        {
            diagnostics.Add(verificationDiagnostic);
            isFatal = true;
        }

        return new OfflineEventSourceResult(
            inputFile.Snapshot,
            records,
            diagnostics,
            isFatal);
    }

    private static OfflineEventSourceResult Fatal(
        OfflineInputFileSnapshot? snapshot,
        ImportDiagnostic diagnostic) =>
        new(snapshot, [], [diagnostic], true);
}
