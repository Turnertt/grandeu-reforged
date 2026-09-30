using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Modinator.Views;

// A Forge-Viewer-style list of item cards with single selection: search,
// type / sort filters (plus source and folder when the list has them), the
// shared card, and paging. It owns no game access — the host hands it
// ItemCardData and reacts to SelectionChanged / Activated.
public partial class ItemBrowser : UserControl
{
    private const int PageSize = 30;
    private const string AllEntry = "\u0001all";

    private sealed record FacetEntry(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private List<ItemCardData> _all = new();
    private List<ItemCardData> _visible = new();
    private int _page;
    private bool _suppress;
    private string? _selectedKey;
    private string _note = "";
    private string _emptyText = "Nothing to show.";
    private string _emptyGlyph = "\uE71C";
    // Rebuilding 30 cards per keystroke is visibly laggy; wait for a pause.
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // Raised when the selected card changes (including to none).
    internal event Action? SelectionChanged;
    // Raised on a double-click; the card is already selected.
    internal event Action<ItemCardData>? Activated;

    public ItemBrowser()
    {
        InitializeComponent();
        _suppress = true;
        ItemCard.FillTypeCombo(CboType);
        ItemCard.FillSortCombo(CboSort);
        _suppress = false;
        _searchDebounce.Tick += (s, e) =>
        {
            _searchDebounce.Stop();
            _page = 0;
            PopulateCards(scrollToTop: true);
        };
        UpdateEmptyState();
    }

    // Host buttons go here (right side of the command bar).
    internal Panel Actions => ActionsHost;

    internal ItemCardData? Selected
        => _selectedKey == null ? null : _all.FirstOrDefault(i => i.Key == _selectedKey);

    internal void Configure(string badge, string glyph, string searchHint, string hint, string emptyText)
    {
        BadgeLabel.Text = badge;
        BadgeIcon.Text = glyph;
        SearchHint.Text = searchHint;
        HintLine.Text = hint;
        _emptyGlyph = glyph;
        _emptyText = emptyText;
        UpdateEmptyState();
    }

    // Adds "Newest first" for lists whose entries carry a saved date.
    internal void AddNewestSort(bool select) => AddSort(ItemSort.Newest, "Newest first", select);

    // Puts an extra sort at the top of the Sort combo (once).
    internal void AddSort(ItemSort mode, string label, bool select)
    {
        foreach (var item in CboSort.Items)
            if (item is ItemCard.SortEntry existing && existing.Mode == mode) return;
        _suppress = true;
        CboSort.Items.Insert(0, new ItemCard.SortEntry(mode, label));
        if (select) CboSort.SelectedIndex = 0;
        _suppress = false;
    }

    // Replaces the list. The selection survives when its key is still
    // present; keepView also keeps the page and the scroll position (live
    // refreshes must not yank the list around under the user).
    internal void SetItems(IReadOnlyList<ItemCardData> items, string note = "", bool keepView = false)
    {
        _all = items.ToList();
        _note = note ?? "";
        string? before = _selectedKey;
        if (_selectedKey != null && !_all.Any(i => i.Key == _selectedKey)) _selectedKey = null;
        RebuildFacets();
        if (!keepView) _page = 0;
        PopulateCards(scrollToTop: !keepView);
        if (before != _selectedKey) SelectionChanged?.Invoke();
    }

    // A status line that isn't a count: "Reading…", or why the list is empty.
    internal void SetMessage(string text)
    {
        LblStatus.Text = text;
        if (CardPanel.Children.Count == 0) EmptyText.Text = text;
    }

    // null hides the bar.
    internal void SetProgress(double? fraction)
    {
        ScanBar.IsIndeterminate = false;
        ScanBar.Visibility = fraction == null ? Visibility.Collapsed : Visibility.Visible;
        if (fraction is double f) ScanBar.Value = Math.Clamp(f, 0, 1);
    }

    // A moving bar plus a status line for work with no measurable progress.
    internal void SetWorking(string message)
    {
        ScanBar.IsIndeterminate = true;
        ScanBar.Visibility = Visibility.Visible;
        LblStatus.Text = message;
    }

    internal void Select(string? key)
    {
        if (key == _selectedKey) return;
        _selectedKey = key != null && _all.Any(i => i.Key == key) ? key : null;
        foreach (var child in CardPanel.Children)
            if (child is Border card && card.Tag is ItemCardData d)
                ItemCard.SetSelected(card, d.Key == _selectedKey);
        SelectionChanged?.Invoke();
    }

    // ── Filters ──────────────────────────────────────────────────

    private void RebuildFacets()
    {
        _suppress = true;
        RebuildFacet(CboSource, SourcePanel, "All sources",
            _all.Select(i => i.Source).Where(s => !string.IsNullOrEmpty(s)), minDistinct: 2);
        RebuildFacet(CboFolder, FolderPanel, "All folders",
            _all.Select(i => i.Folder).Where(f => !string.IsNullOrEmpty(f))!, minDistinct: 1);
        _suppress = false;
    }

    private static void RebuildFacet(ComboBox combo, FrameworkElement panel, string allLabel,
                                     IEnumerable<string> values, int minDistinct)
    {
        string previous = (combo.SelectedItem as FacetEntry)?.Value ?? AllEntry;
        var groups = values.GroupBy(v => v)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        combo.Items.Clear();
        combo.Items.Add(new FacetEntry(AllEntry, allLabel));
        foreach (var g in groups)
            combo.Items.Add(new FacetEntry(g.Key, g.Key + "  —  " + g.Count()));
        int index = 0;
        for (int i = 0; i < combo.Items.Count; i++)
            if (((FacetEntry)combo.Items[i]).Value == previous) { index = i; break; }
        combo.SelectedIndex = index;
        panel.Visibility = groups.Count >= minDistinct ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<ItemCardData> GetFilteredItems()
    {
        IEnumerable<ItemCardData> q = _all;

        if (CboSource.SelectedItem is FacetEntry source && source.Value != AllEntry)
            q = q.Where(i => i.Source == source.Value);

        if (CboFolder.SelectedItem is FacetEntry folder && folder.Value != AllEntry)
            q = q.Where(i => i.Folder == folder.Value);

        if (CboType.SelectedItem is ItemCard.TypeEntry type && !type.IsAll)
            q = q.Where(i => type.Matches(i.User.EquipmentType));

        string needle = (TxtSearch.Text ?? "").Trim();
        if (needle.Length > 0)
            q = q.Where(i => i.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase));

        var list = q.ToList();
        ItemCard.Sort(list, CboSort.SelectedItem is ItemCard.SortEntry sort ? sort.Mode : ItemSort.Quality);
        return list;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _page = 0;
        PopulateCards(scrollToTop: true);
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 0) { _page--; PopulateCards(scrollToTop: true); }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        PopulateCards(scrollToTop: true);
    }

    // ── Cards ────────────────────────────────────────────────────

    private void PopulateCards(bool scrollToTop)
    {
        CardPanel.Children.Clear();

        _visible = GetFilteredItems();
        int totalPages = Math.Max(1, (_visible.Count + PageSize - 1) / PageSize);
        if (_page >= totalPages) _page = totalPages - 1;
        if (_page < 0) _page = 0;

        int start = _page * PageSize;
        int end = Math.Min(start + PageSize, _visible.Count);
        for (int i = start; i < end; i++)
            CardPanel.Children.Add(CreateCard(_visible[i]));

        if (scrollToTop) CardScroller.ScrollToTop();

        BtnPrev.IsEnabled = _page > 0;
        BtnNext.IsEnabled = _page < totalPages - 1;
        LblPage.Text = "Page " + (_page + 1) + " / " + totalPages;

        LblStatus.Text = "Showing " + (end - start) + " of " + _visible.Count
                       + (_visible.Count != _all.Count ? " filtered" : "")
                       + (_note.Length > 0 ? "  —  " + _note : "");
        UpdateEmptyState();
    }

    private Border CreateCard(ItemCardData d)
    {
        var card = ItemCard.Build(d, d.Key == _selectedKey);
        card.MouseLeftButtonDown += (s, e) =>
        {
            Select(d.Key);
            if (e.ClickCount == 2) Activated?.Invoke(d);
        };
        return card;
    }

    private void UpdateEmptyState()
    {
        bool hasCards = CardPanel.Children.Count > 0;
        EmptyState.Visibility = hasCards ? Visibility.Collapsed : Visibility.Visible;
        PaginationBar.Visibility = hasCards ? Visibility.Visible : Visibility.Collapsed;
        if (hasCards) return;
        bool filteredOut = _all.Count > 0;
        EmptyText.Text = filteredOut ? "No items match these filters." : _emptyText;
        EmptyIcon.Text = filteredOut ? "\uE721" : _emptyGlyph;
    }
}
