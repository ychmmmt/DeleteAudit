namespace DeleteAudit.Domain;

public enum WindowsEventSource
{
    Unsupported,
    SysmonDelete,
    SysmonProcess,
    Security4663
}

public enum AuditObjectKind
{
    Unknown,
    File,
    Directory
}

public enum DeletePermissionType
{
    NotObserved,
    Delete,
    DeleteChild,
    DeleteAndDeleteChild
}

public enum AuditRiskLevel
{
    Informational,
    Warning,
    Critical
}

public enum CorrelationMethod
{
    None,
    ProcessGuid,
    DevicePidUserAndTime,
    PathAndTimeHeuristic
}

public enum CorrelationConfidence
{
    None,
    Low,
    Medium,
    High
}

public enum ParseErrorCode
{
    MalformedXml,
    MissingSystemSection,
    MissingEventId,
    InvalidEventId,
    UnsupportedEvent,
    InvalidTimestamp
}
