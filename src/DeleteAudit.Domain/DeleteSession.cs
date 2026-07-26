namespace DeleteAudit.Domain;

public sealed class DeleteSession
{
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);

    public DeleteSession(
        string deleteSessionId,
        string processIdentity,
        string userIdentity,
        string mainPath,
        DateTimeOffset openedUtc)
    {
        DeleteSessionId = deleteSessionId;
        ProcessIdentity = processIdentity;
        UserIdentity = userIdentity;
        MainPath = mainPath;
        OpenedUtc = openedUtc;
        LastEventUtc = openedUtc;
    }

    public string DeleteSessionId { get; }

    public string ProcessIdentity { get; }

    public string UserIdentity { get; }

    public string MainPath { get; }

    public DateTimeOffset OpenedUtc { get; }

    public DateTimeOffset LastEventUtc { get; private set; }

    public int ConfirmedItemCount => _eventIds.Count;

    public int ProtectedItemCount { get; private set; }

    public AuditRiskLevel CurrentRisk { get; private set; } = AuditRiskLevel.Informational;

    public IReadOnlyCollection<string> DeleteEventIds => _eventIds;

    public bool Add(
        NormalizedDeleteEvent deleteEvent,
        bool isProtected,
        AuditRiskLevel riskLevel)
    {
        if (!_eventIds.Add(deleteEvent.DeleteEventId))
        {
            return false;
        }

        if (deleteEvent.OccurredUtc > LastEventUtc)
        {
            LastEventUtc = deleteEvent.OccurredUtc;
        }

        if (isProtected)
        {
            ProtectedItemCount++;
        }

        if (riskLevel > CurrentRisk)
        {
            CurrentRisk = riskLevel;
        }

        return true;
    }
}
