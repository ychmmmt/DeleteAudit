using System.Xml;
using System.Xml.Linq;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Sources;

public sealed class MultiEventXmlOfflineEventSource : IOfflineEventSource
{
    public const string EnvelopeNamespace = "urn:deleteaudit:offline:v1";
    private static readonly XName EnvelopeName =
        XName.Get("DeleteAuditOfflineEvents", EnvelopeNamespace);
    private static readonly XName RecordName = XName.Get("Record", EnvelopeNamespace);

    public string SupportedFileExtension => ".xml";

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
                "The selected XML input file is empty.",
                ImportDiagnosticSeverity.Error,
                "read"));
            isFatal = true;
        }
        else
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = request.MaximumFileSizeBytes
                };

                using var reader = XmlReader.Create(inputFile.ReadStream, settings);
                var document = await XDocument
                    .LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken)
                    .ConfigureAwait(false);
                ReadEnvelope(document, records, diagnostics, ref isFatal);
            }
            catch (XmlException exception)
            {
                diagnostics.Add(new ImportDiagnostic(
                    "invalid_xml_envelope",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "extract"));
                isFatal = true;
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add(new ImportDiagnostic(
                    "invalid_xml_envelope",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "extract"));
                isFatal = true;
            }
            catch (IOException exception)
            {
                diagnostics.Add(new ImportDiagnostic(
                    "input_file_read_failed",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "read"));
                isFatal = true;
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

    private static void ReadEnvelope(
        XDocument document,
        List<OfflineEventRecord> records,
        List<ImportDiagnostic> diagnostics,
        ref bool isFatal)
    {
        var root = document.Root;
        if (root is null || root.Name != EnvelopeName)
        {
            diagnostics.Add(new ImportDiagnostic(
                "invalid_xml_envelope",
                $"The root element must be '{EnvelopeName}'.",
                ImportDiagnosticSeverity.Error,
                "extract"));
            isFatal = true;
            return;
        }

        if (!string.Equals(
                (string?)root.Attribute("formatVersion"),
                "1",
                StringComparison.Ordinal))
        {
            diagnostics.Add(new ImportDiagnostic(
                "unsupported_xml_envelope_version",
                "The XML envelope formatVersion must be '1'.",
                ImportDiagnosticSeverity.Error,
                "extract"));
            isFatal = true;
            return;
        }

        var children = root.Elements().ToArray();
        if (children.Any(element => element.Name != RecordName))
        {
            diagnostics.Add(new ImportDiagnostic(
                "invalid_xml_envelope",
                "The XML envelope may contain only direct Record elements.",
                ImportDiagnosticSeverity.Error,
                "extract"));
            isFatal = true;
            return;
        }

        long recordNumber = 0;
        foreach (var element in children)
        {
            recordNumber++;
            var cdataNodes = element.Nodes().OfType<XCData>().ToArray();
            var hasInvalidContent = element.Attributes().Any()
                || cdataNodes.Length == 0
                || element.Nodes().Any(node =>
                    node is not XCData
                    && (node is not XText text || !string.IsNullOrWhiteSpace(text.Value)));

            if (hasInvalidContent)
            {
                var diagnostic = new ImportDiagnostic(
                    "invalid_xml_record",
                    "Each Record must contain only one or more CDATA sections.",
                    ImportDiagnosticSeverity.Error,
                    "extract",
                    recordNumber);
                records.Add(new OfflineEventRecord(
                    recordNumber,
                    null,
                    OfflineRecordState.Unavailable,
                    [diagnostic]));
                continue;
            }

            records.Add(new OfflineEventRecord(
                recordNumber,
                string.Concat(cdataNodes.Select(node => node.Value)),
                OfflineRecordState.Available,
                []));
        }
    }

    private static OfflineEventSourceResult Fatal(
        OfflineInputFileSnapshot? snapshot,
        ImportDiagnostic diagnostic) =>
        new(snapshot, [], [diagnostic], true);
}
