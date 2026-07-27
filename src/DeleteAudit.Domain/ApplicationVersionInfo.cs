namespace DeleteAudit.Domain;

/// <summary>
/// The single source of truth for the application version this build stamps into
/// the records it produces.
/// </summary>
/// <remarks>
/// <para>
/// This is a build identity — "which version of DeleteAudit wrote this record" — and
/// nothing else. It is deliberately separate from the storage schema version and from
/// the JSONL/manifest format version, which describe the shape of the data rather than
/// the application that produced it, and which change on their own schedule.
/// </para>
/// <para>
/// Both the offline import path and the live monitoring path read this one constant,
/// so a single build can never stamp two different versions on its own output.
/// </para>
/// </remarks>
public static class ApplicationVersionInfo
{
    /// <summary>
    /// The version recorded for anything this build produces.
    /// </summary>
    public const string Current = "0.1.0-alpha";
}
