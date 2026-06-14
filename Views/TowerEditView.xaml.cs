using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class TowerEditView : UserControl
{
    private int Address;
    private string TowerDisplayName;
    private TowerNative _lastNative;

    public TowerEditView(int address, string name)
    {
        InitializeComponent();
        Address = address;
        TowerDisplayName = string.IsNullOrWhiteSpace(name) ? "Tower" : name;
        StatusText.Text = Base.Truncate(TowerDisplayName);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            int size = Marshal.SizeOf(typeof(TowerNative));
            byte[] data = Base.Instance.ReadMemory(Address, size);
            TowerNative native = Base.Push<TowerNative>(data);
            _lastNative = native;
            TowerUser user = Base.TowerToUser(native);

            // HP
            TxtCurrentHP.Text = user.CurrentHP.ToString();
            TxtMaxHP.Text = user.MaxHP.ToString();

            // Attack — invariant culture so the value round-trips through
            // FieldValidator (which parses invariant) on any OS locale.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            TxtAttackDamage.Text = user.AttackDamage.ToString("G", inv);
            TxtAttackRate.Text = user.AttackRate.ToString("G", inv);
            TxtAttackRange.Text = user.AttackRange.ToString("G", inv);
            TxtAttackArc.Text = user.AttackArc.ToString("G", inv);

            // Upgrades
            TxtUpgradeLevel.Text = user.UpgradeLevel.ToString("G", inv);
            TxtMaxUpgrades.Text = user.MaxUpgrades.ToString();

            StatusText.Text = $"{Base.Truncate(TowerDisplayName)} — refreshed";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Read error: " + ex.Message;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateBackFromEditor();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var v = new FieldValidator();
        TowerUser user = new TowerUser();

        user.CurrentHP = v.Int(TxtCurrentHP, "Current HP");
        user.MaxHP = v.Int(TxtMaxHP, "Max HP");
        user.AttackDamage = v.Float(TxtAttackDamage, "Attack Damage");
        user.AttackRate = v.Float(TxtAttackRate, "Attack Rate");
        user.AttackRange = v.Float(TxtAttackRange, "Attack Range");
        user.AttackArc = v.Float(TxtAttackArc, "Attack Arc");
        user.UpgradeLevel = v.Float(TxtUpgradeLevel, "Upgrade Level");
        user.MaxUpgrades = v.Int(TxtMaxUpgrades, "Max Upgrades");

        if (!v.IsValid)
        {
            StatusText.Text = "Invalid: " + v.Report();
            v.FocusFirstError();
            return;
        }

        try
        {
            // Convert to native preserving reserved padding
            TowerNative native = Base.TowerToNative(user, _lastNative);

            byte[] bytes = Base.Push(native);
            Base.Instance.WriteMemory(Address, bytes);

            StatusText.Text = $"{Base.Truncate(TowerDisplayName)} — updated";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Write error: " + ex.Message;
        }
    }
}
