using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Modinator.Views;

// The Item Dupe pickers. Both look like the Forge Viewer (same command bar,
// filters and cards, via ItemBrowser).
//
//   Target: your own items only (item box + hero gear). Items closest to
//           the source are marked and listed first — "Best match" (same
//           base item), then "Recommended" (same kind); any item can still
//           be picked.
//   Source: three tabs — Forge (your own items), Templates (the saved
//           library) and Players & Floor (everyone's equipped gear plus
//           floor drops in the current game).
//   Template: opened from the Templates tab to save a new template —
//           Forge and Players & Floor only (no Templates tab).
//
// The Forge tab reads the game itself (ForgeViewerView.ScanForPicker), so
// nothing depends on a Forge Viewer scan having been run first. This dialog
// only reads; the caller re-verifies identity and ownership before any write.
public partial class ItemPickerDialog : Window
{
    internal enum Mode { Source, Target, Template }

    // Floor mode: a full rediscovery costs ~0.3 s, so new drops are picked
    // up on their own every few seconds; the 1 s ticks in between only
    // re-verify what is already listed and re-read player gear.
    private const int FloorRediscoverEveryTicks = 4;

    private readonly MainWindow _main;
    private readonly int _excludeAddress;
    private readonly DupeSourceShape? _source;
    private readonly CancellationTokenSource _cancel = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closed, _forgeLoaded, _forgeBusy, _templatesLoaded, _sessionStarted, _sessionBusy, _sessionPending;
    private int _ticks;
    private List<MultiplayerItem> _floorKnown = new();
    private string _sessionKeys = "";

    internal int? PickedAddress { get; private set; }
    internal ItemTemplate? PickedTemplate { get; private set; }
    internal MultiplayerItem? PickedRemote { get; private set; }

    private sealed record SessionRead(List<ItemCardData> Cards, List<MultiplayerItem> Floor, string Note);

    // source: what the dupe source is, to rank targets (target picker only).
    internal ItemPickerDialog(MainWindow main, Mode mode, int excludeAddress, DupeSourceShape? source = null)
    {
        InitializeComponent();
        _main = main;
        Owner = main;
        _excludeAddress = excludeAddress;
        _source = source is null || source.ClassPath == "?" ? null : source;

        if (mode == Mode.Target)
        {
            Title = "Pick TARGET item (this item will be overwritten)";
            LblPrompt.Text = "This item becomes a copy of the source. Its own stats are lost." +
                             (_source != null ? " Best match = the same base item as the source, Recommended = the same kind; others may be unstable." : "");
            BtnOk.Content = "USE AS TARGET";
            TabStrip.Visibility = Visibility.Collapsed;
        }
        else if (mode == Mode.Template)
        {
            Title = "Add a template";
            LblPrompt.Text = "The item is only read and saved to your template library.";
            TabTemplates.Visibility = Visibility.Collapsed;
            BtnOk.Content = "ADD AS TEMPLATE";
        }
        else
        {
            Title = "Pick SOURCE item (the item to duplicate)";
            LblPrompt.Text = "The source is only read. The target item becomes a copy of it.";
        }

        ForgeBrowser.Configure("YOUR ITEMS", "\uE71C", "Search items by name, description, or level...",
            "Click to select  ·  double-click to use",
            "No items found in your item box or on your heroes.");
        TemplateBrowser.Configure("TEMPLATES", "\uE8F1", "Search templates by name, item, or description...",
            "Click to select  ·  double-click to use  ·  manage templates in the Templates tab",
            "No templates yet. Use ADD TO TEMPLATES on a Forge Viewer card to save one.");
        TemplateBrowser.AddNewestSort(select: false);
        SessionBrowser.Configure("PLAYERS & FLOOR", "\uE716", "Search items, players or heroes...",
            "Click to select  ·  double-click to use  ·  new drops appear within a few seconds",
            "No player gear or floor items found. Join a game, then press REFRESH.");

        AddRefresh(ForgeBrowser, "Read your item box and hero gear again.", () => LoadForge());
        AddRefresh(TemplateBrowser, "Reload the template library from disk.", () => LoadTemplates());
        AddRefresh(SessionBrowser, "Rescan players' gear and floor items now.", () => RefreshSession(discover: true));

        foreach (var browser in new[] { ForgeBrowser, TemplateBrowser, SessionBrowser })
        {
            browser.SelectionChanged += UpdateOk;
            browser.Activated += _ => Accept();
        }

        _timer.Tick += (_, _) =>
        {
            if (SessionBrowser.Visibility != Visibility.Visible) return;
            _ticks++;
            RefreshSession(discover: _ticks % FloorRediscoverEveryTicks == 0, quiet: true);
        };
        Loaded += (_, _) => ShowTab();
        Closed += (_, _) => { _closed = true; _timer.Stop(); _cancel.Cancel(); };
    }

    private void AddRefresh(ItemBrowser browser, string toolTip, Action refresh)
    {
        var button = new Button
        {
            Content = "REFRESH",
            Tag = "\uE72C",
            ContentTemplate = (DataTemplate)FindResource("IconButtonContent"),
            Padding = new Thickness(14, 7, 14, 7),
            Height = 32,
            ToolTip = toolTip
        };
        button.Click += (_, _) => refresh();
        browser.Actions.Children.Add(button);
    }

    // ── Tabs ─────────────────────────────────────────────────────

    private ItemBrowser ActiveBrowser
        => TabTemplates.IsChecked == true ? TemplateBrowser
         : TabSession.IsChecked == true ? SessionBrowser
         : ForgeBrowser;

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ShowTab();
    }

    // Each tab loads the first time it is shown.
    private void ShowTab()
    {
        var active = ActiveBrowser;
        foreach (var browser in new[] { ForgeBrowser, TemplateBrowser, SessionBrowser })
            browser.Visibility = browser == active ? Visibility.Visible : Visibility.Collapsed;

        if (active == ForgeBrowser && !_forgeLoaded) LoadForge();
        else if (active == TemplateBrowser && !_templatesLoaded) LoadTemplates();
        else if (active == SessionBrowser && !_sessionStarted)
        {
            _sessionStarted = true;
            RefreshSession(discover: true);
            _timer.Start();
        }
        UpdateOk();
    }

    private void UpdateOk() => BtnOk.IsEnabled = ActiveBrowser.Selected != null;

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ActiveBrowser.Selected is not ItemCardData picked) return;
        switch (picked.Payload)
        {
            case ItemTemplate template: PickedTemplate = template.Copy(); break;
            // The caller rechecks live membership and identity before any save/write.
            case MultiplayerItem remote: PickedRemote = remote; break;
            default: PickedAddress = picked.Address; break;
        }
        DialogResult = true;
    }

    // ── Forge: your item box + hero gear ─────────────────────────

    private async void LoadForge()
    {
        if (_forgeBusy || _closed) return;
        _forgeBusy = true;
        _forgeLoaded = true;
        ForgeBrowser.SetMessage("Reading your items…");
        try
        {
            var progress = new Progress<int>(n =>
            {
                if (_forgeBusy && !_closed) ForgeBrowser.SetMessage($"Reading your items… {n:N0}");
            });
            var scan = await Task.Run(() =>
            {
                var read = ForgeViewerView.ScanForPicker(_main, progress);
                if (_source != null)
                    foreach (var item in read.items)
                    {
                        item.Match = _source.Match(item.Address);
                        if (item.Match == 2) item.Footer = "Best match  ·  same base item as the source";
                        else if (item.Match == 1) item.Footer = "Recommended  ·  same kind as the source";
                    }
                return read;
            });
            if (_closed) return;
            if (scan.items.Any(i => i.Recommended))
                ForgeBrowser.AddSort(ItemSort.Recommended, "Recommended first", select: true);
            // The other side of the copy can't be picked twice.
            var items = scan.items.Where(i => i.Address != _excludeAddress).ToList();
            ForgeBrowser.SetItems(items, scan.failed > 0 ? scan.failed + " unreadable (refresh)" : "", keepView: true);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            ForgeBrowser.SetItems(Array.Empty<ItemCardData>());
            ForgeBrowser.SetMessage("Items not reachable — " + ex.Message);
        }
        finally { _forgeBusy = false; }
    }

    // ── Templates ────────────────────────────────────────────────

    private void LoadTemplates()
    {
        _templatesLoaded = true;
        try
        {
            var entries = ItemTemplateLibrary.Load(out int unreadable);
            var cards = new List<ItemCardData>(entries.Count);
            foreach (var entry in entries)
            {
                try { cards.Add(ItemCard.FromTemplate(entry)); }
                catch { unreadable++; }
            }
            TemplateBrowser.SetItems(cards, unreadable > 0 ? unreadable + " unreadable files skipped" : "", keepView: true);
        }
        catch (Exception ex)
        {
            TemplateBrowser.SetItems(Array.Empty<ItemCardData>());
            TemplateBrowser.SetMessage("Library unavailable: " + ex.Message);
        }
    }

    // ── Players & Floor ──────────────────────────────────────────

    // quiet: a timer pass — no bar, no "Finding…" status flicker, and the
    // cards are only rebuilt when the set of items actually changed.
    private async void RefreshSession(bool discover, bool quiet = false)
    {
        if (_closed) return;
        if (_sessionBusy)
        {
            if (discover && !quiet) _sessionPending = true;
            return;
        }
        _sessionBusy = true;
        var known = discover ? null : _floorKnown.ToList();
        bool loud = discover && !quiet;
        if (loud)
        {
            SessionBrowser.SetMessage("Finding player gear and floor items…");
            SessionBrowser.SetProgress(0);
        }
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (!loud || !_sessionBusy || _closed) return;
                SessionBrowser.SetMessage(p.Message);
                SessionBrowser.SetProgress(p.Fraction);
            });
            var read = await Task.Run(() => _main.ReadForDupe(
                world => ReadSession(world, known, progress, _cancel.Token), discoverWorld: loud));
            if (_closed) return;
            _floorKnown = read.Floor;
            string keys = string.Join("|", read.Cards.Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal));
            if (loud || keys != _sessionKeys)
            {
                _sessionKeys = keys;
                SessionBrowser.SetItems(read.Cards, read.Note, keepView: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _floorKnown = new();
                _sessionKeys = "";
                SessionBrowser.SetItems(Array.Empty<ItemCardData>(), keepView: true);
                SessionBrowser.SetMessage("Items unavailable: " + ex.Message);
            }
        }
        finally
        {
            _sessionBusy = false;
            if (!_closed)
            {
                if (loud) SessionBrowser.SetProgress(null);
                if (_sessionPending) { _sessionPending = false; RefreshSession(discover: true); }
            }
        }
    }

    // Worker thread, inside MainWindow's scan gate. Equipped gear of every
    // player in the game plus the floor drops, as one list. A floor failure
    // (not in a level yet) still leaves the player gear usable.
    private SessionRead ReadSession(int world, List<MultiplayerItem>? knownFloor,
                                    IProgress<ScanProgress> progress, CancellationToken cancel)
    {
        var players = MultiplayerEquipment.Read(world, cancel);
        var floor = new List<MultiplayerItem>();
        var notes = new List<string>();
        try
        {
            var scan = FloorEquipment.Read(world, cancel, knownFloor, progress);
            floor = scan.Items;
            if (scan.Note.Length > 0) notes.Add(scan.Note);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { notes.Add("floor items unavailable: " + ex.Message); }

        var cards = new List<ItemCardData>(players.Items.Count + floor.Count);
        int unreadable = players.Unavailable;
        foreach (var item in players.Items.Concat(floor))
        {
            cancel.ThrowIfCancellationRequested();
            if (item.Address == _excludeAddress) continue;
            try
            {
                var card = ItemCardData.ReadLive(item.Address);
                card.Key = item.Key;
                card.Payload = item;
                card.Source = item.Floor != null ? "Floor" : item.Player;
                card.Footer = item.SourceDescription;
                card.SearchText = ItemCard.SearchText(card, null) + " " + item.Player + " " + item.HeroName +
                                  (item.Floor != null ? " floor" : "");
                cards.Add(card);
            }
            catch { unreadable++; }
        }
        if (unreadable > 0) notes.Add(unreadable + " unreadable");
        string note = Count(players.Players, "player") + "  ·  " + Count(floor.Count, "floor item") +
                      (notes.Count > 0 ? "  ·  " + string.Join("  ·  ", notes) : "");
        return new SessionRead(cards, floor, note);
    }

    private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");
}
