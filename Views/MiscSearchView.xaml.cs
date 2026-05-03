using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class MiscSearchView : UserControl
{
    private bool _inSession;

    // Preset hex suffixes keyed by combo index
    private static readonly string[] PresetEndsWith =
    {
        "",           // None
        "44240000",   // Health
        "44240000",   // Mana Bank
        "44240000",   // Mana Pool
        "44240000",   // Player Speed
        "44240000",   // Player Damage
        "44240000",   // Player Range
        "44240000",   // Player Cast Speed
        "44240000",   // Player Jump
        "44240000",   // Tower Damage
        "44240000",   // Tower Range
        "44240000",   // Tower Health
    };

    public MiscSearchView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CboPresets.Items.Clear();
        CboPresets.Items.Add("None");
        CboPresets.Items.Add("Health");
        CboPresets.Items.Add("Mana Bank");
        CboPresets.Items.Add("Mana Pool");
        CboPresets.Items.Add("Player Speed");
        CboPresets.Items.Add("Player Damage");
        CboPresets.Items.Add("Player Range");
        CboPresets.Items.Add("Player Cast Speed");
        CboPresets.Items.Add("Player Jump");
        CboPresets.Items.Add("Tower Damage");
        CboPresets.Items.Add("Tower Range");
        CboPresets.Items.Add("Tower Health");
        CboPresets.SelectedIndex = 0;
    }

    private void CboPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboPresets.SelectedIndex < 0 || TxtEndsWith == null) return;

        if (CboPresets.SelectedIndex < PresetEndsWith.Length)
        {
            TxtEndsWith.Text = PresetEndsWith[CboPresets.SelectedIndex];
        }
    }

    private void BtnFirstScan_Click(object sender, RoutedEventArgs e)
    {
        if (!Base.OpenProcess()) return;

        _inSession = false;
        ScanStatus.Text = "Scanning...";

        decimal.TryParse(TxtSearchValue.Text, out decimal value);
        bool endsWithHasValue = !string.IsNullOrWhiteSpace(TxtEndsWith.Text);

        if (endsWithHasValue)
        {
            int.TryParse(TxtEndsWith.Text, NumberStyles.HexNumber, null, out int endsWithVal);
            Base.CreateMiscMask(value, true);
        }
        else
        {
            Base.CreateMiscMask(value, false);
        }

        int step = endsWithHasValue ? 8 : 4;
        Base.RunFirstScan(0, step, OnFail, OnSuccess, ref Base.MiscResults);

        // Track whether results are float or int
        bool isFloat = ChkFloat.IsChecked == true;
        Base.MiscFloatTracks.Clear();
        foreach (var _ in Base.MiscResults)
        {
            Base.MiscFloatTracks.Add(isFloat);
        }
    }

    private void BtnNextScan_Click(object sender, RoutedEventArgs e)
    {
        if (!Base.OpenProcess()) return;
        ScanStatus.Text = "Scanning...";

        decimal.TryParse(TxtSearchValue.Text, out decimal value);
        bool endsWithHasValue = !string.IsNullOrWhiteSpace(TxtEndsWith.Text);

        Base.CreateMiscMask(value, endsWithHasValue);
        Base.RunNextScan(OnFail, OnSuccess, ref Base.MiscResults);

        // Sync float tracks with results
        bool isFloat = ChkFloat.IsChecked == true;
        Base.MiscFloatTracks.Clear();
        foreach (var _ in Base.MiscResults)
        {
            Base.MiscFloatTracks.Add(isFloat);
        }
    }

    private void BtnViewGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnSuccess()
    {
        _inSession = true;
        BtnNextScan.IsEnabled = true;
        ScanStatus.Text = $"Found {Base.MiscResults.Count:N0} results";
        Base.RaiseResultsChanged(Base.MiscResults);
    }

    private void OnFail()
    {
        _inSession = false;
        BtnNextScan.IsEnabled = false;
        ScanStatus.Text = "No results";
        Base.MiscResults.Clear();
        Base.MiscFloatTracks.Clear();
        Base.RaiseResultsChanged(Base.MiscResults);
    }

    private void BtnNewScan_Click(object sender, RoutedEventArgs e)
    {
        TxtSearchValue.Text = "";
        TxtEndsWith.Text = "";
        ChkFloat.IsChecked = false;
        if (CboPresets.Items.Count > 0) CboPresets.SelectedIndex = 0;

        _inSession = false;
        BtnNextScan.IsEnabled = false;
        Base.MiscResults.Clear();
        Base.MiscFloatTracks.Clear();
        Base.RaiseResultsChanged(Base.MiscResults);
        ScanStatus.Text = "Enter search values and click First Scan";
    }
}
