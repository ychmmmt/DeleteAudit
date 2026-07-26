using DeleteAudit.Domain;

namespace DeleteAudit.Application.Importing;

public interface IOfflineViewerImportService
{
    /// <summary>
    /// Imports one explicitly selected offline event file.
    /// </summary>
    /// <param name="inputFilePath">A fully qualified single-file path.</param>
    /// <param name="networkPathConfirmed">
    /// One-shot authorisation for this call to read from a network share. It
    /// defaults to <see langword="false"/> so a caller that knows nothing about
    /// this rule cannot reach the network by accident. An unconfirmed UNC path is
    /// rejected with <c>network_path_confirmation_required</c> before any
    /// filesystem or network access.
    /// </param>
    Task<ImportResult> ImportAsync(
        string inputFilePath,
        bool networkPathConfirmed = false,
        CancellationToken cancellationToken = default);
}

public interface IOfflineFilePicker
{
    Task<string?> PickSingleFileAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the user to authorise one import from one network share.
/// </summary>
/// <remarks>
/// Implementations must ask every time and must not remember the answer: the
/// result authorises exactly the path passed in, for exactly one import.
/// </remarks>
public interface INetworkPathImportConfirmation
{
    Task<bool> ConfirmAsync(
        string networkPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fail-closed confirmation used when no interactive surface was supplied.
/// It always declines, so a headless or partially wired caller can never import
/// from a network share by omission.
/// </summary>
public sealed class DeniedNetworkPathImportConfirmation : INetworkPathImportConfirmation
{
    public Task<bool> ConfirmAsync(
        string networkPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
