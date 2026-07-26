namespace DeleteAudit.Domain;

public sealed record CorrelationOptions(TimeSpan CandidateWindow)
{
    public void Validate()
    {
        if (CandidateWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CandidateWindow));
        }
    }
}
