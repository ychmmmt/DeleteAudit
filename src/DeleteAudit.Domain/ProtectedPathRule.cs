namespace DeleteAudit.Domain;

public sealed record ProtectedPathRule(
    string RuleId,
    string Path,
    bool Enabled = true)
{
    public bool Matches(string? candidatePath)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var root = Normalize(Path);
        var candidate = Normalize(candidatePath);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim().Replace('/', '\\');
        return normalized.Length > 3 ? normalized.TrimEnd('\\') : normalized;
    }
}
