namespace DeleteAudit.Domain;

public sealed record AuditRiskOptions(
    TimeSpan SessionWindow,
    int WarningCount,
    int CriticalCount)
{
    public void Validate()
    {
        if (SessionWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SessionWindow));
        }

        if (WarningCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WarningCount));
        }

        if (CriticalCount <= WarningCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CriticalCount),
                "CriticalCount must be greater than WarningCount.");
        }
    }
}
