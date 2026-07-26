using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Integrity;

namespace DeleteAudit.Infrastructure.Importing.Output;

public sealed class FileImportJsonlWriter : IImportJsonlWriter
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public async Task<ImportJsonlWriteResult> WriteAsync(
        ImportSession importSession,
        IReadOnlyCollection<ImportJsonlRecord> records,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importSession);
        ArgumentNullException.ThrowIfNull(records);

        if (!Guid.TryParseExact(importSession.ImportSessionId, "D", out var sessionId))
        {
            return Failure("jsonl_invalid_session_id", "ImportSessionId must be a GUID.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory)
            || !Path.IsPathFullyQualified(outputDirectory))
        {
            return Failure(
                "jsonl_invalid_output_directory",
                "The JSONL output directory must be a fully qualified path.");
        }

        if (records.Any(record => record.RecordNumber <= 0))
        {
            return Failure(
                "jsonl_invalid_record_number",
                "JSONL record numbers must be positive.");
        }

        if (records
            .GroupBy(record => record.RecordNumber)
            .Any(group => group.Skip(1).Any()))
        {
            return Failure(
                "jsonl_duplicate_record_number",
                "JSONL record numbers must be unique within an import session.");
        }

        string normalizedOutputDirectory;
        try
        {
            normalizedOutputDirectory = Path.GetFullPath(outputDirectory);
        }
        catch (Exception exception) when (IsExpectedWriteFailure(exception))
        {
            return Failure("jsonl_invalid_output_directory", exception.Message);
        }

        var fileStem = sessionId.ToString("D", CultureInfo.InvariantCulture);
        var jsonlPath = Path.Combine(normalizedOutputDirectory, $"{fileStem}.jsonl");
        var manifestPath = Path.Combine(normalizedOutputDirectory, $"{fileStem}.manifest.json");
        var pendingManifestPath = $"{manifestPath}.pending";
        var orderedRecords = records.OrderBy(record => record.RecordNumber).ToArray();

        string? firstHash = null;
        string? lastHash = null;
        string? jsonlSha256 = null;
        var writtenCount = 0;

        try
        {
            Directory.CreateDirectory(normalizedOutputDirectory);

            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var stream = new FileStream(
                             jsonlPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                             }))
            {
                foreach (var record in orderedRecords)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = JsonlHashChain.CreateEntry(
                        CreatePayload(record),
                        lastHash);
                    var lineBytes = Utf8WithoutBom.GetBytes(entry.JsonLine);

                    await stream
                        .WriteAsync(lineBytes, cancellationToken)
                        .ConfigureAwait(false);
                    await stream
                        .WriteAsync(NewLine, cancellationToken)
                        .ConfigureAwait(false);
                    fileHash.AppendData(lineBytes);
                    fileHash.AppendData(NewLine);

                    firstHash ??= entry.EntryHash;
                    lastHash = entry.EntryHash;
                    writtenCount++;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            jsonlSha256 = Convert
                .ToHexString(fileHash.GetHashAndReset())
                .ToLowerInvariant();

            await WritePendingManifestAsync(
                    pendingManifestPath,
                    importSession,
                    Path.GetFileName(jsonlPath),
                    writtenCount,
                    firstHash,
                    lastHash,
                    jsonlSha256,
                    cancellationToken)
                .ConfigureAwait(false);

            File.Move(pendingManifestPath, manifestPath);

            return new ImportJsonlWriteResult(
                true,
                jsonlPath,
                manifestPath,
                writtenCount,
                firstHash,
                lastHash,
                jsonlSha256,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedWriteFailure(exception))
        {
            return new ImportJsonlWriteResult(
                false,
                File.Exists(jsonlPath) ? jsonlPath : null,
                null,
                writtenCount,
                firstHash,
                lastHash,
                jsonlSha256,
                new ImportDiagnostic(
                    "jsonl_write_failed",
                    exception.Message,
                    ImportDiagnosticSeverity.Error,
                    "jsonl"));
        }
    }

    private static object CreatePayload(ImportJsonlRecord record) =>
        new
        {
            recordNumber = record.RecordNumber,
            outcome = ToWireValue(record.Outcome),
            eventId = record.EventId,
            rawEventId = record.RawEventId,
            rawXml = record.RawXml,
            diagnostics = record.Diagnostics.Select(diagnostic => new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                severity = ToWireValue(diagnostic.Severity),
                stage = diagnostic.Stage,
                recordNumber = diagnostic.RecordNumber
            })
        };

    private static async Task WritePendingManifestAsync(
        string path,
        ImportSession importSession,
        string jsonlFileName,
        int entryCount,
        string? firstHash,
        string? lastHash,
        string jsonlSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString("status", "success");
            writer.WriteString("importSessionId", importSession.ImportSessionId);
            writer.WriteString("importStatus", ToWireValue(importSession.Status));
            writer.WriteString("sourceSha256", importSession.Sha256);
            writer.WriteString("jsonlFileName", jsonlFileName);
            writer.WriteNumber("entryCount", entryCount);
            writer.WriteNumber("totalRecordCount", importSession.TotalRecordCount);
            writer.WriteNumber("successRecordCount", importSession.SuccessCount);
            writer.WriteNumber("ignoredRecordCount", importSession.IgnoredCount);
            writer.WriteNumber("errorRecordCount", importSession.ErrorCount);
            WriteNullableString(writer, "firstEntryHash", firstHash);
            WriteNullableString(writer, "lastEntryHash", lastHash);
            writer.WriteString("jsonlSha256", jsonlSha256);
            writer.WriteString(
                "completedUtc",
                importSession.EndedUtc.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture));
            writer.WriteString("applicationVersion", importSession.ApplicationVersion);
            writer.WriteNumber("schemaVersion", importSession.SchemaVersion);
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static bool IsExpectedWriteFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException
            or CryptographicException
            or JsonException;

    private static ImportJsonlWriteResult Failure(string code, string message) =>
        new(
            false,
            null,
            null,
            0,
            null,
            null,
            null,
            new ImportDiagnostic(
                code,
                message,
                ImportDiagnosticSeverity.Error,
                "jsonl"));

    private static string ToWireValue(ImportRecordOutcome outcome) => outcome switch
    {
        ImportRecordOutcome.Succeeded => "succeeded",
        ImportRecordOutcome.Ignored => "ignored",
        ImportRecordOutcome.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToWireValue(ImportDiagnosticSeverity severity) => severity switch
    {
        ImportDiagnosticSeverity.Information => "information",
        ImportDiagnosticSeverity.Warning => "warning",
        ImportDiagnosticSeverity.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
    };

    private static string ToWireValue(ImportStatus status) => status switch
    {
        ImportStatus.Completed => "completed",
        ImportStatus.PartialFailure => "partial_failure",
        ImportStatus.Failed => "failed",
        ImportStatus.AlreadyImported => "already_imported",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
