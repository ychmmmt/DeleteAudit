using System.Windows;

namespace DeleteAudit.Viewer;

/// <summary>
/// Per-import confirmation shown before anything reads from a network share.
/// "取消" is both the default button and the Escape action, so dismissing the
/// dialog any way at all declines.
/// </summary>
public partial class NetworkPathConfirmationWindow : Window
{
    public NetworkPathConfirmationWindow(string networkPath)
    {
        InitializeComponent();
        NetworkPathText.Text = networkPath;
    }

    private void OnContinueClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
