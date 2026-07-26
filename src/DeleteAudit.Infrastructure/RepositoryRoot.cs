namespace DeleteAudit.Infrastructure;

/// <summary>
/// Resolves the repository root that anchors every controlled data directory.
///
/// Resolution order:
/// 1. the <c>DELETEAUDIT_REPOSITORY_ROOT</c> environment variable, when set;
/// 2. otherwise the nearest ancestor of <see cref="AppContext.BaseDirectory"/> that
///    contains <c>DeleteAudit.sln</c>.
///
/// If neither succeeds the resolver fails closed with an explicit error. It never falls
/// back to the current working directory, a user profile folder, or any other guess.
/// </summary>
public static class RepositoryRoot
{
    public const string EnvironmentVariableName = "DELETEAUDIT_REPOSITORY_ROOT";

    private const string SolutionFileName = "DeleteAudit.sln";

    private static readonly Lazy<string> LazyValue =
        new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The resolved repository root. Throws if it cannot be determined.</summary>
    public static string Value => LazyValue.Value;

    /// <summary>The controlled artifacts directory beneath the repository root.</summary>
    public static string ArtifactsDirectory => Path.Combine(Value, "artifacts");

    /// <summary>
    /// Resolves the root without caching. Exposed so tests can exercise the resolution
    /// rules directly; production code should use <see cref="Value"/>.
    /// </summary>
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ValidateConfiguredRoot(configured.Trim());
        }

        var discovered = FindSolutionDirectory(AppContext.BaseDirectory);
        if (discovered is not null)
        {
            return discovered;
        }

        throw new InvalidOperationException(
            $"The DeleteAudit repository root could not be resolved. No '{SolutionFileName}' "
            + $"was found above '{AppContext.BaseDirectory}'. Set the "
            + $"'{EnvironmentVariableName}' environment variable to a fully qualified "
            + "local directory.");
    }

    private static string ValidateConfiguredRoot(string configured)
    {
        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException(
                $"'{EnvironmentVariableName}' must be a fully qualified path.");
        }

        if (IsUncOrDevicePath(configured))
        {
            throw new InvalidOperationException(
                $"'{EnvironmentVariableName}' must not be a UNC or device path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"'{EnvironmentVariableName}' is not a usable path: {exception.Message}");
        }

        if (IsUncOrDevicePath(fullPath))
        {
            throw new InvalidOperationException(
                $"'{EnvironmentVariableName}' must not resolve to a UNC or device path.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"'{EnvironmentVariableName}' points to '{fullPath}', which does not exist.");
        }

        return fullPath;
    }

    private static string? FindSolutionDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return Path.TrimEndingDirectorySeparator(current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsUncOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);
}
