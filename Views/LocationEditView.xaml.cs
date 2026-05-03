using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Modinator.Views;

public partial class LocationEditView : UserControl
{
    private int Address;
    private DispatcherTimer _timer;

    public LocationEditView(int address)
    {
        InitializeComponent();
        Address = address;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (s, e) => ShowDetails();
        Loaded += (s, e) => { ShowDetails(); _timer.Start(); };
        Unloaded += (s, e) => _timer.Stop();
    }

    private void ShowDetails()
    {
        try
        {
            byte[] data = Base.Instance.ReadMemory(Address, 12);
            float x = BitConverter.ToSingle(data, 0);
            float y = BitConverter.ToSingle(data, 4);
            float z = BitConverter.ToSingle(data, 8);
            TxtCurX.Text = x.ToString("N0");
            TxtCurY.Text = y.ToString("N0");
            TxtCurZ.Text = z.ToString("N0");
        }
        catch { }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => ShowDetails();

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateBackFromEditor();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var v = new FieldValidator();
        float x = v.Float(TxtNewX, "X");
        float y = v.Float(TxtNewY, "Y");
        float z = v.Float(TxtNewZ, "Z");

        if (!v.IsValid)
        {
            StatusText.Text = "Invalid: " + v.Report();
            v.FocusFirstError();
            return;
        }

        try
        {
            byte[] data = new byte[12];
            Array.Copy(BitConverter.GetBytes(x), 0, data, 0, 4);
            Array.Copy(BitConverter.GetBytes(y), 0, data, 4, 4);
            Array.Copy(BitConverter.GetBytes(z), 0, data, 8, 4);
            Base.Instance.WriteMemory(Address, data);
            StatusText.Text = "Updated!";
        }
        catch { Base.RaiseMessage("Failed to write location.", "Error"); }
    }
}
