using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Modinator.Views;

// Lists items from the last Forge scan for the Item Dupe pickers.
// Returns the picked address via PickedAddress, or null on cancel.
public partial class CloneSourcePickerDialog : Window
{
    public int? PickedAddress { get; private set; }

    private readonly List<Row> _all;
    private readonly int _excludeAddress;
    private bool _suppressFilterEvent;

    internal CloneSourcePickerDialog(
        int excludeAddress = 0,
        string? titleOverride = null,
        string? promptOverride = null,
        string? okButtonOverride = null)
    {
        InitializeComponent();
        _excludeAddress = excludeAddress;
        if (titleOverride != null)    Title = titleOverride;
        if (promptOverride != null)   LblPrompt.Text = promptOverride;
        if (okButtonOverride != null) BtnOk.Content  = okButtonOverride;
        _all = BuildRows();
        PopulateCombos();
        ApplyAllFilters();
        TxtFilter.Focus();
    }

    // Walk the entire forge snapshot, re-read each item fresh so the
    // stats shown match memory right now, and skip the opposite side so
    // the same address cannot be picked twice.
    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        int size = Marshal.SizeOf(typeof(ItemNative));
        var snap = ForgeViewerView.LastSnapshot;

        foreach (var s in snap)
        {
            if (s.Address == _excludeAddress) continue;
            try
            {
                byte[] data = Base.Instance.ReadMemory(s.Address, size);
                var native = Base.Push<ItemNative>(data);
                var user = Base.ItemToUser(native);
                string name = Base.ReadUni<ItemNative>(s.Address, "EquipmentName") ?? "";
                if (string.IsNullOrWhiteSpace(name)) name = s.Name;
                if (string.IsNullOrWhiteSpace(name)) name = "(unnamed)";

                rows.Add(new Row
                {
                    Address = s.Address,
                    AddressText = Base.AddressToString(s.Address),
                    Quality = QualityDisplay.Name(user.Quality2),
                    QualityRank = QualityRank(user.Quality2),
                    Name = name,
                    Forger = s.ForgerName ?? "",
                    Type = user.EquipmentType.ToString(),
                    TypeEnum = user.EquipmentType,
                    Level = user.Level,
                    IsHero = s.IsHero,
                });
            }
            catch { /* skip unreadable entries */ }
        }
        return rows;
    }

    // ── Combo setup ───────────────────────────────────────────────

    private void PopulateCombos()
    {
        _suppressFilterEvent = true;

        // Source: All / Forge box / Hero-equipped (the snapshot tags each
        // item with its origin).
        CboSource.Items.Add(new SourceEntry(null,  "All sources"));
        CboSource.Items.Add(new SourceEntry(false, "Forge"));
        CboSource.Items.Add(new SourceEntry(true,  "Hero"));
        CboSource.SelectedIndex = 0;

        // Type: "All" + every distinct type present in the snapshot so
        // the user isn't presented with types they don't actually own.
        CboType.Items.Add(new TypeEntry(null, "All types"));
        foreach (var t in _all.Select(r => r.TypeEnum).Distinct().OrderBy(t => t.ToString()))
            CboType.Items.Add(new TypeEntry(t, t.ToString()));
        CboType.SelectedIndex = 0;

        // Quality: a min-rank ("X & up") threshold for EVERY tier, built
        // from the enum so new tiers can never be missing again (the old
        // hardcoded list stopped at Ultimate and lacked Ultimate 93/+/++).
        CboQuality.Items.Add(new QualityEntry(-1, "Any quality"));
        foreach (Quality2 q in Enum.GetValues(typeof(Quality2))
                                   .Cast<Quality2>()
                                   .OrderByDescending(QualityRank))
        {
            int rank = QualityRank(q);
            string label = QualityDisplay.Name(q) + (rank >= QualityRank(Quality2.UltimatePlusPlus) ? "" : " & up");
            CboQuality.Items.Add(new QualityEntry(rank, label));
        }
        CboQuality.SelectedIndex = 0;

        // Sort: quality desc by default, then common alternatives.
        CboSort.Items.Add(new SortEntry(SortMode.QualityDesc,  "Quality (best first)"));
        CboSort.Items.Add(new SortEntry(SortMode.LevelDesc,    "Level (high to low)"));
        CboSort.Items.Add(new SortEntry(SortMode.NameAsc,      "Name (A-Z)"));
        CboSort.Items.Add(new SortEntry(SortMode.TypeAsc,      "Type (A-Z)"));
        CboSort.SelectedIndex = 0;

        _suppressFilterEvent = false;
    }

    // ── Filtering / sorting ───────────────────────────────────────

    private void ApplyAllFilters()
    {
        string needle = (TxtFilter.Text ?? "").Trim();
        EquipmentType? typeFilter = (CboType.SelectedItem as TypeEntry)?.Type;
        int qualityMin = (CboQuality.SelectedItem as QualityEntry)?.MinRank ?? -1;
        SortMode mode = (CboSort.SelectedItem as SortEntry)?.Mode ?? SortMode.QualityDesc;

        bool? heroFilter = (CboSource.SelectedItem as SourceEntry)?.Hero;
        IEnumerable<Row> q = _all;
        if (heroFilter is bool h) q = q.Where(r => r.IsHero == h);
        if (typeFilter is EquipmentType t) q = q.Where(r => r.TypeEnum == t);
        if (qualityMin >= 0) q = q.Where(r => r.QualityRank >= qualityMin);
        if (needle.Length > 0)
        {
            q = q.Where(r =>
                (r.Name ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (r.Forger ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (r.Type ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (r.Quality ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var list = q.ToList();
        switch (mode)
        {
            case SortMode.LevelDesc:
                list.Sort((a, b) => b.Level.CompareTo(a.Level)); break;
            case SortMode.NameAsc:
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)); break;
            case SortMode.TypeAsc:
                list.Sort((a, b) =>
                {
                    int c = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                    return b.QualityRank.CompareTo(a.QualityRank);
                });
                break;
            default:
                list.Sort((a, b) =>
                {
                    int c = b.QualityRank.CompareTo(a.QualityRank);
                    if (c != 0) return c;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                break;
        }

        Lv.ItemsSource = list;
        LblCount.Text = $"{list.Count} of {_all.Count} shown";
    }

    // ── Event handlers ────────────────────────────────────────────

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyAllFilters();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvent) return;
        ApplyAllFilters();
    }

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilterEvent = true;
        TxtFilter.Text = "";
        CboSource.SelectedIndex = 0;
        CboType.SelectedIndex = 0;
        CboQuality.SelectedIndex = 0;
        CboSort.SelectedIndex = 0;
        _suppressFilterEvent = false;
        ApplyAllFilters();
    }

    private void Lv_DoubleClick(object sender, MouseButtonEventArgs e) => BtnOk_Click(sender, null!);

    private void BtnOk_Click(object sender, RoutedEventArgs? e)
    {
        if (Lv.SelectedItem is not Row r)
        {
            MessageBox.Show(this, "Pick an item first.", "Item Dupe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PickedAddress = r.Address;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ── Helpers ──────────────────────────────────────────────────

    private static int QualityRank(Quality2 q) => QualityDisplay.Rank(q);

    // ── Data types ───────────────────────────────────────────────

    private class Row
    {
        public int Address { get; set; }
        public string AddressText { get; set; } = "";
        public string Quality { get; set; } = "";
        public int QualityRank { get; set; }
        public string Name { get; set; } = "";
        public string Forger { get; set; } = "";
        public string Type { get; set; } = "";
        public EquipmentType TypeEnum { get; set; }
        public int Level { get; set; }
        public bool IsHero { get; set; }
    }

    // null Hero = "All sources"; false = Forge box; true = Hero-equipped.
    private class SourceEntry
    {
        public bool? Hero;
        public string Label;
        public SourceEntry(bool? hero, string label) { Hero = hero; Label = label; }
        public override string ToString() => Label;
    }

    private class TypeEntry
    {
        public EquipmentType? Type;
        public string Label;
        public TypeEntry(EquipmentType? type, string label) { Type = type; Label = label; }
        public override string ToString() => Label;
    }

    private class QualityEntry
    {
        public int MinRank;
        public string Label;
        public QualityEntry(int minRank, string label) { MinRank = minRank; Label = label; }
        public override string ToString() => Label;
    }

    private enum SortMode { QualityDesc, LevelDesc, NameAsc, TypeAsc }

    private class SortEntry
    {
        public SortMode Mode;
        public string Label;
        public SortEntry(SortMode mode, string label) { Mode = mode; Label = label; }
        public override string ToString() => Label;
    }
}
