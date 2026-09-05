using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Modinator.Views;

public partial class ForgeViewerView : UserControl
{
    // ── Inner types ──────────────────────────────────────────────

    private class CachedItem
    {
        public int Address;
        public string Name = "";
        public string BaseName = "";
        public string Description = "";
        public string ForgerName = "";
        public string SearchText = "";
        public int EquipmentTemplate;
        public ItemUser User = new();
        public int FolderID;
        public int EquipmentID1;
        public int EquipmentID2;
        public bool IsHero;
        public bool IsRealInstance => EquipmentID1 != 0 || EquipmentID2 != 0;
    }

    private class FolderEntry
    {
        public int FolderID;
        public string Label;
        public FolderEntry(int folderId, string label) { FolderID = folderId; Label = label; }
        public override string ToString() => Label;
    }

    private class TypeEntry
    {
        public EquipmentType Type;
        public string Label;
        public bool IsAll;
        public EquipmentType[]? Group;

        public TypeEntry(EquipmentType type, string label, bool isAll)
        { Type = type; Label = label; IsAll = isAll; }

        public TypeEntry(string label, params EquipmentType[] group)
        { Label = label; Group = group; }

        public bool Matches(EquipmentType t)
        {
            if (IsAll) return true;
            if (Group != null) return Array.IndexOf(Group, t) >= 0;
            return t == Type;
        }

        public override string ToString() => Label;
    }

    private enum SourceMode { Forge, Hero, All }

    private class SourceEntry
    {
        public SourceMode Mode;
        public string Label;
        public SourceEntry(SourceMode mode, string label) { Mode = mode; Label = label; }
        public override string ToString() => Label;
    }

    private enum SortMode
    {
        Quality, MaxLevelDesc, MaxLevelAsc, LevelDesc,
        HeroDamageDesc, TowerDamageDesc, WeaponDamageDesc,
        NameAsc, BestStat
    }

    private class SortEntry
    {
        public SortMode Mode;
        public string Label;
        public SortEntry(SortMode mode, string label) { Mode = mode; Label = label; }
        public override string ToString() => Label;
    }

    // ── Constants & state ────────────────────────────────────────

    private const int PageSize = 30;

    // Sentinel "icon path" for the weapon Elemental-damage tile, which has
    // no dedicated asset — MakeIconTile renders a glyph placeholder for it.
    private const string SelectionMarkerTag = "SelectionMarker";

    private readonly Dictionary<string, ImageBrush?> _statIconBrushes = new();
    private readonly SolidColorBrush _selectedCardBrush = new(Color.FromArgb(30, 88, 101, 242));

    private List<int> forgeResults = new();
    private List<CachedItem> cachedItems = new();

    // Snapshot of the most recent forge read, exposed so other views (the
    // Item Dupe picker) can surface the item list without having
    // to re-scan. Updated after every ReadAllItems() pass. Internal because
    // Quality2 / EquipmentType are internal to the assembly.
    internal static IReadOnlyList<ForgeSnapshotItem> LastSnapshot { get; private set; } = Array.Empty<ForgeSnapshotItem>();

    internal sealed class ForgeSnapshotItem
    {
        public int Address { get; init; }
        public string Name { get; init; } = "";
        public string ForgerName { get; init; } = "";
        public int EquipmentTemplate { get; init; }
        public int FolderID { get; init; }
        public int EquipmentID1 { get; init; }
        public int EquipmentID2 { get; init; }
        public Quality2 Quality { get; init; }
        public EquipmentType EquipmentType { get; init; }
        public int Level { get; init; }
        public bool IsHero { get; init; }
        public bool IsRealInstance => EquipmentID1 != 0 || EquipmentID2 != 0;
    }
    private int currentPage;
    private HashSet<int> selectedAddresses = new();
    private bool _suppressFilterEvent;
    private Dictionary<int, string> _folderNames = new();
    // The filtered+sorted list PopulateCards last rendered from — reused by
    // the bulk-button / selection-status refreshers so a card click or a
    // keystroke doesn't re-filter and re-sort the whole cache twice.
    private List<CachedItem> _visibleFiltered = new();
    // Search-box debounce: rebuilding 30 cards per keystroke is visibly
    // laggy; wait for a short pause in typing instead.
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    // Source combo changed while a scan was in flight — run once more when
    // it finishes so the list can't silently disagree with the combo.
    private bool _rescanPending;

    // ── Constructor ──────────────────────────────────────────────

    public ForgeViewerView()
    {
        InitializeComponent();
        _selectedCardBrush.Freeze();
        _suppressFilterEvent = true;
        PopulateSourceCombo();
        PopulateTypeCombo();
        PopulateSortCombo();
        _suppressFilterEvent = false;
        _searchDebounce.Tick += (s, e) =>
        {
            _searchDebounce.Stop();
            currentPage = 0;
            PopulateCards();
        };
    }

    // ── Combo population ─────────────────────────────────────────

    private void PopulateSourceCombo()
    {
        CboSource.Items.Add(new SourceEntry(SourceMode.All,   "All"));
        CboSource.Items.Add(new SourceEntry(SourceMode.Forge, "Forge"));
        CboSource.Items.Add(new SourceEntry(SourceMode.Hero,  "Hero"));
        CboSource.SelectedIndex = 0; // default: All (Forge + Hero)
    }

    private void PopulateTypeCombo()
    {
        CboType.Items.Add(new TypeEntry(EquipmentType.All, "All types", true));
        CboType.Items.Add(new TypeEntry(EquipmentType.Weapon, "Weapon", false));
        CboType.Items.Add(new TypeEntry("All Armor / Accessories",
            EquipmentType.ArmorHelmet, EquipmentType.ArmorTorso,
            EquipmentType.ArmorBoots, EquipmentType.ArmorGloves,
            EquipmentType.Hat, EquipmentType.ArmGuard,
            EquipmentType.Shield, EquipmentType.Mask));
        CboType.Items.Add(new TypeEntry(EquipmentType.ArmorHelmet, "Helmet", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.ArmorTorso, "Torso", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.ArmorBoots, "Boots", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.ArmorGloves, "Gloves", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.Familiar, "Familiar", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.Hat, "Hat", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.ArmGuard, "ArmGuard", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.Shield, "Shield", false));
        CboType.Items.Add(new TypeEntry(EquipmentType.Mask, "Mask", false));
        CboType.SelectedIndex = 0;
    }

    private void PopulateSortCombo()
    {
        CboSort.Items.Add(new SortEntry(SortMode.Quality, "Quality (best first)"));
        CboSort.Items.Add(new SortEntry(SortMode.MaxLevelDesc, "Max Level (high to low)"));
        CboSort.Items.Add(new SortEntry(SortMode.MaxLevelAsc, "Max Level (low to high)"));
        CboSort.Items.Add(new SortEntry(SortMode.LevelDesc, "Level (high to low)"));
        CboSort.Items.Add(new SortEntry(SortMode.HeroDamageDesc, "Hero Damage (high to low)"));
        CboSort.Items.Add(new SortEntry(SortMode.TowerDamageDesc, "Tower Damage (high to low)"));
        CboSort.Items.Add(new SortEntry(SortMode.WeaponDamageDesc, "Weapon Damage (high to low)"));
        CboSort.Items.Add(new SortEntry(SortMode.BestStat, "Best stat total"));
        CboSort.Items.Add(new SortEntry(SortMode.NameAsc, "Name (A-Z)"));
        CboSort.SelectedIndex = 0;
    }

    // ── Button handlers ──────────────────────────────────────────

    private void BtnScan_Click(object sender, RoutedEventArgs e) => RunForgeScan();

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Changing the source changes the underlying item set (not just a
        // view filter), so re-enumerate. Suppressed during ctor population.
        if (_suppressFilterEvent) return;
        if (!BtnScan.IsEnabled) { _rescanPending = true; return; }
        RunForgeScan();
    }

    private async void RunForgeScan()
    {
        // Re-entrancy guard: the Source combo's SelectionChanged also calls
        // this, and it stays enabled during the multi-second awaits — a
        // second concurrent scan would interleave with the first.
        if (!BtnScan.IsEnabled) return;
        // Attach on the UI thread (may show the choose-process dialog).
        if (!Base.OpenProcess()) return;
        if (Window.GetWindow(this) is not Modinator.MainWindow mw) return;

        var mode = (CboSource.SelectedItem as SourceEntry)?.Mode ?? SourceMode.Forge;
        BtnScan.IsEnabled = false;
        try
        {
            // Every game-memory step runs off the UI thread. The first
            // resolve can be a multi-second WorldInfo sweep (cold cache on
            // every launch / toggle change), and the per-item read is ~5
            // RPMs × ~1,000 items — both used to freeze the window with
            // "Reading items..." never even painting. The UI thread only
            // commits results and builds cards. ResolvePlayerPawnAddress /
            // InvalidatePawnScanCache / ForceStructuralReseed all serialize
            // on MainWindow's scan gate against the Auto-Kill task.
            LblStatus.Text = "Locating items...";
            Enumeration? en = await System.Threading.Tasks.Task.Run(() => TryEnumerate(mw, mode));

            if (en == null)
            {
                // Cheap retry first: a single stale read in the
                // pawn→HeroManager chain (common right after a map change /
                // game restart) yields null. Drop the cached pawn-scan + AK
                // handle and resolve once more before the heavier path.
                en = await System.Threading.Tasks.Task.Run(() =>
                {
                    mw.InvalidatePawnScanCache();
                    return TryEnumerate(mw, mode);
                });
            }

            if (en == null)
            {
                // Auto-recalibrate (self-healing): the cheap retry didn't
                // help, so re-derive the WorldInfo + pawn-vtable seed
                // structurally from live memory — exactly what Settings →
                // CALIBRATE does — then try the enumeration once more. This
                // is the "if the forge fails, run forge calibration by
                // default" behaviour: the user never has to open Settings for
                // the common post-patch / post-restart miss.
                // ForceStructuralReseed re-pins the seed and leaves a
                // freshly-validated WorldInfo cached, which
                // ResolvePlayerPawnAddress then reuses.
                LblStatus.Text = "Recalibrating from live memory...";
                en = await System.Threading.Tasks.Task.Run(() =>
                {
                    mw.ForceStructuralReseed();
                    return TryEnumerate(mw, mode);
                });
            }

            if (en == null)
            {
                // Staged diagnosis: not running / 64-bit unsupported /
                // menu (no pawn) / chain broke — say which.
                string why = GameChain.DescribeScanFailure(_lastResolvedPawn);
                Base.RaiseMessage(why, "Forge Viewer");
                LblStatus.Text = "Items not reachable — " + why;
                OnScanFail();
                return;
            }

            if (en.Addresses.Count == 0)
            {
                forgeResults = en.Addresses;
                OnScanFail();
                return;
            }

            // Read every item (struct + strings + name fallback) off-thread,
            // reporting progress; commit the finished lists in one swap so
            // a filter change during the read can never enumerate a list
            // that is being rebuilt underneath it.
            int total = en.Addresses.Count;
            var progress = new Progress<int>(n => LblStatus.Text = $"Reading items... {n} / {total}");
            var read = await System.Threading.Tasks.Task.Run(() => ReadAllItems(en, progress));

            // Addresses enumerated but NOTHING readable is a failed scan, not
            // an empty forge. Publishing that as success shows a working box
            // as empty with no hint why — and "my forge is empty" is the
            // single hardest symptom to diagnose remotely.
            if (read.items.Count == 0 && read.failed > 0)
            {
                _lastScanFailed = read.failed;
                LblStatus.Text = $"Scan failed — none of the {read.failed} items could be read. " +
                                 "The game may have closed or changed map; try again.";
                OnScanFail();
                return;
            }

            _lastScanFailed = read.failed;
            forgeResults = en.Addresses;
            _folderNames = read.folders;
            cachedItems = read.items;
            PublishSnapshot();
            OnScanSuccess();
        }
        catch (Exception ex)
        {
            // An exception escaping an async void handler would take the
            // whole app down; the memory paths swallow their own read
            // races, so anything reaching here is unexpected — report it.
            LblStatus.Text = "Scan failed: " + ex.Message;
            OnScanFail();
        }
        finally
        {
            BtnScan.IsEnabled = true;
            if (_rescanPending)
            {
                _rescanPending = false;
                RunForgeScan();
            }
        }
    }

    // Authoritative enumeration — walks the HeroManager's own lists instead
    // of scanning memory and guessing which structs are "real". Chain
    // verified live 2026-05-15 (memdump forge_chain; Num matched the
    // in-game forge count exactly):
    //   playerPawn +0x22C → ADunDefPlayerController
    //              +0x3B8 → Player (ULocalPlayer)
    //              +0x194 → ViewportClient (UDunDefViewportClient)
    //              +0xCFC → TheHeroManager (UDunDefHeroManager)
    // Forge  = HeroManager.ItemBoxEquipments TArray (offset is version-
    //          fragile — DISCOVERED + pinned via GameChain.ReadItemBox, not
    //          a fixed literal; a 2026-06 patch moved it 0x39C → 0x3A8)
    // Hero   = HeroManager.ActiveHeroes TArray (discovered + pinned pair
    //          base + 0xC — see GameChain), then each
    //          UDunDefHero.HeroEquipments TArray @ +0x5B0
    // Every element is a UHeroEquipment*; its inline ItemNative starts at
    // +0x38 (same layout as forge items / floor drops), so we return
    // he+0x38 and the existing ReadAllItems/ItemNative/edit pipeline is
    // unchanged for both sources.
    // Result of one enumeration pass (built on a worker thread, committed
    // on the UI thread). HeroMgr is kept so the item read can fetch folder
    // names without walking the pawn chain a second time.
    private sealed class Enumeration
    {
        public int HeroMgr;
        public List<int> Addresses = new();
        public HashSet<int> HeroAddrs = new();
    }

    // Worker-thread body: resolve the chain and enumerate. Touches no UI
    // (the Source mode is captured by the caller). null = chain unreachable.
    private Enumeration? TryEnumerate(Modinator.MainWindow mw, SourceMode mode)
    {
        _lastResolvedPawn = mw.ResolvePlayerPawnAddress();
        int heroMgr = GameChain.ResolveHeroManager(_lastResolvedPawn);
        if (!IsGamePtr(heroMgr)) return null;

        var en = new Enumeration { HeroMgr = heroMgr };

        if (mode == SourceMode.Forge || mode == SourceMode.All)
            foreach (int he in GameChain.ReadItemBox(heroMgr))     // ItemBoxEquipments (self-healing offset)
                en.Addresses.Add(he + 0x38);

        if (mode == SourceMode.Hero || mode == SourceMode.All)
            foreach (int hero in GameChain.ReadActiveHeroes(heroMgr)) // ActiveHeroes (self-healing offset)
                foreach (int he in ReadPtrArray(hero + 0x5B0))     // UDunDefHero.HeroEquipments
                {
                    int addr = he + 0x38;
                    en.Addresses.Add(addr);
                    en.HeroAddrs.Add(addr);
                }

        return en;
    }

    // Last pawn the chain resolution saw — feeds the staged failure
    // message (distinguishes "no character" from "chain broke").
    private int _lastResolvedPawn;

    // Thin wrappers over the shared GameChain helpers (one home for the
    // chain logic; call sites here stay unchanged).
    private static List<int> ReadPtrArray(int tarrayAddr) => GameChain.ReadPtrArray(tarrayAddr);
    private static bool IsGamePtr(int p) => GameChain.IsGamePtr(p);
    private static int RdPtr(int addr) => GameChain.RdPtr(addr);
    private static int RdInt(int addr) => GameChain.RdInt(addr);

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var items = GetFilteredItems();
        if (items.Count == 0)
        {
            Base.RaiseMessage(
                "Nothing to export -- run a scan and make sure the current filter has at least one item.",
                "Export CSV");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV File|*.csv",
            FileName = "forge_items.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Address,EquipmentTemplate,EquipmentType,Quality,Level,BaseEquipmentName,EquipmentName");
        foreach (var ci in items)
        {
            sb.Append("0x").Append(ci.Address.ToString("X8")).Append(',');
            sb.Append("0x").Append(ci.EquipmentTemplate.ToString("X8")).Append(',');
            sb.Append(ci.User.EquipmentType).Append(',');
            sb.Append(ci.User.Quality2).Append(',');
            sb.Append(ci.User.Level).Append(',');
            sb.Append(CsvEscape(ci.BaseName)).Append(',');
            sb.Append(CsvEscape(ci.Name));
            sb.AppendLine();
        }
        File.WriteAllText(dlg.FileName, sb.ToString());

        Base.RaiseMessage("Exported " + items.Count + " items to:\r\n" + dlg.FileName, "Export CSV");
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var filtered = GetFilteredItems();
        if (filtered.Count == 0) return;

        bool allSelected = filtered.All(ci => selectedAddresses.Contains(ci.Address));
        if (allSelected)
        {
            foreach (var ci in filtered) selectedAddresses.Remove(ci.Address);
        }
        else
        {
            foreach (var ci in filtered) selectedAddresses.Add(ci.Address);
        }

        PopulateCards();
    }

    private void BtnBulk_Click(object sender, RoutedEventArgs e)
    {
        if (selectedAddresses.Count == 0) return;

        // Mixed types are fine now — bulk MAX is class-aware per item.
        var typeEntry = CboType.SelectedItem as TypeEntry;
        string typeLabel = (typeEntry != null && !typeEntry.IsAll) ? typeEntry.Label : "selected items";
        var addresses = new List<int>(selectedAddresses);

        // Identity of each selected item AS OF THIS SCAN, so the dialog can
        // skip an address that no longer holds the item the user selected
        // (sold/dropped → freed → reused by another object). An address with
        // no entry here is treated as stale by the dialog — that is the point:
        // the selection survives a rescan, so anything that vanished from the
        // rebuilt cache is exactly what must not be written to.
        var identities = new Dictionary<int, ItemIdentity>(addresses.Count);
        foreach (var ci in cachedItems)
            if (selectedAddresses.Contains(ci.Address))
                identities[ci.Address] = new ItemIdentity(ci.EquipmentTemplate, ci.EquipmentID1, ci.EquipmentID2);

        int unknown = addresses.Count - identities.Count;
        if (unknown > 0 && identities.Count == 0)
        {
            Base.RaiseMessage(
                "None of the selected items are in the current scan — they were probably sold, " +
                "dropped, or the list was refreshed. Rescan and re-select.",
                "Bulk Edit");
            return;
        }
        if (unknown > 0)
            Base.RaiseMessage(
                $"{unknown} of the {addresses.Count} selected items are no longer in the current scan " +
                "and will be skipped. Rescan the Forge to pick them up again.",
                "Bulk Edit");

        var dlg = new BulkEditDialog(addresses, typeLabel, identities);
        dlg.Owner = Application.Current.MainWindow;
        if (dlg.ShowDialog() == true)
        {
            // Refresh cached items from memory after bulk edit
            int structSize = Marshal.SizeOf(typeof(ItemNative));
            foreach (int addr in addresses)
            {
                try
                {
                    byte[] data = Base.Instance.ReadMemory(addr, structSize);
                    ItemNative native = Base.Push<ItemNative>(data);
                    ItemUser user = Base.ItemToUser(native);
                    for (int i = 0; i < cachedItems.Count; i++)
                    {
                        if (cachedItems[i].Address == addr)
                        {
                            cachedItems[i].User = user;
                            // The strings have to be re-read too, not just the
                            // struct: BuildSearchHaystack pulls Name/Description/
                            // ForgerName off the cached item, so rebuilding it
                            // from stale strings leaves the search box (and the
                            // card text) matching the pre-edit values until a
                            // full rescan. Bulk edit writes all three — and since
                            // the watermark, Description changes on EVERY bulk
                            // edit, not just ones that set description text.
                            cachedItems[i].Name = SafeReadName(addr, native, user);
                            cachedItems[i].Description = SafeReadUni(addr, "Description");
                            cachedItems[i].ForgerName = SafeReadUni(addr, "ForgerName");
                            cachedItems[i].SearchText = BuildSearchHaystack(cachedItems[i], _folderNames);
                            break;
                        }
                    }
                }
                catch { }
            }
            selectedAddresses.Clear();
            PopulateCards();

            string summary = "Bulk edit complete.\n\nUpdated: " + dlg.AppliedCount;
            if (dlg.FailedCount > 0)
                summary += "\nFailed: " + dlg.FailedCount;
            if (dlg.StaleCount > 0)
                summary += "\nSkipped (changed since scan): " + dlg.StaleCount + " — rescan the Forge to see them.";
            Base.RaiseMessage(summary, "Bulk Edit");
        }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        // Wipe every cached bit of forge state and return the UI to the
        // "just opened the page" look. Next action has to be a fresh scan.
        forgeResults.Clear();
        cachedItems.Clear();
        selectedAddresses.Clear();
        _folderNames.Clear();
        LastSnapshot = Array.Empty<ForgeSnapshotItem>();
        currentPage = 0;

        _suppressFilterEvent = true;
        TxtSearch.Text = "";
        if (CboType.Items.Count > 0) CboType.SelectedIndex = 0;
        if (CboSort.Items.Count > 0) CboSort.SelectedIndex = 0;
        CboFolder.Items.Clear();
        _suppressFilterEvent = false;

        CardPanel.Children.Clear();
        LblStatus.Text = "Ready — press SCAN ALL to rebuild the cache.";
        LblPage.Text = "Page 1 / 1";
        BtnScanLabel.Text = "SCAN ALL";
        BtnScanIcon.Text = "\uE71E"; // Refresh/Sync glyph
        BtnPrev.IsEnabled = false;
        BtnNext.IsEnabled = false;
        UpdateBulkButton();
        UpdateEmptyState();
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (currentPage > 0) { currentPage--; PopulateCards(); }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        currentPage++;
        PopulateCards();
    }

    // ── Filter events ────────────────────────────────────────────

    // Filters and the search box narrow the VIEW only — the selection is
    // keyed by address and survives them, so a user can select across
    // folders / searches and bulk-edit the union. The status line and the
    // BULK (N) label count everything selected, and the status also says
    // how many of those are hidden by the current filter.
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvent) return;
        currentPage = 0;
        PopulateCards();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounced — see _searchDebounce.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // ── Bootstrap damage pointers ────────────────────────────────

    private int[]? BootstrapDamagePointers()
    {
        EquipmentType[] attempts = new[]
        {
            EquipmentType.Hat, EquipmentType.ArmGuard, EquipmentType.Shield,
            EquipmentType.Mask, EquipmentType.Familiar, EquipmentType.ArmorGloves,
            EquipmentType.ArmorBoots, EquipmentType.ArmorHelmet, EquipmentType.ArmorTorso
        };

        int structSize = Marshal.SizeOf(typeof(ItemNative));

        foreach (var t in attempts)
        {
            try
            {
                var search = new ItemSearch { EquipmentType = t };
                var seed = Base.ItemToNative(search);
                seed.R1 = new byte[3];
                seed._InstancePad = new byte[164];

                Base.CreateItemMask(seed);
                Base.Instance.ScanPages();
                Base.Instance.FirstScan(Base.Search, 56, 256, Base.Mask);

                int[]? hits = Base.Instance.Results;
                if (hits == null || hits.Length == 0) continue;

                foreach (int addr in hits)
                {
                    try
                    {
                        byte[] data = Base.Instance.ReadMemory(addr, structSize);
                        ItemNative native = Base.Push<ItemNative>(data);
                        if (native.DamageReductions == null || native.DamageReductions.Length < 4) continue;

                        if (native.Level <= 0 || native.Level > 200) continue;
                        if (native.MaxEquipmentLevel <= 0 || native.MaxEquipmentLevel > 200) continue;
                        if (native.MaxEquipmentLevel < native.Level) continue;
                        if ((uint)native.EquipmentTemplate < 0x100000u) continue;
                        if ((native.EquipmentTemplate & 3) != 0) continue;
                        if (!NativeArrayConsistent(native.EquipmentName)) continue;

                        int[] ptrs = new int[4];
                        bool valid = true;
                        for (int i = 0; i < 4; i++)
                        {
                            ptrs[i] = native.DamageReductions[i].DamageType;
                            if ((uint)ptrs[i] < 0x100000u || (ptrs[i] & 3) != 0)
                            { valid = false; break; }
                        }
                        if (!valid) continue;

                        int testHits = CountItemsWithPointers(ptrs);
                        if (testHits >= 20)
                        {
                            Base.Log("Forge Viewer: bootstrapped from " + t + " at 0x" + addr.ToString("X8") + " (" + testHits + " items)");
                            return ptrs;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    private static int CountItemsWithPointers(int[] ptrs)
    {
        try
        {
            ItemNative s = new ItemNative
            {
                StatModifiers = new int[10],
                DamageReductions = new DamageNative[4],
                R1 = new byte[3],
                _InstancePad = new byte[164]
            };
            for (int i = 0; i < 4; i++)
                s.DamageReductions[i].DamageType = ptrs[i];

            Base.CreateItemMask(s, useDamagePointers: true);
            Base.Instance.FirstScan(Base.Search, 56, 256, Base.Mask);
            int[]? hits = Base.Instance.Results;
            return hits == null ? 0 : hits.Length;
        }
        catch { return 0; }
    }

    private static bool NativeArrayConsistent(NativeArray na)
    {
        if (na.Address == 0)
            return na.CurrentLength == 0 && na.MaximumLength == 0;
        if (na.CurrentLength < 0 || na.CurrentLength > 4096) return false;
        if (na.MaximumLength < 0 || na.MaximumLength > 4096) return false;
        return true;
    }

    // ── Scan callbacks ───────────────────────────────────────────

    private void OnScanFail()
    {
        LblStatus.Text = "No items found.";
        cachedItems.Clear();
        currentPage = 0;
        CardPanel.Children.Clear();
        RepopulateFolderCombo(preserveView: false);
        UpdateEmptyState();
    }

    private void OnScanSuccess()
    {
        // A REFRESH keeps the folder the user was looking at and the page
        // they were on (edit an item → refresh to verify → still there).
        RepopulateFolderCombo(preserveView: true);
        // Once we have a cache, flip the primary button into REFRESH mode —
        // pressing it just reruns the same scan path.
        if (forgeResults.Count > 0)
        {
            BtnScanLabel.Text = "REFRESH";
            BtnScanIcon.Text = "\uE72C"; // Refresh arrows glyph
        }
    }

    // ── Read all items from memory ───────────────────────────────

    // Worker-thread body: folder names first (so the search haystack can
    // include them), then every item's struct + strings + name fallback.
    // Builds fresh lists — the caller swaps them in on the UI thread, so a
    // filter change mid-read can never enumerate a list being rebuilt.
    private (List<CachedItem> items, Dictionary<int, string> folders, int failed) ReadAllItems(
        Enumeration en, IProgress<int>? progress)
    {
        var folders = ReadFolderNames(en.HeroMgr);
        var items = new List<CachedItem>(en.Addresses.Count);
        int failed = 0;
        int structSize = Marshal.SizeOf(typeof(ItemNative));
        for (int i = 0; i < en.Addresses.Count; i++)
        {
            int address = en.Addresses[i];
            try
            {
                byte[] data = Base.Instance.ReadMemory(address, structSize);
                ItemNative native = Base.Push<ItemNative>(data);
                ItemUser user = Base.ItemToUser(native);
                string name = SafeReadName(address, native, user);
                string baseName = SafeReadUni(address, "BaseEquipmentName");
                string description = SafeReadUni(address, "Description");
                string forgerName = SafeReadUni(address, "ForgerName");

                var cached = new CachedItem
                {
                    Address = address,
                    Name = name,
                    BaseName = baseName,
                    Description = description,
                    ForgerName = forgerName,
                    EquipmentTemplate = native.EquipmentTemplate,
                    User = user,
                    FolderID = native.FolderID,
                    EquipmentID1 = native.EquipmentID1,
                    EquipmentID2 = native.EquipmentID2,
                    IsHero = en.HeroAddrs.Contains(address)
                };
                cached.SearchText = BuildSearchHaystack(cached, folders);
                items.Add(cached);
            }
            // Per-item races are expected and deliberately swallowed (an item
            // can be sold/moved mid-scan). But they must still be COUNTED:
            // without that, every address failing produced an empty cache that
            // was published as a successful scan, i.e. a working forge looked
            // like an empty one — the exact symptom that is hardest to
            // diagnose remotely.
            catch { failed++; }
            if (progress != null && ((i + 1) % 50 == 0 || i + 1 == en.Addresses.Count))
                progress.Report(i + 1);
        }
        if (failed > 0)
            Base.LogEvent($"ReadAllItems: {failed} of {en.Addresses.Count} item reads failed");
        return (items, folders, failed);
    }

    private void PublishSnapshot()
    {
        var snap = new List<ForgeSnapshotItem>(cachedItems.Count);
        foreach (var ci in cachedItems)
        {
            snap.Add(new ForgeSnapshotItem
            {
                Address = ci.Address,
                Name = ci.Name,
                ForgerName = ci.ForgerName,
                EquipmentTemplate = ci.EquipmentTemplate,
                FolderID = ci.FolderID,
                EquipmentID1 = ci.EquipmentID1,
                EquipmentID2 = ci.EquipmentID2,
                Quality = ci.User.Quality2,
                EquipmentType = ci.User.EquipmentType,
                Level = ci.User.Level,
                IsHero = ci.IsHero,
            });
        }
        LastSnapshot = snap;
    }

    // ── Folder names — authoritative (HeroManager.ItemFolders) ────
    //
    // UDunDefHeroManager.ItemFolders is a TArray<FItemFolder> at
    // HeroManager + 0x0098 (data, Num@+0x9C). FItemFolder (stride 24):
    //   +0x00 ParentID  +0x04 FolderID  +0x08 FolderName (FString:
    //   Data@+0x08, Num@+0x0C incl. null)  +0x14 Tag.
    // SDK-confirmed + matches DD_ModMenu list_folders (hm->ItemFolders →
    // f.FolderID / f.FolderName). Reuses the same verified HeroManager
    // chain as the item enumeration — no scan, no heuristic.

    // Takes the HeroManager the enumeration already resolved — walking the
    // pawn chain a second time per scan (and re-running the seed pin) was
    // pure overhead.
    private static Dictionary<int, string> ReadFolderNames(int heroMgr)
    {
        var names = new Dictionary<int, string>();
        if (!IsGamePtr(heroMgr)) return names;

        int dataPtr = RdPtr(heroMgr + 0x98);   // ItemFolders TArray.Data
        int num     = RdInt(heroMgr + 0x9C);   // ItemFolders TArray.Num
        if (!IsGamePtr(dataPtr) || num <= 0 || num > 100000) return names;

        const int Stride = 24;                 // sizeof(FItemFolder)
        byte[]? block;
        try { block = Base.Instance.ReadMemory(dataPtr, num * Stride); }
        catch { return names; }
        if (block == null || block.Length < num * Stride) return names;

        for (int i = 0; i < num; i++)
        {
            int b = i * Stride;
            int folderId = BitConverter.ToInt32(block, b + 0x04); // FItemFolder.FolderID
            int strPtr   = BitConverter.ToInt32(block, b + 0x08); // FolderName.Data
            int strLen   = BitConverter.ToInt32(block, b + 0x0C); // FolderName.Num (incl null)
            if (!IsGamePtr(strPtr) || strLen <= 1 || strLen > 512) continue;
            string? name = Base.ReadUniDirect(strPtr, strLen - 1);
            if (!string.IsNullOrEmpty(name)) names[folderId] = name;
        }
        return names;
    }

    // ── Folder combo ─────────────────────────────────────────────

    // preserveView: keep the currently selected folder (if it still exists)
    // and the current page — a REFRESH should not throw the user back to
    // "All folders", page 1. A failed scan / RESET passes false.
    private void RepopulateFolderCombo(bool preserveView)
    {
        int prevFolder = preserveView && CboFolder.SelectedItem is FolderEntry prev
            ? prev.FolderID : int.MinValue;
        int prevPage = preserveView ? currentPage : 0;

        _suppressFilterEvent = true;
        CboFolder.Items.Clear();

        // Every cached item now comes from the authoritative
        // ItemBoxEquipments enumeration — all real, no heuristic filter.
        var groups = cachedItems
            .GroupBy(ci => ci.FolderID)
            .Select(g => new { FolderID = g.Key, Count = g.Count() })
            .OrderBy(g => g.FolderID)
            .ToList();

        CboFolder.Items.Add(new FolderEntry(int.MinValue, "All folders (" + cachedItems.Count + ")"));
        foreach (var g in groups)
        {
            // FolderID -1 = items sitting loose in the forge box (not in
            // any user folder) \u2014 label it "Forge" instead of "Folder -1".
            string folderName;
            if (_folderNames.TryGetValue(g.FolderID, out string? realName) && !string.IsNullOrEmpty(realName))
                folderName = realName;
            else if (g.FolderID == -1)
                folderName = "Forge";
            else
                folderName = "Folder " + g.FolderID;
            folderName += "  \u2014  " + g.Count + " item" + (g.Count == 1 ? "" : "s");
            CboFolder.Items.Add(new FolderEntry(g.FolderID, folderName));
        }

        int selectIdx = 0;
        if (prevFolder != int.MinValue)
            for (int i = 0; i < CboFolder.Items.Count; i++)
                if (CboFolder.Items[i] is FolderEntry fe && fe.FolderID == prevFolder) { selectIdx = i; break; }
        if (CboFolder.Items.Count > 0)
            CboFolder.SelectedIndex = selectIdx;

        _suppressFilterEvent = false;
        currentPage = prevPage; // PopulateCards clamps to the new page count
        PopulateCards();
    }

    // ── Filtering ────────────────────────────────────────────────

    // Selection / bulk is always available now \u2014 bulk MAX is class-aware
    // (each item maxed only with its compatible stats), so a mixed-type
    // selection is safe; no need to filter to one equipment type first.
    // Uses _visibleFiltered (what PopulateCards last rendered) rather than
    // re-filtering + re-sorting the whole cache on every click/keystroke.
    private void UpdateBulkButton()
    {
        bool active = selectedAddresses.Count > 0;
        BtnBulk.IsEnabled = active;
        if (BtnBulk.Content is StackPanel sp && sp.Children.Count >= 2 && sp.Children[1] is TextBlock tb)
            tb.Text = active ? "BULK (" + selectedAddresses.Count + ")" : "BULK EDIT";

        BtnSelectAll.IsEnabled = true;
        var filtered = _visibleFiltered;
        bool allSelected = filtered.Count > 0 && filtered.All(ci => selectedAddresses.Contains(ci.Address));
        BtnSelectAllLabel.Text = allSelected ? "CLEAR" : "SELECT ALL";
        BtnSelectAllIcon.Text = allSelected ? "\uE8E6" : "\uE762"; // ClearSelection / SelectAll glyphs
    }

    // "  \u2014  N selected (M hidden by filter)" or the Ctrl+click hint. Shared
    // by PopulateCards and UpdateSelectionStatus so the two never drift.
    // Items that couldn't be read on the last scan. Rendered into the status
    // line (which every page/filter render rebuilds, so it can't be set once
    // and forgotten) because a silently short list is indistinguishable from
    // a genuinely smaller forge.
    private int _lastScanFailed;

    private string ScanWarningSuffix()
        => _lastScanFailed > 0 ? "  —  " + _lastScanFailed + " unreadable (rescan)" : "";

    private string SelectionSuffix()
    {
        if (selectedAddresses.Count == 0) return "  \u2014  Ctrl+click to select";
        int visible = 0;
        foreach (var ci in _visibleFiltered)
            if (selectedAddresses.Contains(ci.Address)) visible++;
        int hidden = selectedAddresses.Count - visible;
        string s = "  \u2014  " + selectedAddresses.Count + " selected";
        if (hidden > 0) s += " (" + hidden + " hidden by filter)";
        return s;
    }

    // Toggle the "selected" look on a card without rebuilding anything else.
    // Mirrors the initial assignment inside CreateCard().
    private void ApplySelectedVisual(Border card, bool isSelected)
    {
        card.BorderThickness = new Thickness(isSelected ? 2 : 1);
        card.BorderBrush = isSelected
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("BorderBrush");
        card.Background = isSelected ? _selectedCardBrush : (Brush)FindResource("SurfaceLightBrush");
        SetSelectionMarker(card, isSelected);
    }

    private void SetSelectionMarker(Border card, bool isSelected)
    {
        if (card.Child is not Grid grid) return;

        for (int i = grid.Children.Count - 1; i >= 0; i--)
        {
            if (grid.Children[i] is FrameworkElement { Tag: SelectionMarkerTag })
                grid.Children.RemoveAt(i);
        }

        if (!isSelected) return;

        grid.Children.Add(new Border
        {
            Tag = SelectionMarkerTag,
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Child = new TextBlock
            {
                Text = "\u2713",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
    }

    // Re-compute just the status-line suffix so the "N selected" counter
    // stays current after a selection toggle. Keeps PopulateCards' exact
    // format so the visible string doesn't jitter.
    private void UpdateSelectionStatus()
    {
        if (LblStatus == null) return;
        string baseText = LblStatus.Text;
        int dashIdx = baseText.IndexOf('\u2014');
        if (dashIdx > 0) baseText = baseText.Substring(0, dashIdx).TrimEnd();

        LblStatus.Text = baseText + SelectionSuffix();
    }

    private List<CachedItem> GetFilteredItems()
    {
        IEnumerable<CachedItem> q = cachedItems;

        if (CboFolder.SelectedItem is FolderEntry folder && folder.FolderID != int.MinValue)
            q = q.Where(ci => ci.FolderID == folder.FolderID);

        if (CboType.SelectedItem is TypeEntry type && !type.IsAll)
            q = q.Where(ci => type.Matches(ci.User.EquipmentType));

        string? needle = TxtSearch.Text;
        if (!string.IsNullOrWhiteSpace(needle))
        {
            needle = needle.Trim();
            q = q.Where(ci => ci.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var list = q.ToList();
        ApplySort(list);
        return list;
    }

    private void ApplySort(List<CachedItem> list)
    {
        SortMode mode = CboSort.SelectedItem is SortEntry entry ? entry.Mode : SortMode.Quality;
        Comparison<CachedItem> primary = mode switch
        {
            SortMode.MaxLevelDesc     => (a, b) => b.User.MaxLevel.CompareTo(a.User.MaxLevel),
            SortMode.MaxLevelAsc      => (a, b) => a.User.MaxLevel.CompareTo(b.User.MaxLevel),
            SortMode.LevelDesc        => (a, b) => b.User.Level.CompareTo(a.User.Level),
            SortMode.HeroDamageDesc   => (a, b) => b.User.HeroDamage.CompareTo(a.User.HeroDamage),
            SortMode.TowerDamageDesc  => (a, b) => b.User.TowerDamage.CompareTo(a.User.TowerDamage),
            SortMode.WeaponDamageDesc => (a, b) => b.User.Damage.CompareTo(a.User.Damage),
            SortMode.BestStat         => (a, b) => StatTotal(b.User).CompareTo(StatTotal(a.User)),
            SortMode.NameAsc          => (a, b) => string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase),
            _                         => (a, b) => QualityRank(b.User.Quality2).CompareTo(QualityRank(a.User.Quality2)),
        };
        // List.Sort is unstable and most keys tie heavily (quality has ~20
        // values across ~1,000 items), so without a total order the page
        // contents reshuffled on every REFRESH. Name, then address, makes
        // the order deterministic across scans.
        list.Sort((a, b) =>
        {
            int c = primary(a, b);
            if (c != 0) return c;
            c = string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return ((uint)a.Address).CompareTo((uint)b.Address);
        });
    }

    // ── Card rendering ───────────────────────────────────────────

    private void PopulateCards()
    {
        if (CardPanel == null) return;
        CardPanel.Children.Clear();

        var list = GetFilteredItems();
        _visibleFiltered = list;
        int totalPages = Math.Max(1, (list.Count + PageSize - 1) / PageSize);
        if (currentPage >= totalPages) currentPage = totalPages - 1;
        if (currentPage < 0) currentPage = 0;

        int start = currentPage * PageSize;
        int end = Math.Min(start + PageSize, list.Count);
        const bool selectionMode = true; // selection is always on (class-aware bulk MAX)

        for (int i = start; i < end; i++)
        {
            var ci = list[i];
            var card = CreateCard(ci, selectionMode);
            CardPanel.Children.Add(card);
        }

        // Any repopulate (page change, filter, search, scan) starts the
        // view back at the top rather than keeping a stale scroll offset.
        CardScroller.ScrollToTop();

        BtnPrev.IsEnabled = currentPage > 0;
        BtnNext.IsEnabled = currentPage < totalPages - 1;
        LblPage.Text = "Page " + (currentPage + 1) + " / " + totalPages;
        UpdateBulkButton();

        LblStatus.Text = "Showing " + (end - start) + " of " + list.Count + " filtered"
                       + ScanWarningSuffix() + SelectionSuffix();
        UpdateEmptyState();
    }

    // ── Item card ────────────────────────────────────────────────────
    // Laid out like DD1's own item panel (see DECISIONS.md, 2026-09-04):
    // level and type top-left, forged-by top-right, name centred, the
    // quality word leading straight into the description, then icon rows
    // split by rule lines — primary stats as round icons, hero and tower
    // bonuses as square tiles, weapon extras round again. Numbers sit under
    // their icon. No per-tile boxes: the earlier bordered-pill grid gave
    // every stat the same weight and read as a generic dashboard.
    //
    // Rows pack their live stats left (a zero stat is simply omitted) — the
    // icon families already say which group a tile belongs to.
    private Border CreateCard(CachedItem ci, bool selectionMode)
    {
        bool isSelected = selectionMode && selectedAddresses.Contains(ci.Address);
        var u = ci.User;
        var qBrush = new SolidColorBrush(GetAccentColor(u.Quality2));
        var tColor = GetTypeColor(u.EquipmentType);
        var textPrimary   = (Brush)FindResource("TextPrimaryBrush");
        var textSecondary = (Brush)FindResource("TextSecondaryBrush");
        var textMuted     = (Brush)FindResource("TextMutedBrush");

        var card = new Border
        {
            Width = 256,
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            BorderBrush = isSelected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
            Background = isSelected ? _selectedCardBrush : (Brush)FindResource("SurfaceLightBrush"),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Tag = ci.Address
        };
        // SetSelectionMarker overlays its check onto this Grid — keep it.
        var outerGrid = new Grid();
        card.Child = outerGrid;
        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 10) };
        outerGrid.Children.Add(body);

        // ── Top strip: [TYPE] Lv x / y ............ FORGED BY / Name ──
        // Forged-by takes the game's two-line form (small label over the
        // name) and every pixel the left group doesn't use, so a long forger
        // name isn't squeezed by an inline "forged by" prefix.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(46, tColor.R, tColor.G, tColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, tColor.R, tColor.G, tColor.B)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = TypeLabel(u.EquipmentType).ToUpperInvariant(),
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(tColor)
            }
        });
        left.Children.Add(new TextBlock
        {
            Text = "Lv " + u.Level.ToString("N0") + " / " + u.MaxLevel.ToString("N0"),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = textSecondary,
            VerticalAlignment = VerticalAlignment.Center
        });
        top.Children.Add(left);

        if (!string.IsNullOrWhiteSpace(ci.ForgerName))
        {
            // The game's two-line form: a small FORGED BY label over the name,
            // right-aligned, and the name keeps its own line breaks (a coloured
            // two-line forger is common). The strip may grow on THIS side only:
            // the tag/level group is pinned to the top, so it never slides down
            // with a taller forged-by block — that slide was the earlier bug,
            // not the second line itself.
            var forged = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10, 0, 0, 0)
            };
            forged.Children.Add(new TextBlock
            {
                Text = "FORGED BY",
                FontSize = 7.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = textMuted,
                HorizontalAlignment = HorizontalAlignment.Right
            });
            var forger = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = 13,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = 13 * 3   // three lines at most; beyond that, trim
            };
            AppendColorRuns(forger, ci.ForgerName, textSecondary);
            forged.Children.Add(forger);
            Grid.SetColumn(forged, 1);
            top.Children.Add(forged);
        }
        body.Children.Add(top);

        // ── Name, centred. Custom names can carry <color> runs too. ──
        var name = new TextBlock
        {
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 40,
            Margin = new Thickness(0, 6, 0, 0)
        };
        AppendColorRuns(name, string.IsNullOrWhiteSpace(ci.Name) ? "(unnamed)" : ci.Name, textPrimary);
        body.Children.Add(name);

        // ── "Ultimate++ The last gift bestowed to Etheria" ──
        var desc = new TextBlock
        {
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 13.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 13.5 * 3,
            Margin = new Thickness(4, 2, 4, 0)
        };
        string qualityText = QualityDisplay.Name(u.Quality2);
        if (u.Quality3 != Quality3.None) qualityText += " " + u.Quality3;
        desc.Inlines.Add(new System.Windows.Documents.Run(qualityText) { Foreground = qBrush, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrWhiteSpace(ci.Description))
        {
            desc.Inlines.Add(new System.Windows.Documents.Run(" "));
            AppendColorRuns(desc, ci.Description, textSecondary);
        }
        body.Children.Add(desc);

        // ── Stat rows ──
        // Weapons AND familiars (pets) carry damage; everything else shows
        // the four resistances.
        bool isDamageItem = u.EquipmentType == EquipmentType.Weapon
                         || u.EquipmentType == EquipmentType.Familiar;
        if (isDamageItem)
        {
            AddIconRow(body, new (string?, string, int)[]
            {
                ("/Assets/Icons/weapon_damage.png",  "Attack",    u.Damage),
                ("/Assets/Icons/weapon_ranged.png",  "Ranged",    u.RangedDamage),
                ("/Assets/Icons/resist_generic.png", "Elemental", u.ElementalDamage?.Value ?? 0),
            }, round: true, plus: false, iconSize: 28, fontSize: 13);
        }
        else
        {
            AddIconRow(body, new (string?, string, int)[]
            {
                ("/Assets/Icons/resist_generic.png",   "Generic",   u.Generic?.Value   ?? 0),
                ("/Assets/Icons/resist_poison.png",    "Poison",    u.Poison?.Value    ?? 0),
                ("/Assets/Icons/resist_fire.png",      "Fire",      u.Fire?.Value      ?? 0),
                ("/Assets/Icons/resist_lightning.png", "Lightning", u.Lightning?.Value ?? 0),
            }, round: true, plus: false, iconSize: 28, fontSize: 13);
        }
        AddIconRow(body, new (string?, string, int)[]
        {
            ("/Assets/Icons/hero_health.png",  "Health",  u.HeroHealth),
            ("/Assets/Icons/hero_speed.png",   "Speed",   u.HeroSpeed),
            ("/Assets/Icons/hero_damage.png",  "Damage",  u.HeroDamage),
            ("/Assets/Icons/hero_casting.png", "Casting", u.HeroCasting),
        }, round: false, plus: true, iconSize: 22, fontSize: 12);
        AddIconRow(body, new (string?, string, int)[]
        {
            ("/Assets/Icons/tower_health.png", "Health", u.TowerHealth),
            ("/Assets/Icons/tower_speed.png",  "Speed",  u.TowerSpeed),
            ("/Assets/Icons/tower_damage.png", "Damage", u.TowerDamage),
            ("/Assets/Icons/tower_range.png",  "Range",  u.TowerRange),
        }, round: false, plus: true, iconSize: 22, fontSize: 12);
        AddIconRow(body, new (string?, string, int)[]
        {
            ("/Assets/Icons/weapon_knockback.png",   "Knockback",   u.Knockback),
            ("/Assets/Icons/weapon_projectiles.png", "Projectiles", u.NumberOfProjectiles),
            ("/Assets/Icons/weapon_projspeed.png",   "Proj Spd",    u.SpeedOfProjectiles),
            ("/Assets/Icons/weapon_shotspersec.png", "Shots/s",     u.ShotsPerSecond),
            ("/Assets/Icons/weapon_reload.png",      "Reload",      u.ReloadSpeed),
            ("/Assets/Icons/weapon_chargespeed.png", "Charge",      u.ChargeSpeed),
            ("/Assets/Icons/weapon_clipammo.png",    "Clip",        u.ClipAmmo),
            ("/Assets/Icons/weapon_blocking.png",    "Block",       u.Blocking),
        }, round: true, plus: true, iconSize: 22, fontSize: 12);

        // ── Selection checkmark ──
        if (isSelected)
            SetSelectionMarker(card, true);

        // ── Hover ──
        card.MouseEnter += (s, e) => { if (!selectedAddresses.Contains(ci.Address)) card.Background = (Brush)FindResource("SurfaceLighterBrush"); };
        card.MouseLeave += (s, e) => { if (!selectedAddresses.Contains(ci.Address)) card.Background = (Brush)FindResource("SurfaceLightBrush"); };

        // ── Click ──
        // Mutate only the clicked card's visuals + the set — rebuilding
        // every card with PopulateCards() is O(N) per click and visibly
        // lags with 50+ items on screen.
        card.MouseLeftButtonUp += (s, e) =>
        {
            if (selectionMode && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                bool nowSelected;
                if (selectedAddresses.Contains(ci.Address))
                {
                    selectedAddresses.Remove(ci.Address);
                    nowSelected = false;
                }
                else
                {
                    selectedAddresses.Add(ci.Address);
                    nowSelected = true;
                }
                ApplySelectedVisual(card, nowSelected);
                UpdateBulkButton();
                UpdateSelectionStatus();
            }
        };
        card.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) OpenItemEditor(ci.Address, ci.Name ?? ""); };

        return card;
    }

    // DD1 <color:r,g,b> runs → coloured inlines. Uncoloured text takes
    // `defaultBrush`; newlines inside a run become LineBreaks.
    private static void AppendColorRuns(TextBlock target, string markup, Brush defaultBrush, FontWeight? weight = null)
    {
        foreach (ColorRun r in ColorMarkup.Parse(markup))
        {
            string[] lines = r.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) target.Inlines.Add(new System.Windows.Documents.LineBreak());
                if (lines[i].Length == 0) continue;
                var run = new System.Windows.Documents.Run(lines[i])
                {
                    Foreground = r.HasColor ? new SolidColorBrush(Color.FromRgb(r.R, r.G, r.B)) : defaultBrush
                };
                if (weight is FontWeight w) run.FontWeight = w;
                target.Inlines.Add(run);
            }
        }
    }

    // One rule-separated row of icon tiles, four to a line, wrapping past
    // four. Zero-valued stats are omitted and the rest pack left.
    private void AddIconRow(Panel body, (string? icon, string label, int value)[] slots,
                            bool round, bool plus, double iconSize, double fontSize)
    {
        bool any = false;
        foreach (var s in slots) if (s.value != 0) { any = true; break; }
        if (!any) return;

        body.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("BorderBrush"),
            Opacity = 0.55,
            Margin = new Thickness(0, 8, 0, 8)
        });
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var (icon, label, value) in slots)
        {
            if (value == 0) continue;
            grid.Children.Add(MakeIconTile(icon, label, value, round, plus, iconSize, fontSize));
        }
        body.Children.Add(grid);
    }

    // Icon over number over a small label, centred. Round for an item's own
    // stats (damage / resists / weapon extras), square for hero and tower
    // bonuses — the same visual grammar the game's panel uses.
    private FrameworkElement MakeIconTile(string? iconPath, string label, int value,
                                          bool round, bool plus, double iconSize, double fontSize)
    {
        var col = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
            // Exact value on hover — load-bearing once large values are
            // compacted to "50K" / "12.5M" on the face.
            ToolTip = label + ": " + value.ToString("N0")
        };

        ImageBrush? iconBrush = GetStatIconBrush(iconPath);
        if (iconBrush != null)
        {
            col.Children.Add(new Border
            {
                Width = iconSize,
                Height = iconSize,
                CornerRadius = new CornerRadius(round ? iconSize / 2 : 4),
                Background = iconBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            col.Children.Add(new TextBlock
            {
                Text = ((char)0x2726).ToString(), // four-pointed star placeholder
                FontSize = iconSize - 8,
                Height = iconSize,
                Foreground = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
        }

        var number = new TextBlock
        {
            Text = (plus && value > 0 ? "+" : "") + FormatStatValue(value),
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        col.Children.Add(number);

        col.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 8,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        });
        return col;
    }

    internal static string FormatStatValue(int v)
    {
        // long, not int: a garbage read of 0x80000000 (int.MinValue) would
        // make Math.Abs(int) throw OverflowException on the render path.
        long a = Math.Abs((long)v);
        if (a >= 1_000_000)
        {
            double m = v / 1_000_000.0;
            return (m == Math.Floor(m) ? m.ToString("0") : m.ToString("0.#")) + "M";
        }
        if (a > 10_000)
        {
            double k = v / 1_000.0;
            return (k == Math.Floor(k) ? k.ToString("0") : k.ToString("0.#")) + "K";
        }
        return v.ToString("N0");
    }

    private ImageBrush? GetStatIconBrush(string? iconPath)
    {
        if (iconPath == null) return null;
        if (_statIconBrushes.TryGetValue(iconPath, out ImageBrush? cached)) return cached;

        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,," + iconPath, UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            _statIconBrushes[iconPath] = brush;
            return brush;
        }
        catch
        {
            _statIconBrushes[iconPath] = null;
            return null;
        }
    }

    // Centered placeholder shown whenever no cards are on screen: a pre-scan
    // prompt vs a "filters exclude everything" message. Called from every
    // path that can empty the card panel (scan fail, reset, filter change).
    private void UpdateEmptyState()
    {
        bool hasCards = CardPanel != null && CardPanel.Children.Count > 0;
        EmptyState.Visibility = hasCards ? Visibility.Collapsed : Visibility.Visible;
        PaginationBar.Visibility = hasCards ? Visibility.Visible : Visibility.Collapsed;
        if (hasCards) return;
        bool scanned = cachedItems.Count > 0;
        EmptyText.Text = scanned
            ? "No items match these filters."
            : "Click SCAN ALL to populate the forge view.";
        EmptyIcon.Text = ((char)(scanned ? 0xE721 : 0xE71C)).ToString();
    }

    private void OpenItemEditor(int address, string name)
    {
        try
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null)
            {
                Base.RaiseMessage($"MainWindow is null. App.MainWindow type: {Application.Current.MainWindow?.GetType().Name ?? "null"}", "Error");
                return;
            }
            if (mainWindow.ContentArea == null)
            {
                Base.RaiseMessage("ContentArea is null.", "Error");
                return;
            }
            // Go through ShowEditor so the forge gets pushed onto the
            // editor's back-stack; otherwise BACK falls through to home.
            mainWindow.ShowEditor(address, Base.Genus.Item, name ?? "(unnamed)");
        }
        catch (Exception ex)
        {
            Base.RaiseMessage($"Failed to open editor: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", "Error");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "(unnamed)";
        if (s.Length > max) return s.Substring(0, max - 1) + "\u2026";
        return s;
    }

    private static int StatTotal(ItemUser u)
    {
        return u.HeroHealth + u.HeroSpeed + u.HeroDamage + u.HeroCasting + u.HeroSkill1 + u.HeroSkill2
            + u.TowerHealth + u.TowerSpeed + u.TowerDamage + u.TowerRange;
    }

    // Everything the search box matches against. Beyond the free text it
    // covers what a user actually types to find gear: quality ("ultimate",
    // "supreme"), the type both raw and as the card shows it ("ArmorBoots"
    // / "Boots"), the base (archetype) name, and the folder name.
    private static string BuildSearchHaystack(CachedItem ci, Dictionary<int, string> folderNames)
    {
        var sb = new StringBuilder(192);
        sb.Append(ci.Name ?? "").Append(' ');
        sb.Append(ci.BaseName ?? "").Append(' ');
        sb.Append(ci.Description ?? "").Append(' ');
        sb.Append(ci.ForgerName ?? "").Append(' ');
        sb.Append(QualityDisplay.Name(ci.User.Quality2)).Append(' ');
        if (ci.User.Quality3 != Quality3.None) sb.Append(ci.User.Quality3).Append(' ');
        sb.Append(ci.User.EquipmentType).Append(' ');
        sb.Append(TypeLabel(ci.User.EquipmentType)).Append(' ');
        if (folderNames.TryGetValue(ci.FolderID, out string? folder) && !string.IsNullOrEmpty(folder))
            sb.Append(folder).Append(' ');
        sb.Append("Level ").Append(ci.User.Level).Append(' ');
        sb.Append("MaxLevel ").Append(ci.User.MaxLevel);
        return sb.ToString();
    }

    private static int QualityRank(Quality2 q)
    {
        return QualityDisplay.Rank(q);
    }

    // internal: HeroViewerView reuses it for equipment-row quality dots.
    internal static Color GetAccentColor(Quality2 q) => QualityColors.Get(q);

    // Friendly type label for the card meta line ("ArmorBoots" → "Boots").
    // internal: HeroViewerView reuses it for equipment-row meta lines.
    internal static string TypeLabel(EquipmentType t)
    {
        string s = t.ToString();
        return s.StartsWith("Armor") ? s.Substring(5) : s;
    }

    // Card strip / legend colour, keyed to the item category so every
    // weapon shares one hue, every armour piece another, etc.
    // internal: HeroViewerView reuses it for equipment mini-card strips.
    internal static Color GetTypeColor(EquipmentType t)
    {
        return t switch
        {
            EquipmentType.Weapon
                => Color.FromRgb(200, 70, 60),    // weapons — red
            EquipmentType.ArmorHelmet or EquipmentType.ArmorTorso
                or EquipmentType.ArmorBoots or EquipmentType.ArmorGloves
                => Color.FromRgb(70, 130, 200),   // armour — steel blue
            EquipmentType.Hat or EquipmentType.ArmGuard
                or EquipmentType.Shield or EquipmentType.Mask
                => Color.FromRgb(150, 90, 200),   // accessories — violet
            EquipmentType.Familiar
                => Color.FromRgb(70, 175, 95),    // pets — green
            _ => Color.FromRgb(120, 125, 135)     // anything else — neutral
        };
    }

    private static string CsvEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ── Name reading (ported from original) ──────────────────────

    private static string SafeReadUni(int address, string field)
    {
        try
        {
            string v = Base.ReadUni<ItemNative>(address, field);
            return v ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool LooksLikeRealName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length < 2 || s.Length > 80) return false;
        int printable = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 0x20 && c < 0x7F) printable++;
            else if (c >= 0xA0 && c < 0xFFFE) printable++;
        }
        return printable >= s.Length - 1;
    }

    private static bool LooksLikeItemName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length < 3 || s.Length > 60) return false;
        int letters = 0, spaces = 0, others = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letters++;
            else if (c == ' ' || c == '\'' || c == '-') spaces++;
            else others++;
        }
        if (letters < s.Length / 2) return false;
        if (others > 2) return false;
        if (!char.IsLetter(s[0])) return false;
        return true;
    }

    private static string? ScanMemoryForName(int centerAddr, int radiusBefore, int radiusAfter)
    {
        try
        {
            // Unsigned math: DD1 is LARGEADDRESSAWARE, so an item above 2 GB
            // has a NEGATIVE int address. Signed `centerAddr - radiusBefore`
            // went negative, tripped the low-address clamp, and the fallback
            // silently scanned from 0x10000 instead of around the item.
            long start = (long)(uint)centerAddr - radiusBefore;
            if (start < 0x10000) start = 0x10000;
            int size = radiusBefore + radiusAfter;
            if (size <= 0) return null;

            byte[] block;
            try { block = Base.Instance.ReadMemory(unchecked((int)(uint)start), size); }
            catch { return null; }

            // NativeArray pointer scan, 4-byte aligned
            for (int off = 0; off + 12 <= block.Length; off += 4)
            {
                int ptr = BitConverter.ToInt32(block, off);
                int curLen = BitConverter.ToInt32(block, off + 4);
                int maxLen = BitConverter.ToInt32(block, off + 8);

                if ((uint)ptr < 0x100000u) continue;
                if ((ptr & 1) != 0) continue;
                if (curLen < 3 || curLen > 80) continue;
                if (maxLen < curLen || maxLen > 256) continue;

                try
                {
                    byte[] strBytes = Base.Instance.ReadMemory(ptr, (curLen - 1) * 2);
                    string s = Encoding.Unicode.GetString(strBytes);
                    if (LooksLikeItemName(s)) return s;
                }
                catch { }
            }

            // Inline UTF-16 string scan, 2-byte aligned
            for (int off = 0; off + 10 <= block.Length; off += 2)
            {
                int maxChars = Math.Min(80, (block.Length - off) / 2);
                int len = 0;
                bool ok = true;
                while (len < maxChars)
                {
                    ushort c = (ushort)(block[off + len * 2] | (block[off + len * 2 + 1] << 8));
                    if (c == 0) break;
                    if (c < 0x20 || c >= 0x7F) { ok = false; break; }
                    len++;
                }
                if (!ok || len < 5 || len > 60) continue;
                string s = Encoding.Unicode.GetString(block, off, len * 2);
                if (LooksLikeItemName(s)) return s;
            }
        }
        catch { }
        return null;
    }

    private static string SafeReadName(int address, ItemNative native, ItemUser user)
    {
        string custom = SafeReadUni(address, "EquipmentName");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;

        string baseName = SafeReadUni(address, "BaseEquipmentName");
        if (!string.IsNullOrWhiteSpace(baseName)) return baseName;

        int tmpl = native.EquipmentTemplate;
        if ((uint)tmpl >= 0x100000u && (tmpl & 3) == 0)
        {
            int archetypeProps = tmpl + 56;
            string tmplBase = SafeReadUni(archetypeProps, "BaseEquipmentName");
            if (!string.IsNullOrWhiteSpace(tmplBase)) return tmplBase;
            string tmplName = SafeReadUni(archetypeProps, "EquipmentName");
            if (!string.IsNullOrWhiteSpace(tmplName)) return tmplName;
            string tmplBase2 = SafeReadUni(tmpl, "BaseEquipmentName");
            if (!string.IsNullOrWhiteSpace(tmplBase2)) return tmplBase2;
            string tmplName2 = SafeReadUni(tmpl, "EquipmentName");
            if (!string.IsNullOrWhiteSpace(tmplName2)) return tmplName2;
        }

        string descr = SafeReadUni(address, "Description");
        if (!string.IsNullOrWhiteSpace(descr)) return descr;

        string? scanned = ScanMemoryForName(address, 256, 2048);
        if (scanned != null) return scanned;

        if ((uint)tmpl >= 0x100000u && (tmpl & 3) == 0)
        {
            string? archScanned = ScanMemoryForName(tmpl, 256, 2048);
            if (archScanned != null) return archScanned;
        }

        string typeLabel = user.EquipmentType.ToString();
        if (typeLabel.StartsWith("Armor"))
            typeLabel = typeLabel.Substring(5);
        return user.Quality2 + " " + typeLabel;
    }
}
