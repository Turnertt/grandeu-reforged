using System.Windows;

namespace Modinator.Views;

// First-run disclaimer (and re-viewable from Settings → Actions).
// DialogResult == true only when the user pressed I UNDERSTAND; Esc, the
// X button and EXIT all return false so the caller can shut down.
public partial class DisclaimerDialog : Window
{
    public DisclaimerDialog(bool reviewOnly = false)
    {
        InitializeComponent();
        if (reviewOnly)
        {
            // Already accepted — just reading it again.
            BtnExit.Visibility = Visibility.Collapsed;
            BtnAccept.Content = "CLOSE";
            LblFoot.Text = "You accepted this on first launch.";
        }
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
