using System.Windows;
using DeleteAudit.Application.Presentation;

namespace DeleteAudit.Viewer;

public sealed class WpfRawXmlPreviewClipboard : IRawXmlPreviewClipboard
{
    public void SetPreviewText(string previewText)
    {
        ArgumentNullException.ThrowIfNull(previewText);
        Clipboard.SetText(previewText);
    }
}
