using DeleteAudit.Application.Importing;
using Microsoft.Win32;

namespace DeleteAudit.Viewer;

public sealed class OpenFileDialogOfflineFilePicker : IOfflineFilePicker
{
    public Task<string?> PickSingleFileAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = false,
            Filter = "DeleteAudit 离线事件 (*.xml;*.evtx)|*.xml;*.evtx|XML 文件 (*.xml)|*.xml|EVTX 文件 (*.evtx)|*.evtx",
            Multiselect = false,
            Title = "选择一个 XML 或 EVTX 文件"
        };

        return Task.FromResult(
            dialog.ShowDialog() == true
                ? dialog.FileName
                : null);
    }
}
