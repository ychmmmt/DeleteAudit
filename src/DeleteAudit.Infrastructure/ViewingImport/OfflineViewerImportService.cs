using DeleteAudit.Application.Importing;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Importing;
using DeleteAudit.Infrastructure.Importing.Output;
using DeleteAudit.Infrastructure.Importing.Persistence;
using DeleteAudit.Infrastructure.Importing.Reporting;
using DeleteAudit.Infrastructure.Importing.Sources;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.ViewingImport;

public sealed class OfflineViewerImportService : IOfflineViewerImportService
{
    private const long DefaultMaximumFileSizeBytes = 67_108_864;
    private const int DefaultSchemaVersion = 2;

    private readonly ViewerDataLocation _location;
    private readonly OfflineImportOptions _importOptions;
    private readonly string _applicationVersion;
    private readonly CorrelationOptions _correlationOptions;
    private readonly AuditRiskOptions _riskOptions;
    private readonly IReadOnlyList<ProtectedPathRule> _protectedRules;
    private readonly IReadOnlyList<IOfflineEventSource>? _sources;
    private readonly IImportJsonlWriter _jsonlWriter;
    private readonly TimeProvider _timeProvider;

    public OfflineViewerImportService(ViewerDataLocation location)
        : this(
            location,
            CreateDefaultImportOptions(location),
            // One shared constant, so the offline and live paths can never stamp
            // two different application versions from the same build.
            ApplicationVersionInfo.Current,
            new CorrelationOptions(TimeSpan.FromSeconds(3)),
            new AuditRiskOptions(TimeSpan.FromSeconds(10), 30, 100))
    {
    }

    public OfflineViewerImportService(
        ViewerDataLocation location,
        OfflineImportOptions importOptions,
        string applicationVersion,
        CorrelationOptions correlationOptions,
        AuditRiskOptions riskOptions,
        IEnumerable<ProtectedPathRule>? protectedRules = null,
        IEnumerable<IOfflineEventSource>? sources = null,
        IImportJsonlWriter? jsonlWriter = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        ArgumentNullException.ThrowIfNull(correlationOptions);
        ArgumentNullException.ThrowIfNull(riskOptions);

        importOptions.Validate();
        correlationOptions.Validate();
        riskOptions.Validate();
        location.EnsureContains(importOptions.JsonlOutputDirectory);

        _location = location;
        _importOptions = importOptions;
        _applicationVersion = applicationVersion;
        _correlationOptions = correlationOptions;
        _riskOptions = riskOptions;
        _protectedRules = (protectedRules ?? []).ToArray();
        _sources = sources?.ToArray();
        _jsonlWriter = jsonlWriter ?? new FileImportJsonlWriter();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ImportResult> ImportAsync(
        string inputFilePath,
        bool networkPathConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        // Runs before the viewer database is opened, so an unconfirmed network
        // path creates no database, writes no output and reaches no share.
        var validationDiagnostic = ValidateInputPath(inputFilePath, networkPathConfirmed);
        if (validationDiagnostic is not null)
        {
            return Failure(validationDiagnostic);
        }

        var databasePath = _location.EnsureDatabasePath();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return Failure(new ImportDiagnostic(
                "viewer_database_unavailable",
                exception.Message,
                ImportDiagnosticSeverity.Error,
                "persist"));
        }

        using var repository = new SqliteOfflineImportRepository(
            connection,
            timeProvider: _timeProvider);
        var pipeline = new OfflineImportPipeline(
            repository,
            _jsonlWriter,
            _correlationOptions,
            _riskOptions,
            _protectedRules,
            _sources,
            _timeProvider);
        var request = new ImportRequest(
            Path.GetFullPath(inputFilePath),
            _importOptions.MaximumFileSizeBytes,
            _importOptions.JsonlOutputDirectory,
            _applicationVersion,
            _importOptions.SchemaVersion)
        {
            // Carried into the pipeline so the validator can refuse an
            // unconfirmed share again, immediately before it would touch it.
            NetworkPathConfirmed = networkPathConfirmed
        };

        return await pipeline
            .ImportAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ImportDiagnostic? ValidateInputPath(
        string inputFilePath,
        bool networkPathConfirmed)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            return Diagnostic(
                "invalid_import_request",
                "An explicit input file path is required.");
        }

        if (!Path.IsPathFullyQualified(inputFilePath))
        {
            return Diagnostic(
                "invalid_import_request",
                "The input file path must be fully qualified.");
        }

        // Classified from the text as selected, before normalization, so the
        // decision cannot depend on anything the filesystem says.
        var networkDiagnostic = NetworkConfirmationDiagnostic(
            inputFilePath,
            networkPathConfirmed);
        if (networkDiagnostic is not null)
        {
            return networkDiagnostic;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(inputFilePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Diagnostic("invalid_input_path", exception.Message);
        }

        // Re-checked after normalization: a path that only becomes a share once
        // it is expanded must not slip past the first check.
        networkDiagnostic = NetworkConfirmationDiagnostic(
            normalizedPath,
            networkPathConfirmed);
        if (networkDiagnostic is not null)
        {
            return networkDiagnostic;
        }

        // A drive letter says nothing about whether a file is a legitimate offline log,
        // so no volume is rejected on its letter alone. A mapped network drive is a
        // drive letter and is therefore treated as any other local path. Device-namespace
        // paths, alternate data streams, reparse points, non-regular files and unsupported
        // formats are all still rejected downstream by OfflineInputFileValidator.
        var extension = Path.GetExtension(normalizedPath);
        if (!string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".evtx", StringComparison.OrdinalIgnoreCase))
        {
            return Diagnostic(
                "unsupported_file_extension",
                "Only a single .xml or .evtx file can be imported.");
        }

        return null;
    }

    /// <summary>
    /// Returns a diagnostic when <paramref name="path"/> names a plain UNC share
    /// that this import was not explicitly authorised to read, and null in every
    /// other case. Device paths are not handled here: they are rejected outright
    /// by <see cref="OfflineInputFileValidator"/> and are never confirmable.
    /// </summary>
    private static ImportDiagnostic? NetworkConfirmationDiagnostic(
        string path,
        bool networkPathConfirmed) =>
        !networkPathConfirmed
        && InputPathClassifier.Classify(path) == InputPathKind.NetworkShare
            ? Diagnostic(
                "network_path_confirmation_required",
                "Reading from a network share requires an explicit confirmation for this import.")
            : null;

    private static OfflineImportOptions CreateDefaultImportOptions(
        ViewerDataLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new OfflineImportOptions(
            DefaultMaximumFileSizeBytes,
            location.JsonlOutputDirectory,
            DefaultSchemaVersion);
    }

    private static ImportResult Failure(ImportDiagnostic diagnostic)
    {
        var report = ImportReportBuilder.Build(
            null,
            0,
            0,
            [],
            0,
            [],
            [],
            [diagnostic]);
        return new ImportResult(
            ImportStatus.Failed,
            null,
            report,
            false,
            null,
            null);
    }

    private static ImportDiagnostic Diagnostic(string code, string message) =>
        new(
            code,
            message,
            ImportDiagnosticSeverity.Error,
            "source_validation");
}
