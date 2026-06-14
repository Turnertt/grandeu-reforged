using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Modinator.Behaviors;

namespace Modinator.Views;

public partial class MiscEditView : UserControl
{
    private int Address;
    private bool IsFloat;

    public MiscEditView(int address, bool isFloat, string name)
    {
        InitializeComponent();
        Address = address;
        IsFloat = isFloat;
        StatusText.Text = "Misc - " + name;
        if (isFloat)
        {
            TxtFloatLabel.Visibility = Visibility.Visible;
            NumericInput.SetMode(TxtValue, NumericMode.Float);
        }
        Loaded += (s, e) => ShowDetails();
    }

    private void ShowDetails()
    {
        // Invariant culture both ways: FieldValidator parses with
        // InvariantCulture, so formatting with the OS culture (e.g. German
        // "1.234,5") would make an untouched value fail validation on UPDATE.
        try
        {
            byte[] data = Base.Instance.ReadMemory(Address, 4);
            if (IsFloat)
                TxtValue.Text = BitConverter.ToSingle(data, 0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            else
                TxtValue.Text = BitConverter.ToInt32(data, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { Base.RaiseMessage("Failed to read value.", "Error"); }
    }

    // Enter applies — this is the single-field editor, so the keyboard flow
    // "type value, press Enter" should just work.
    private void TxtValue_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            BtnUpdate_Click(sender, e);
            e.Handled = true;
        }
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
        byte[] bytes;
        if (IsFloat)
        {
            float fv = v.Float(TxtValue, "Value");
            if (!v.IsValid) { StatusText.Text = "Invalid: " + v.Report(); v.FocusFirstError(); return; }
            bytes = BitConverter.GetBytes(fv);
        }
        else
        {
            int iv = v.Int(TxtValue, "Value");
            if (!v.IsValid) { StatusText.Text = "Invalid: " + v.Report(); v.FocusFirstError(); return; }
            bytes = BitConverter.GetBytes(iv);
        }

        try
        {
            Base.Instance.WriteMemory(Address, bytes);
            StatusText.Text = "Updated!";
        }
        catch { Base.RaiseMessage("Failed to write value.", "Error"); }
    }
}
