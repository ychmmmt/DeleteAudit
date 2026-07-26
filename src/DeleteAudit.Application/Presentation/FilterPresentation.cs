using System.Globalization;
using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public sealed record RiskFilterOption(string Label, AuditRiskLevel? Value);

public sealed record SeverityFilterOption(
    string Label,
    ImportDiagnosticSeverity? Value);

public static class FilterPresentation
{
    public static IReadOnlyList<RiskFilterOption> RiskOptions { get; } =
    [
        new("全部", null),
        new("Warning", AuditRiskLevel.Warning),
        new("Critical", AuditRiskLevel.Critical)
    ];

    public static IReadOnlyList<SeverityFilterOption> SeverityOptions { get; } =
    [
        new("全部", null),
        new("Information", ImportDiagnosticSeverity.Information),
        new("Warning", ImportDiagnosticSeverity.Warning),
        new("Error", ImportDiagnosticSeverity.Error)
    ];

    public static DateTimeOffset? ParseOptionalUtc(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces
                | DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{fieldName}必须是有效的 UTC 时间，例如 2026-07-23T12:00:00Z。",
            fieldName);
    }
}
