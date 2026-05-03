using System.Windows;

namespace Modinator.Views;

public partial class MoreOptionsDialog : Window
{
    public MoreOptionsDialog()
    {
        InitializeComponent();
        TxtFreezeTimer.Text = Base.FreezeTime.ToString();
        TxtRefreshTimer.Text = Base.RenewTime.ToString();
        ChkDisableRefresh.IsChecked = !Base.Renew;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtFreezeTimer.Text, out int ft))
            Base.FreezeTime = Math.Clamp(ft, 5, 1200000);
        if (int.TryParse(TxtRefreshTimer.Text, out int rt))
            Base.RenewTime = Math.Clamp(rt, 5, 1200000);
        Base.Renew = ChkDisableRefresh.IsChecked != true;
        DialogResult = true;
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        TxtFreezeTimer.Text = "10";
        TxtRefreshTimer.Text = "5000";
        ChkDisableRefresh.IsChecked = false;
    }
}
