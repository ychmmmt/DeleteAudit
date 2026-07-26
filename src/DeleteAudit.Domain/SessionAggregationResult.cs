namespace DeleteAudit.Domain;

public sealed record SessionAggregationResult(
    DeleteSession Session,
    RiskAssessment Assessment,
    bool SessionCreated,
    bool EventAdded);
