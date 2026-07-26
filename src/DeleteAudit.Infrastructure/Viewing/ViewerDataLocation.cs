using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Viewing;

public sealed class ViewerDataLocation
{
    /// <summary>
    /// The controlled viewer data root, anchored to the resolved repository root rather
    /// than to any developer machine. See <see cref="RepositoryRoot"/>.
    /// </summary>
    public static string DefaultRoot { get; } =
        Path.Combine(RepositoryRoot.ArtifactsDirectory, "viewer-data");

    public static string DefaultDatabasePath { get; } =
        Path.Combine(DefaultRoot, "deleteaudit.db");

    public static string DefaultJsonlDirectory { get; } =
        Path.Combine(DefaultRoot, "jsonl");

    private ViewerDataLocation(string databasePath, string jsonlOutputDirectory)
    {
        DatabasePath = ValidateDescendant(databasePath, nameof(databasePath));
        JsonlOutputDirectory = ValidateDescendant(
            jsonlOutputDirectory,
            nameof(jsonlOutputDirectory));
    }

    public static ViewerDataLocation Default { get; } =
        new(DefaultDatabasePath, DefaultJsonlDirectory);

    public string DatabasePath { get; }

    public string JsonlOutputDirectory { get; }

    public static ViewerDataLocation CreateDefault() => Default;

    public static ViewerDataLocation CreateForTesting(
        string databasePath,
        string jsonlOutputDirectory) =>
        new(databasePath, jsonlOutputDirectory);

    public string EnsureContains(string path)
    {
        var validatedPath = ValidateDescendant(path, nameof(path));
        if (!string.Equals(
                validatedPath,
                JsonlOutputDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The output path must match this viewer data location.",
                nameof(path));
        }

        return validatedPath;
    }

    public string EnsureDatabasePath() =>
        ValidateDescendant(DatabasePath, nameof(DatabasePath));

    public SqliteConnection CreateReadOnlyConnection()
    {
        var databasePath = EnsureDatabasePath();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        return new SqliteConnection(connectionString.ToString());
    }

    private static string ValidateDescendant(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A fully qualified path is required.", parameterName);
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be fully qualified.", parameterName);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultRoot));
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(root, fullPath);
        var parentPrefix = $"..{Path.DirectorySeparatorChar}";
        if (string.Equals(relativePath, ".", StringComparison.Ordinal)
            || string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.StartsWith(parentPrefix, StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException(
                $"The path must be inside '{DefaultRoot}'.",
                parameterName);
        }

        EnsureNoExistingReparsePoints(fullPath, parameterName);
        return fullPath;
    }

    private static void EnsureNoExistingReparsePoints(
        string fullPath,
        string parameterName)
    {
        var projectRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(RepositoryRoot.Value));
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException(
                        "Viewer data paths cannot traverse a reparse point.",
                        parameterName);
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (string.Equals(current, projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }
}
