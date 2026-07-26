using System.Data.Common;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Correlation;
using DeleteAudit.Infrastructure.Importing.Output;
using DeleteAudit.Infrastructure.Importing.Persistence;
using DeleteAudit.Infrastructure.Importing.Reporting;
using DeleteAudit.Infrastructure.Importing.Sources;
using DeleteAudit.Infrastructure.Parsing;
using DeleteAudit.Infrastructure.Sessions;

namespace DeleteAudit.Infrastructure.Importing;

public sealed class OfflineImportPipeline
{
    private readonly IReadOnlyList<IOfflineEventSource> _sources;
    private readonly IOfflineImportRepository _repository;
    private readonly IImportJsonlWriter _jsonlWriter;
    private readonly WindowsEventXmlParser _parser;
    private readonly DeleteEventCorrelator _correlator;
    private readonly AuditRiskOptions _riskOptions;
    private readonly IReadOnlyList<ProtectedPathRule> _protectedRules;
    private readonly TimeProvider _timeProvider;

    public OfflineImportPipeline(
        IOfflineImportRepository repository,
        IImportJsonlWriter jsonlWriter,
        CorrelationOptions correlationOptions,
        AuditRiskOptions riskOptions,
        IEnumerable<ProtectedPathRule>? protectedRules = null,
        IEnumerable<IOfflineEventSource>? sources = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(jsonlWriter);
        ArgumentNullException.ThrowIfNull(correlationOptions);
        ArgumentNullException.ThrowIfNull(riskOptions);
        correlationOptions.Validate();
        riskOptions.Validate();

        _repository = repository;
        _jsonlWriter = jsonlWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _parser = new WindowsEventXmlParser(_timeProvider);
        _correlator = new DeleteEventCorrelator(correlationOptions);
        _riskOptions = riskOptions;
        _protectedRules = (protectedRules ?? []).ToArray();
        _sources = (sources ??
            [
                new MultiEventXmlOfflineEventSource(),
                new EvtxOfflineEventSource()
            ]).ToArray();

        var duplicateExtensions = _sources
            .GroupBy(source => source.SupportedFileExtension, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToArray();
        if (duplicateExtensions.Length != 0)
        {
            throw new ArgumentException(
                $"Multiple offline sources handle: {string.Join(", ", duplicateExtensions)}.",
                nameof(sources));
        }
    }

    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedUtc = _timeProvider.GetUtcNow();
        try
        {
            request.Validate();
        }
        catch (ArgumentException exception)
        {
            return FailedWithoutSession(Diagnostic(
                "invalid_import_request",
                exception.Message,
                "source_validation"));
        }

        var extension = Path.GetExtension(request.InputFilePath);
        var source = _sources.FirstOrDefault(candidate =>
            string.Equals(
                candidate.SupportedFileExtension,
                extension,
                StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return FailedWithoutSession(Diagnostic(
                "unsupported_file_extension",
                $"No offline source accepts the '{extension}' extension.",
                "source_validation"));
        }

        OfflineEventSourceResult sourceResult;
        try
        {
            sourceResult = await source
                .ReadAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedPipelineFailure(exception))
        {
            return FailedWithoutSession(Diagnostic(
                "offline_source_failed",
                exception.Message,
                "read"));
        }

        var sourceDiagnostics = sourceResult.Diagnostics
            .Concat(sourceResult.Records.SelectMany(record => record.Diagnostics))
            .ToList();
        if (sourceResult.InputFile is null)
        {
            return FailedWithoutSession(sourceDiagnostics.ToArray());
        }

        try
        {
            await _repository.ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
            var existing = await _repository
                .FindBySha256Async(sourceResult.InputFile.Sha256, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var alreadyImported = Diagnostic(
                    "already_imported",
                    $"Content SHA-256 was already imported as session {existing.ImportSessionId}.",
                    "persist",
                    ImportDiagnosticSeverity.Information);
                sourceDiagnostics.Add(alreadyImported);
                var duplicateReport = ImportReportBuilder.Build(
                    sourceResult.InputFile,
                    0,
                    0,
                    [],
                    0,
                    [],
                    [],
                    sourceDiagnostics);
                return new ImportResult(
                    ImportStatus.AlreadyImported,
                    existing,
                    duplicateReport,
                    true,
                    null,
                    null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedPipelineFailure(exception))
        {
            sourceDiagnostics.Add(Diagnostic(
                "database_schema_or_lookup_failed",
                exception.Message,
                "persist"));
            return FailedWithReport(sourceResult.InputFile, sourceDiagnostics);
        }

        if (sourceResult.IsFatal
            && sourceDiagnostics.Any(diagnostic =>
                diagnostic.Code is "input_changed_during_import"
                    or "input_post_verification_failed"))
        {
            return FailedWithReport(sourceResult.InputFile, sourceDiagnostics);
        }

        var parsed = ParseRecords(sourceResult.Records, sourceDiagnostics);
        var eligibleRawIds = new HashSet<string>(
            parsed
                .Where(item => item.PreparedRecord.RawEvent is not null)
                .Where(item => CanPersistRaw(item.PreparedRecord.RawEvent!))
                .Select(item => item.PreparedRecord.RawEvent!.RawEventId),
            StringComparer.Ordinal);

        MarkProjectionFailures(parsed, eligibleRawIds, sourceDiagnostics);

        var deleteCandidateIndexes = Enumerable
            .Range(0, parsed.Count)
            .Where(index =>
                parsed[index].ParseResult?.DeleteEvent is not null
                && eligibleRawIds.Contains(
                    parsed[index].ParseResult!.DeleteEvent!.RawEventId)
                && CanPersistDelete(parsed[index].ParseResult!.DeleteEvent!))
            .ToArray();
        IReadOnlySet<string> existingDeleteEventIds;
        try
        {
            existingDeleteEventIds = await _repository
                .FindExistingDeleteEventIdsAsync(
                    deleteCandidateIndexes
                        .Select(index =>
                            parsed[index].ParseResult!.DeleteEvent!.DeleteEventId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedPipelineFailure(exception))
        {
            sourceDiagnostics.Add(Diagnostic(
                "database_delete_deduplication_lookup_failed",
                exception.Message,
                "persist"));
            return FailedWithParsedReport(
                sourceResult.InputFile,
                parsed,
                sourceDiagnostics);
        }

        var deleteCandidates = SelectNewDeleteCandidates(
            parsed,
            deleteCandidateIndexes,
            existingDeleteEventIds,
            sourceDiagnostics);
        var processContexts = parsed
            .Where(item =>
                item.PreparedRecord.ProcessContext is not null
                && eligibleRawIds.Contains(item.PreparedRecord.ProcessContext.RawEventId))
            .Select(item => item.PreparedRecord.ProcessContext!)
            .ToArray();
        var securityEvidence = parsed
            .Where(item =>
                item.ParseResult?.SecurityEvidence is not null
                && eligibleRawIds.Contains(item.ParseResult.SecurityEvidence.RawEventId))
            .Select(item => item.ParseResult!.SecurityEvidence!)
            .ToArray();
        var aggregator = new DeleteSessionAggregator(
            _riskOptions,
            _protectedRules,
            _timeProvider);
        var deleteProjections = new List<PreparedDeleteProjection>();
        foreach (var candidate in deleteCandidates)
        {
            var correlation = _correlator.Correlate(
                candidate.ParseResult!.DeleteEvent!,
                processContexts,
                securityEvidence);
            var aggregation = aggregator.Add(correlation.Event);
            deleteProjections.Add(new PreparedDeleteProjection(correlation, aggregation));
        }

        var preparedRecords = parsed
            .Select(item => item.PreparedRecord)
            .ToArray();
        var successCount = preparedRecords.Count(
            record => record.Outcome == ImportRecordOutcome.Succeeded);
        var ignoredCount = preparedRecords.Count(
            record => record.Outcome == ImportRecordOutcome.Ignored);
        var errorCount = preparedRecords.Count(
            record => record.Outcome == ImportRecordOutcome.Error);
        var status = sourceResult.IsFatal
            ? successCount + ignoredCount > 0
                ? ImportStatus.PartialFailure
                : ImportStatus.Failed
            : errorCount switch
        {
            0 => ImportStatus.Completed,
            _ when successCount + ignoredCount > 0 => ImportStatus.PartialFailure,
            _ => ImportStatus.Failed
        };
        var importSession = new ImportSession(
            Guid.NewGuid().ToString("D"),
            sourceResult.InputFile.OriginalFileName,
            sourceResult.InputFile.NormalizedAbsolutePath,
            sourceResult.InputFile.FileSize,
            sourceResult.InputFile.LastWriteUtc,
            sourceResult.InputFile.Sha256,
            startedUtc,
            _timeProvider.GetUtcNow(),
            preparedRecords.Length,
            successCount,
            ignoredCount,
            errorCount,
            request.ApplicationVersion,
            request.SchemaVersion,
            status);
        var preparedImport = new PreparedImport(
            source.SupportedFileExtension.Equals(".evtx", StringComparison.OrdinalIgnoreCase)
                ? "evtx"
                : "multi_xml",
            importSession,
            preparedRecords,
            deleteProjections,
            sourceDiagnostics);

        var report = BuildReport(
            sourceResult.InputFile,
            parsed,
            deleteProjections,
            sourceDiagnostics);

        OfflineImportCommitResult commitResult;
        try
        {
            commitResult = await _repository
                .CommitAsync(preparedImport, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedPipelineFailure(exception))
        {
            sourceDiagnostics.Add(Diagnostic(
                "database_commit_failed",
                exception.Message,
                "persist"));
            report = BuildReport(
                sourceResult.InputFile,
                parsed,
                deleteProjections,
                sourceDiagnostics);
            return new ImportResult(
                ImportStatus.Failed,
                importSession with { Status = ImportStatus.Failed },
                report,
                false,
                null,
                null);
        }

        if (commitResult.Status == ImportStatus.AlreadyImported)
        {
            sourceDiagnostics.Add(Diagnostic(
                "already_imported",
                $"Content SHA-256 was already imported as session {commitResult.Session.ImportSessionId}.",
                "persist",
                ImportDiagnosticSeverity.Information));
            report = BuildReport(
                sourceResult.InputFile,
                parsed,
                deleteProjections,
                sourceDiagnostics);
            return new ImportResult(
                ImportStatus.AlreadyImported,
                commitResult.Session,
                report,
                true,
                null,
                null);
        }

        var jsonlRecords = preparedRecords
            .Select(record => new ImportJsonlRecord(
                record.SourceRecord.RecordNumber,
                record.Outcome,
                record.RawEvent?.EventId,
                record.RawEvent?.RawEventId,
                record.SourceRecord.RawXml,
                sourceDiagnostics
                    .Where(diagnostic =>
                        diagnostic.RecordNumber == record.SourceRecord.RecordNumber)
                    .ToArray()))
            .ToArray();
        ImportJsonlWriteResult jsonlResult;
        try
        {
            jsonlResult = await _jsonlWriter
                .WriteAsync(
                    importSession,
                    jsonlRecords,
                    request.JsonlOutputDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedPipelineFailure(exception))
        {
            jsonlResult = new ImportJsonlWriteResult(
                false,
                null,
                null,
                0,
                null,
                null,
                null,
                Diagnostic("jsonl_write_failed", exception.Message, "jsonl"));
        }

        var finalStatus = jsonlResult.Success
            ? status
            : StatusWithOutputFailure(status);
        if (jsonlResult.Diagnostic is not null)
        {
            sourceDiagnostics.Add(jsonlResult.Diagnostic);
            report = BuildReport(
                sourceResult.InputFile,
                parsed,
                deleteProjections,
                sourceDiagnostics);
        }

        try
        {
            await _repository
                .UpdateOutputAsync(
                    importSession.ImportSessionId,
                    new ImportOutputUpdate(
                        finalStatus,
                        jsonlResult.Success ? "complete" : "failed",
                        jsonlResult.JsonlPath,
                        jsonlResult.JsonlSha256,
                        jsonlResult.ManifestPath,
                        jsonlResult.Diagnostic),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedPipelineFailure(exception))
        {
            finalStatus = StatusWithOutputFailure(status);
            sourceDiagnostics.Add(Diagnostic(
                "output_metadata_update_failed",
                exception.Message,
                "persist"));
            report = BuildReport(
                sourceResult.InputFile,
                parsed,
                deleteProjections,
                sourceDiagnostics);
        }

        return new ImportResult(
            finalStatus,
            importSession with { Status = finalStatus },
            report,
            true,
            jsonlResult.JsonlPath,
            jsonlResult.ManifestPath);
    }

    private List<ParsedImportRecord> ParseRecords(
        IReadOnlyList<OfflineEventRecord> sourceRecords,
        List<ImportDiagnostic> diagnostics)
    {
        var output = new List<ParsedImportRecord>(sourceRecords.Count);
        foreach (var sourceRecord in sourceRecords.OrderBy(record => record.RecordNumber))
        {
            if (sourceRecord.State == OfflineRecordState.Unavailable
                || sourceRecord.RawXml is null)
            {
                output.Add(new ParsedImportRecord(
                    new PreparedImportRecord(
                        sourceRecord,
                        ImportRecordOutcome.Error,
                        null,
                        null),
                    null,
                    false));
                continue;
            }

            var result = _parser.Parse(sourceRecord.RawXml);
            if (result.Error is not null)
            {
                var unsupported = result.Error.Code == ParseErrorCode.UnsupportedEvent
                    && result.RawEvent is not null;
                diagnostics.Add(Diagnostic(
                    $"parse_{result.Error.Code.ToString().ToLowerInvariant()}",
                    result.Error.Message,
                    "parse",
                    unsupported
                        ? ImportDiagnosticSeverity.Warning
                        : ImportDiagnosticSeverity.Error,
                    sourceRecord.RecordNumber));
                output.Add(new ParsedImportRecord(
                    new PreparedImportRecord(
                        sourceRecord,
                        unsupported
                            ? ImportRecordOutcome.Ignored
                            : ImportRecordOutcome.Error,
                        result.RawEvent,
                        result.ProcessContext),
                    result,
                    unsupported));
                continue;
            }

            var ignored = result.DeleteEvent is null
                && result.ProcessContext is null
                && result.SecurityEvidence is null;
            if (ignored)
            {
                diagnostics.Add(Diagnostic(
                    "supported_event_has_no_delete_evidence",
                    "The event was parsed but does not establish or enrich a delete fact.",
                    "parse",
                    ImportDiagnosticSeverity.Information,
                    sourceRecord.RecordNumber));
            }

            output.Add(new ParsedImportRecord(
                new PreparedImportRecord(
                    sourceRecord,
                    ignored
                        ? ImportRecordOutcome.Ignored
                        : ImportRecordOutcome.Succeeded,
                    result.RawEvent,
                    result.ProcessContext),
                result,
                true));
        }

        return output;
    }

    private static ParsedImportRecord[] SelectNewDeleteCandidates(
        IList<ParsedImportRecord> records,
        IReadOnlyList<int> candidateIndexes,
        IReadOnlySet<string> existingDeleteEventIds,
        List<ImportDiagnostic> diagnostics)
    {
        var selected = new List<ParsedImportRecord>(candidateIndexes.Count);
        var seenInInput = new HashSet<string>(StringComparer.Ordinal);

        foreach (var index in candidateIndexes)
        {
            var item = records[index];
            var deleteEvent = item.ParseResult!.DeleteEvent!;
            var alreadyPersisted = existingDeleteEventIds.Contains(
                deleteEvent.DeleteEventId);
            var repeatedInInput = !seenInInput.Add(deleteEvent.DeleteEventId);
            if (!alreadyPersisted && !repeatedInInput)
            {
                selected.Add(item);
                continue;
            }

            records[index] = item with
            {
                PreparedRecord = item.PreparedRecord with
                {
                    Outcome = ImportRecordOutcome.Ignored
                }
            };
            diagnostics.Add(Diagnostic(
                alreadyPersisted
                    ? "delete_fact_already_imported"
                    : "duplicate_delete_fact_in_input",
                alreadyPersisted
                    ? "The delete fact already exists and was excluded from session and risk aggregation."
                    : "A repeated delete fact in the same input was excluded from session and risk aggregation.",
                "normalize",
                ImportDiagnosticSeverity.Information,
                item.PreparedRecord.SourceRecord.RecordNumber));
        }

        return selected
            .OrderBy(item => item.ParseResult!.DeleteEvent!.OccurredUtc)
            .ThenBy(item => item.PreparedRecord.SourceRecord.RecordNumber)
            .ToArray();
    }

    private static void MarkProjectionFailures(
        IList<ParsedImportRecord> records,
        HashSet<string> eligibleRawIds,
        List<ImportDiagnostic> diagnostics)
    {
        for (var index = 0; index < records.Count; index++)
        {
            var item = records[index];
            var raw = item.PreparedRecord.RawEvent;
            if (raw is not null
                && raw.Source != WindowsEventSource.Unsupported
                && !eligibleRawIds.Contains(raw.RawEventId))
            {
                records[index] = item with
                {
                    PreparedRecord = item.PreparedRecord with
                    {
                        Outcome = ImportRecordOutcome.Error
                    }
                };
                diagnostics.Add(Diagnostic(
                    "raw_projection_skipped_missing_required_field",
                    "The parsed event is retained in the import record but cannot be written to the Phase 1A raw_events schema without inventing a required field.",
                    "normalize",
                    ImportDiagnosticSeverity.Error,
                    item.PreparedRecord.SourceRecord.RecordNumber));
                continue;
            }

            if (item.ParseResult?.ProcessContext is { } process
                && (process.ProcessId is null || string.IsNullOrWhiteSpace(process.ProcessGuid)))
            {
                records[index] = item with
                {
                    PreparedRecord = item.PreparedRecord with
                    {
                        Outcome = ImportRecordOutcome.Error
                    }
                };
                diagnostics.Add(Diagnostic(
                    "process_projection_skipped_missing_required_field",
                    "The process event remains raw evidence; process_observations requires a real PID and Process GUID.",
                    "normalize",
                    ImportDiagnosticSeverity.Error,
                    item.PreparedRecord.SourceRecord.RecordNumber));
            }

            if (item.ParseResult?.DeleteEvent is { } deleteEvent
                && !CanPersistDelete(deleteEvent))
            {
                records[index] = item with
                {
                    PreparedRecord = item.PreparedRecord with
                    {
                        Outcome = ImportRecordOutcome.Error
                    }
                };
                diagnostics.Add(Diagnostic(
                    "delete_projection_skipped_missing_required_field",
                    "The delete fact remains raw evidence; delete_events requires a real record ID and full path.",
                    "normalize",
                    ImportDiagnosticSeverity.Error,
                    item.PreparedRecord.SourceRecord.RecordNumber));
            }
        }
    }

    private static ImportReport BuildReport(
        OfflineInputFileSnapshot inputFile,
        IReadOnlyList<ParsedImportRecord> parsed,
        IReadOnlyList<PreparedDeleteProjection> deleteProjections,
        IReadOnlyList<ImportDiagnostic> diagnostics) =>
        ImportReportBuilder.Build(
            inputFile,
            parsed.Count(item => item.CountedAsParsedSuccess),
            parsed.Count(item => !item.CountedAsParsedSuccess),
            parsed
                .Where(item => item.PreparedRecord.RawEvent is not null)
                .Select(item => item.PreparedRecord.RawEvent!),
            deleteProjections.Count,
            deleteProjections.Select(item => item.CorrelationResult),
            deleteProjections.Select(item => item.SessionAggregationResult),
            diagnostics);

    private static ImportResult FailedWithoutSession(params ImportDiagnostic[] diagnostics)
    {
        var report = ImportReportBuilder.Build(
            null,
            0,
            0,
            [],
            0,
            [],
            [],
            diagnostics);
        return new ImportResult(
            ImportStatus.Failed,
            null,
            report,
            false,
            null,
            null);
    }

    private static ImportResult FailedWithReport(
        OfflineInputFileSnapshot inputFile,
        IReadOnlyList<ImportDiagnostic> diagnostics)
    {
        var report = ImportReportBuilder.Build(
            inputFile,
            0,
            0,
            [],
            0,
            [],
            [],
            diagnostics);
        return new ImportResult(
            ImportStatus.Failed,
            null,
            report,
            false,
            null,
            null);
    }

    private static ImportResult FailedWithParsedReport(
        OfflineInputFileSnapshot inputFile,
        IReadOnlyList<ParsedImportRecord> parsed,
        IReadOnlyList<ImportDiagnostic> diagnostics)
    {
        var report = ImportReportBuilder.Build(
            inputFile,
            parsed.Count(item => item.CountedAsParsedSuccess),
            parsed.Count(item => !item.CountedAsParsedSuccess),
            parsed
                .Where(item => item.PreparedRecord.RawEvent is not null)
                .Select(item => item.PreparedRecord.RawEvent!),
            0,
            [],
            [],
            diagnostics);
        return new ImportResult(
            ImportStatus.Failed,
            null,
            report,
            false,
            null,
            null);
    }

    private static bool CanPersistRaw(RawWindowsEvent value) =>
        value.Source != WindowsEventSource.Unsupported
        && !string.IsNullOrWhiteSpace(value.ComputerName)
        && !string.IsNullOrWhiteSpace(value.ChannelName)
        && !string.IsNullOrWhiteSpace(value.ProviderName)
        && value.EventRecordId is not null;

    private static bool CanPersistDelete(NormalizedDeleteEvent value) =>
        value.EventRecordId is not null
        && !string.IsNullOrWhiteSpace(value.FullPath);

    private static ImportStatus StatusWithOutputFailure(ImportStatus importStatus) =>
        importStatus == ImportStatus.Failed
            ? ImportStatus.Failed
            : ImportStatus.PartialFailure;

    private static bool IsExpectedPipelineFailure(Exception exception) =>
        exception is InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or DbException;

    private static ImportDiagnostic Diagnostic(
        string code,
        string message,
        string stage,
        ImportDiagnosticSeverity severity = ImportDiagnosticSeverity.Error,
        long? recordNumber = null) =>
        new(code, message, severity, stage, recordNumber);

    private sealed record ParsedImportRecord(
        PreparedImportRecord PreparedRecord,
        WindowsEventParseResult? ParseResult,
        bool CountedAsParsedSuccess);
}
