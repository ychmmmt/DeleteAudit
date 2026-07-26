namespace DeleteAudit.Domain;

public sealed record OfflineImportOptions(
    long MaximumFileSizeBytes,
    string JsonlOutputDirectory,
    int SchemaVersion)
{
    public void Validate()
    {
        if (MaximumFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileSizeBytes));
        }

        if (string.IsNullOrWhiteSpace(JsonlOutputDirectory)
            || !Path.IsPathFullyQualified(JsonlOutputDirectory))
        {
            throw new ArgumentException(
                "The JSONL output directory must be fully qualified.",
                nameof(JsonlOutputDirectory));
        }

        if (SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        }
    }
}
