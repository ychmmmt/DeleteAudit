using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Reporting;

public static class ImportReportBuilder
{
    public static ImportReport Build(
        OfflineInputFileSnapshot? inputFile,
        int parsedSuccessCount,
        int parsedFailureCount,
        IEnumerable<RawWindowsEvent> rawEvents,
        int deleteFactCount,
        IEnumerable<CorrelationResult> correlations,
        IEnumerable<SessionAggregationResult> aggregationResults,
        IEnumerable<ImportDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(rawEvents);
        ArgumentNullException.ThrowIfNull(correlations);
        ArgumentNullException.ThrowIfNull(aggregationResults);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var rawEventArray = rawEvents.ToArray();
        var correlationArray = correlations.ToArray();
        var sessions = aggregationResults
            .Select(result => result.Session)
            .GroupBy(session => session.DeleteSessionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var eventIdCounts = rawEventArray
            .GroupBy(raw => raw.EventId)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());

        var confidenceCounts = Enum
            .GetValues<CorrelationConfidence>()
            .ToDictionary(
                confidence => confidence,
                confidence => correlationArray.Count(result => result.Confidence == confidence));

        var pathRisks = correlationArray
            .Where(result => !string.IsNullOrWhiteSpace(result.Event.FullPath))
            .Select(result =>
            {
                var session = sessions.FirstOrDefault(
                    candidate => candidate.DeleteEventIds.Contains(result.Event.DeleteEventId));
                return new
                {
                    Path = result.Event.FullPath!,
                    Risk = session?.CurrentRisk ?? AuditRiskLevel.Informational
                };
            })
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HighRiskPathSummary(
                group.First().Path,
                group.Max(item => item.Risk),
                group.Count()))
            .OrderByDescending(item => item.RiskLevel)
            .ThenByDescending(item => item.DeleteFactCount)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        return new ImportReport(
            inputFile,
            parsedSuccessCount,
            parsedFailureCount,
            eventIdCounts,
            deleteFactCount,
            confidenceCounts,
            sessions.Count(session => session.CurrentRisk == AuditRiskLevel.Warning),
            sessions.Count(session => session.CurrentRisk == AuditRiskLevel.Critical),
            pathRisks,
            diagnostics.ToArray());
    }
}
