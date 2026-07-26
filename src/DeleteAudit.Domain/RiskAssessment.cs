namespace DeleteAudit.Domain;

public sealed record RiskAssessment(
    string RiskAssessmentId,
    string DeleteSessionId,
    DateTimeOffset AssessedUtc,
    AuditRiskLevel RiskLevel,
    string RuleCode,
    int ObservedCount,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    bool ProtectedPathMatched);
