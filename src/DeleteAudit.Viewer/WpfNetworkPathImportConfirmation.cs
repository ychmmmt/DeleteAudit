using System.Windows;
using DeleteAudit.Application.Importing;

namespace DeleteAudit.Viewer;

/// <summary>
/// Shows <see cref="NetworkPathConfirmationWindow"/> once per import attempt.
/// Nothing about the answer is kept: selecting the same share again asks again.
/// </summary>
public sealed class WpfNetworkPathImportConfirmation : INetworkPathImportConfirmation
{
    private readonly Window _owner;

    public WpfNetworkPathImportConfirmation(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public Task<bool> ConfirmAsync(
        string networkPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = new NetworkPathConfirmationWindow(networkPath)
        {
            Owner = _owner
        };

        return Task.FromResult(dialog.ShowDialog() == true);
    }
}
