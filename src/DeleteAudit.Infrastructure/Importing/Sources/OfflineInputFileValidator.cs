using System.Security.Cryptography;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing.Sources;

public sealed record OfflineInputFileOpenResult(
    ValidatedOfflineFile? File,
    ImportDiagnostic? Diagnostic)
{
    public bool IsSuccess => File is not null && Diagnostic is null;
}

public static class OfflineInputFileValidator
{
    private const int BufferSize = 64 * 1024;

    public static async Task<OfflineInputFileOpenResult> TryOpenAsync(
        ImportRequest request,
        string expectedExtension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            request.Validate();
        }
        catch (ArgumentException exception)
        {
            return Failure("invalid_import_request", exception.Message);
        }

        if (expectedExtension is not ".xml" and not ".evtx")
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedExtension),
                expectedExtension,
                "Only the .xml and .evtx offline formats are supported.");
        }

        var requestedPathDiagnostic = ClassifyPath(
            request.InputFilePath,
            request.NetworkPathConfirmed);
        if (requestedPathDiagnostic is not null)
        {
            return new OfflineInputFileOpenResult(null, requestedPathDiagnostic);
        }

        string absolutePath;
        try
        {
            absolutePath = Path.GetFullPath(request.InputFilePath);
        }
        catch (ArgumentException exception)
        {
            return Failure("invalid_input_path", exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return Failure("invalid_input_path", exception.Message);
        }
        catch (PathTooLongException exception)
        {
            return Failure("invalid_input_path", exception.Message);
        }

        var absolutePathDiagnostic = ClassifyPath(
            absolutePath,
            request.NetworkPathConfirmed);
        if (absolutePathDiagnostic is not null)
        {
            return new OfflineInputFileOpenResult(null, absolutePathDiagnostic);
        }

        if (!string.Equals(
                Path.GetExtension(absolutePath),
                expectedExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "unsupported_file_extension",
                $"This source accepts only {expectedExtension} files.");
        }

        if (ContainsAlternateDataStream(absolutePath))
        {
            return Failure(
                "alternate_data_stream_rejected",
                "Alternate data streams are not valid offline event input files.");
        }

        var pathDiagnostic = ValidatePathComponents(absolutePath);
        if (pathDiagnostic is not null)
        {
            return new OfflineInputFileOpenResult(null, pathDiagnostic);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                absolutePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
        }
        catch (FileNotFoundException exception)
        {
            return Failure("input_file_not_found", exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return Failure("input_file_not_found", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure("input_file_access_denied", exception.Message);
        }
        catch (IOException exception)
        {
            return Failure("input_file_open_failed", exception.Message);
        }

        try
        {
            var openedAttributes = File.GetAttributes(stream.SafeFileHandle);
            if (HasUnsafeFileAttributes(openedAttributes))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return Failure(
                    "not_regular_file",
                    "The opened input handle does not refer to a regular file.");
            }

            if (stream.Length > request.MaximumFileSizeBytes)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return Failure(
                    "input_file_too_large",
                    $"The input file exceeds the configured {request.MaximumFileSizeBytes} byte limit.");
            }

            var hash = await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            var lastWriteUtc = new DateTimeOffset(
                File.GetLastWriteTimeUtc(stream.SafeFileHandle));
            var snapshot = new OfflineInputFileSnapshot(
                Path.GetFileName(absolutePath),
                absolutePath,
                stream.Length,
                lastWriteUtc,
                hash);

            return new OfflineInputFileOpenResult(
                new ValidatedOfflineFile(stream, snapshot),
                null);
        }
        catch (UnauthorizedAccessException exception)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return Failure("input_file_access_denied", exception.Message);
        }
        catch (IOException exception)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return Failure("input_file_read_failed", exception.Message);
        }
        catch (OperationCanceledException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ImportDiagnostic? ValidatePathComponents(string absolutePath)
    {
        var root = Path.GetPathRoot(absolutePath);
        if (string.IsNullOrEmpty(root))
        {
            return Diagnostic("invalid_input_path", "The input file path has no filesystem root.");
        }

        var components = absolutePath[root.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return Diagnostic("not_regular_file", "A filesystem root is not an input file.");
        }

        var current = root;
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException exception)
            {
                return Diagnostic("input_file_not_found", exception.Message);
            }
            catch (DirectoryNotFoundException exception)
            {
                return Diagnostic("input_file_not_found", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Diagnostic("input_file_access_denied", exception.Message);
            }
            catch (IOException exception)
            {
                return Diagnostic("input_file_metadata_failed", exception.Message);
            }
            catch (ArgumentException exception)
            {
                return Diagnostic("invalid_input_path", exception.Message);
            }
            catch (NotSupportedException exception)
            {
                return Diagnostic("invalid_input_path", exception.Message);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Diagnostic(
                    "reparse_point_rejected",
                    $"The input path contains a reparse point at '{current}'.");
            }

            var isLast = index == components.Length - 1;
            if (!isLast && (attributes & FileAttributes.Directory) == 0)
            {
                return Diagnostic(
                    "invalid_input_path",
                    $"The input path component '{current}' is not a directory.");
            }

            if (isLast
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return Diagnostic(
                    "not_regular_file",
                    "The selected input path is not a regular file.");
            }
        }

        return null;
    }

    /// <summary>
    /// Gate that both path checks run through. It is pure string analysis and sits
    /// ahead of every filesystem call in <see cref="TryOpenAsync"/>, so a device
    /// path is refused and an unauthorised share is refused before anything is
    /// probed, opened or contacted.
    /// </summary>
    private static ImportDiagnostic? ClassifyPath(
        string path,
        bool networkPathConfirmed) =>
        InputPathClassifier.Classify(path) switch
        {
            InputPathKind.DeviceNamespace => Diagnostic(
                "device_path_rejected",
                "Device namespace paths are not valid offline event input files."),
            InputPathKind.NetworkShare when !networkPathConfirmed => Diagnostic(
                "network_path_confirmation_required",
                "Reading from a network share requires an explicit confirmation for this import."),
            _ => null
        };

    private static bool ContainsAlternateDataStream(string absolutePath)
    {
        var root = Path.GetPathRoot(absolutePath);
        return root is not null && absolutePath.AsSpan(root.Length).Contains(':');
    }

    public static bool HasUnsafeFileAttributes(FileAttributes attributes) =>
        (attributes
         & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0;

    private static async Task<string> ComputeHashAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256
            .HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static OfflineInputFileOpenResult Failure(string code, string message) =>
        new(null, Diagnostic(code, message));

    private static ImportDiagnostic Diagnostic(string code, string message) =>
        new(code, message, ImportDiagnosticSeverity.Error, "source_validation");
}

public sealed class ValidatedOfflineFile : IAsyncDisposable
{
    private readonly FileStream _stream;

    internal ValidatedOfflineFile(
        FileStream stream,
        OfflineInputFileSnapshot snapshot)
    {
        _stream = stream;
        Snapshot = snapshot;
    }

    public OfflineInputFileSnapshot Snapshot { get; }

    internal FileStream ReadStream
    {
        get
        {
            _stream.Position = 0;
            return _stream;
        }
    }

    public async Task<ImportDiagnostic?> VerifyUnchangedAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var length = _stream.Length;
            var lastWriteUtc = new DateTimeOffset(
                File.GetLastWriteTimeUtc(_stream.SafeFileHandle));
            var hash = await ComputeHashAsync(_stream, cancellationToken).ConfigureAwait(false);

            if (length != Snapshot.FileSize
                || lastWriteUtc != Snapshot.LastWriteUtc
                || !string.Equals(hash, Snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ImportDiagnostic(
                    "input_changed_during_import",
                    "The input file size, modification time, or content changed during import.",
                    ImportDiagnosticSeverity.Error,
                    "source_validation");
            }

            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            return VerificationFailure(exception.Message);
        }
        catch (IOException exception)
        {
            return VerificationFailure(exception.Message);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private static async Task<string> ComputeHashAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256
            .HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ImportDiagnostic VerificationFailure(string message) =>
        new(
            "input_post_verification_failed",
            message,
            ImportDiagnosticSeverity.Error,
            "source_validation");
}
