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
    // no dedicated asset — MakeStatTile renders a glyph placeholder for it.
    private const string SelectionMarkerTag = "SelectionMarker";

    private readonly Dictionary<string, ImageBrush?> _statIconBrushes = new();
    private readonly SolidColorBrush _selectedCardBrush = new(Color.FromArgb(30, 88, 101, 242));

    private List<int> forgeResults = new();
    private List<CachedItem> cachedItems = new();
    // Addresses (he+0x38) that came from a hero's HeroEquipments rather
    // than the forge ItemBox — used to tag snapshot items so the picker's
    // Source dropdown can filter Forge vs Hero.
    private readonly HashSet<int> _heroResultAddrs = new();

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
        BuildTypeLegend();
    }

    // Colour key for the per-category card strips. Swatches come from
    // GetTypeColor itself so the legend can never drift from the cards.
    private void BuildTypeLegend()
    {
        var entries = new (string label, EquipmentType type)[]
        {
            ("Weapon",    EquipmentType.Weapon),
            ("Armor",     EquipmentType.ArmorTorso),
            ("Accessory", EquipmentType.Hat),
            ("Familiar",  EquipmentType.Familiar),
        };
        foreach (var (label, type) in entries)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(GetTypeColor(type)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            TypeLegend.Children.Add(row);
        }
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
        RunForgeScan();
    }

    private async void RunForgeScan()
    {
        // Re-entrancy guard: the Source combo's SelectionChanged also calls
        // this, and it stays enabled during the multi-second recalibrate
        // await — a second concurrent scan would interleave with the first.
        if (!BtnScan.IsEnabled) return;
        if (!Base.OpenProcess()) return;

        BtnScan.IsEnabled = false;
        try
        {
            LblStatus.Text = "Reading items...";
            List<int>? items = EnumerateItemAddresses();

            if (items == null && Window.GetWindow(this) is Modinator.MainWindow mw0)
            {
                // Cheap retry first: a single stale read in the
                // pawn→HeroManager chain (common right after a map change /
                // game restart) yields null. Drop the cached pawn-scan + AK
                // handle and resolve once more before the heavier path.
                mw0.InvalidatePawnScanCache();
                items = EnumerateItemAddresses();
            }

            if (items == null && Window.GetWindow(this) is Modinator.MainWindow mw)
            {
                // Auto-recalibrate (self-healing): the cheap retry didn't
                // help, so re-derive the WorldInfo + pawn-vtable seed
                // structurally from live memory — exactly what Settings →
                // CALIBRATE does — on a background thread so the UI stays
                // responsive, then try the enumeration once more. This is
                // the "if the forge fails, run forge calibration by default"
                // behaviour: the user never has to open Settings for the
                // common post-patch / post-restart miss. ForceStructuralReseed
                // re-pins the seed and leaves a freshly-validated WorldInfo
                // cached, which ResolvePlayerPawnAddress then reuses.
                LblStatus.Text = "Recalibrating from live memory...";
                await System.Threading.Tasks.Task.Run(() => mw.ForceStructuralReseed());
                items = EnumerateItemAddresses();
            }

            if (items == null)
            {
                // Staged diagnosis: not running / 64-bit unsupported /
                // menu (no pawn) / chain broke — say which.
                string why = GameChain.DescribeScanFailure(_lastResolvedPawn);
                Base.RaiseMessage(why, "Forge Viewer");
                LblStatus.Text = "Items not reachable — " + why;
                OnScanFail();
                return;
            }

            forgeResults = items;
            if (forgeResults.Count == 0) OnScanFail();
            else OnScanSuccess();
        }
        finally
        {
            BtnScan.IsEnabled = true;
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
    // Forge  = HeroManager.ItemBoxEquipments TArray @ +0x39C
    // Hero   = HeroManager.ActiveHeroes TArray @ +0x36C, then each
    //          UDunDefHero.HeroEquipments TArray @ +0x5B0
    // Every element is a UHeroEquipment*; its inline ItemNative starts at
    // +0x38 (same layout as forge items / floor drops), so we return
    // he+0x38 and the existing ReadAllItems/ItemNative/edit pipeline is
    // unchanged for both sources.
    private List<int>? EnumerateItemAddresses()
    {
        int heroMgr = ResolveHeroManager();
        if (!IsGamePtr(heroMgr)) return null;

        var mode = (CboSource.SelectedItem as SourceEntry)?.Mode ?? SourceMode.Forge;
        var list = new List<int>();
        _heroResultAddrs.Clear();

        if (mode == SourceMode.Forge || mode == SourceMode.All)
            foreach (int he in ReadPtrArray(heroMgr + 0x39C))      // ItemBoxEquipments
                list.Add(he + 0x38);

        if (mode == SourceMode.Hero || mode == SourceMode.All)
            foreach (int hero in ReadPtrArray(heroMgr + 0x36C))    // ActiveHeroes
                foreach (int he in ReadPtrArray(hero + 0x5B0))     // UDunDefHero.HeroEquipments
                {
                    int addr = he + 0x38;
                    list.Add(addr);
                    _heroResultAddrs.Add(addr);
                }

        return list;
    }

    // Last pawn the chain resolution saw — feeds the staged failure
    // message (distinguishes "no character" from "chain broke").
    private int _lastResolvedPawn;

    private int ResolveHeroManager()
    {
        if (Window.GetWindow(this) is not Modinator.MainWindow main) return 0;
        _lastResolvedPawn = main.ResolvePlayerPawnAddress();
        return GameChain.ResolveHeroManager(_lastResolvedPawn);
    }

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

        var dlg = new BulkEditDialog(addresses, typeLabel);
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
                            cachedItems[i].SearchText = BuildSearchHaystack(cachedItems[i]);
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

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvent) return;
        currentPage = 0;
        selectedAddresses.Clear();
        PopulateCards();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        currentPage = 0;
        selectedAddresses.Clear();
        PopulateCards();
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
        RepopulateFolderCombo();
        UpdateEmptyState();
    }

    private void OnScanSuccess()
    {
        ReadAllItems();
        ReadFolderNames();
        RepopulateFolderCombo();
        // Once we have a cache, flip the primary button into REFRESH mode —
        // pressing it just reruns the same scan path.
        if (forgeResults.Count > 0)
        {
            BtnScanLabel.Text = "REFRESH";
            BtnScanIcon.Text = "\uE72C"; // Refresh arrows glyph
        }
    }

    // ── Read all items from memory ───────────────────────────────

    private void ReadAllItems()
    {
        cachedItems.Clear();
        int structSize = Marshal.SizeOf(typeof(ItemNative));
        for (int i = 0; i < forgeResults.Count; i++)
        {
            int address = forgeResults[i];
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
                    IsHero = _heroResultAddrs.Contains(address)
                };
                cached.SearchText = BuildSearchHaystack(cached);
                cachedItems.Add(cached);
            }
            catch { }
        }

        PublishSnapshot();
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

    private void ReadFolderNames()
    {
        _folderNames.Clear();
        int heroMgr = ResolveHeroManager();
        if (!IsGamePtr(heroMgr)) return;

        int dataPtr = RdPtr(heroMgr + 0x98);   // ItemFolders TArray.Data
        int num     = RdInt(heroMgr + 0x9C);   // ItemFolders TArray.Num
        if (!IsGamePtr(dataPtr) || num <= 0 || num > 100000) return;

        const int Stride = 24;                 // sizeof(FItemFolder)
        byte[]? block;
        try { block = Base.Instance.ReadMemory(dataPtr, num * Stride); }
        catch { return; }
        if (block == null || block.Length < num * Stride) return;

        for (int i = 0; i < num; i++)
        {
            int b = i * Stride;
            int folderId = BitConverter.ToInt32(block, b + 0x04); // FItemFolder.FolderID
            int strPtr   = BitConverter.ToInt32(block, b + 0x08); // FolderName.Data
            int strLen   = BitConverter.ToInt32(block, b + 0x0C); // FolderName.Num (incl null)
            if (!IsGamePtr(strPtr) || strLen <= 1 || strLen > 512) continue;
            string? name = Base.ReadUniDirect(strPtr, strLen - 1);
            if (!string.IsNullOrEmpty(name)) _folderNames[folderId] = name;
        }
    }

    // ── Folder combo ─────────────────────────────────────────────

    private void RepopulateFolderCombo()
    {
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

        if (CboFolder.Items.Count > 0)
            CboFolder.SelectedIndex = 0;

        _suppressFilterEvent = false;
        currentPage = 0;
        PopulateCards();
    }

    // ── Filtering ────────────────────────────────────────────────

    // Selection / bulk is always available now \u2014 bulk MAX is class-aware
    // (each item maxed only with its compatible stats), so a mixed-type
    // selection is safe; no need to filter to one equipment type first.
    private void UpdateBulkButton()
    {
        bool active = selectedAddresses.Count > 0;
        BtnBulk.IsEnabled = active;
        if (BtnBulk.Content is StackPanel sp && sp.Children.Count >= 2 && sp.Children[1] is TextBlock tb)
            tb.Text = active ? "BULK (" + selectedAddresses.Count + ")" : "BULK EDIT";

        BtnSelectAll.IsEnabled = true;
        var filtered = GetFilteredItems();
        bool allSelected = filtered.Count > 0 && filtered.All(ci => selectedAddresses.Contains(ci.Address));
        BtnSelectAllLabel.Text = allSelected ? "CLEAR" : "SELECT ALL";
        BtnSelectAllIcon.Text = allSelected ? "\uE8E6" : "\uE762"; // ClearSelection / SelectAll glyphs
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

        string sel = selectedAddresses.Count > 0
            ? "  \u2014  " + selectedAddresses.Count + " selected" : "";
        string hint = selectedAddresses.Count == 0
            ? "  \u2014  Ctrl+click to select" : "";
        LblStatus.Text = baseText + sel + hint;
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
        switch (mode)
        {
            case SortMode.MaxLevelDesc:
                list.Sort((a, b) => b.User.MaxLevel.CompareTo(a.User.MaxLevel)); break;
            case SortMode.MaxLevelAsc:
                list.Sort((a, b) => a.User.MaxLevel.CompareTo(b.User.MaxLevel)); break;
            case SortMode.LevelDesc:
                list.Sort((a, b) => b.User.Level.CompareTo(a.User.Level)); break;
            case SortMode.HeroDamageDesc:
                list.Sort((a, b) => b.User.HeroDamage.CompareTo(a.User.HeroDamage)); break;
            case SortMode.TowerDamageDesc:
                list.Sort((a, b) => b.User.TowerDamage.CompareTo(a.User.TowerDamage)); break;
            case SortMode.WeaponDamageDesc:
                list.Sort((a, b) => b.User.Damage.CompareTo(a.User.Damage)); break;
            case SortMode.BestStat:
                list.Sort((a, b) => StatTotal(b.User).CompareTo(StatTotal(a.User))); break;
            case SortMode.NameAsc:
                list.Sort((a, b) => string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase)); break;
            default:
                list.Sort((a, b) => QualityRank(b.User.Quality2).CompareTo(QualityRank(a.User.Quality2))); break;
        }
    }

    // ── Card rendering ───────────────────────────────────────────

    private void PopulateCards()
    {
        if (CardPanel == null) return;
        CardPanel.Children.Clear();

        var list = GetFilteredItems();
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

        string sel = selectionMode && selectedAddresses.Count > 0
            ? "  \u2014  " + selectedAddresses.Count + " selected" : "";
        string hint = selectionMode && selectedAddresses.Count == 0
            ? "  \u2014  Ctrl+click to select" : "";
        LblStatus.Text = "Showing " + (end - start) + " of " + list.Count + " filtered" + sel + hint;
        UpdateEmptyState();
    }

    private Border CreateCard(CachedItem ci, bool selectionMode)
    {
        bool isSelected = selectionMode && selectedAddresses.Contains(ci.Address);
        var qColor = GetAccentColor(ci.User.Quality2);
        var qBrush = new SolidColorBrush(qColor);
        // Header gradient is keyed to the item *category* (weapon / armor /
        // accessory / familiar), not quality — quality stays as the text.
        var tColor = GetTypeColor(ci.User.EquipmentType);

        string qualityText = QualityDisplay.Name(ci.User.Quality2);
        if (ci.User.Quality3 != Quality3.None)
            qualityText += "  \u00b7  " + ci.User.Quality3;

        // ── Card shell — narrower + tighter so the board reads denser ──
        var card = new Border
        {
            Width = 240,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            BorderBrush = isSelected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
            Background = (Brush)FindResource("SurfaceLightBrush"),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Tag = ci.Address
        };
        if (isSelected)
            card.Background = _selectedCardBrush;

        var outerGrid = new Grid();
        card.Child = outerGrid;

        var mainStack = new StackPanel();
        outerGrid.Children.Add(mainStack);

        // ── Category identity — a restrained 3 px strip across the top
        //    (the legend in the command bar is its key). Replaces the old
        //    tall saturated gradient wash, which drowned the content and
        //    forced drop shadows on every piece of header text.
        mainStack.Children.Add(new Border
        {
            Height = 3,
            // Top corners follow the card radius — Border.ClipToBounds is
            // rectangular, so without this the strip pokes past the curve.
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = new SolidColorBrush(tColor)
        });

        // ── Header — left-aligned: name, then quality · type · level,
        //    then a one-line description. No shadows/plates needed on a
        //    flat surface.
        var headerStack = new StackPanel { Margin = new Thickness(10, 7, 10, 8) };

        headerStack.Children.Add(new TextBlock
        {
            Text = ci.Name ?? "(unnamed)",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36
        });

        var meta = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        meta.Inlines.Add(new System.Windows.Documents.Run(qualityText)
        {
            Foreground = qBrush,
            FontWeight = FontWeights.Bold,
            FontSize = 10.5
        });
        meta.Inlines.Add(new System.Windows.Documents.Run(
            "  ·  " + TypeLabel(ci.User.EquipmentType) +
            "  ·  Lv " + ci.User.Level + " / " + ci.User.MaxLevel)
        {
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontSize = 10
        });
        headerStack.Children.Add(meta);

        if (!string.IsNullOrWhiteSpace(ci.Description))
            headerStack.Children.Add(new TextBlock
            {
                Text = ci.Description,
                FontSize = 9.5,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            });

        mainStack.Children.Add(headerStack);

        mainStack.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("BorderBrush"),
            Opacity = 0.5,
            Margin = new Thickness(10, 0, 10, 0)
        });

        // ── Stats body — DD1-style icon tiles (label + framed icon plate
        //    + value). Fixed row order mirrors the in-game item panel:
        //    primary (weapon damages / armor resists) → hero → tower →
        //    everything else. Skill bonuses are intentionally omitted. ──
        var body = new StackPanel { Margin = new Thickness(10, 6, 10, 9) };
        mainStack.Children.Add(body);

        var u = ci.User;
        // Weapons AND familiars (pets) carry damage — both show the damage
        // row in slot 1. Everything else (armor/accessories) shows resists.
        bool isDamageItem = u.EquipmentType == EquipmentType.Weapon
                         || u.EquipmentType == EquipmentType.Familiar;

        // Row 1 — primary, stretched edge-to-edge (the headline stats get
        // the width). Damage items: Attack / Ranged / Elemental (the
        // type-neutral energy-orb icon — elemental and resists never
        // appear on the same card, so reuse doesn't collide).
        // Armor/accessories: the 4 resistances. Zero-value tiles drop.
        if (isDamageItem)
        {
            AddStatRow(body, new System.Collections.Generic.List<(string?, string, int)>
            {
                ("/Assets/Icons/weapon_damage.png",  "Attack",    u.Damage),
                ("/Assets/Icons/weapon_ranged.png",  "Ranged",    u.RangedDamage),
                ("/Assets/Icons/resist_generic.png", "Elemental", u.ElementalDamage?.Value ?? 0),
            }, primaryRow: true, plus: false);
        }
        else
        {
            AddStatRow(body, new System.Collections.Generic.List<(string?, string, int)>
            {
                ("/Assets/Icons/resist_generic.png",   "Generic",   u.Generic?.Value   ?? 0),
                ("/Assets/Icons/resist_poison.png",    "Poison",    u.Poison?.Value    ?? 0),
                ("/Assets/Icons/resist_fire.png",      "Fire",      u.Fire?.Value      ?? 0),
                ("/Assets/Icons/resist_lightning.png", "Lightning", u.Lightning?.Value ?? 0),
            }, primaryRow: true, plus: false);
        }

        // Everything else — ONE continuous 3-column grid in fixed role
        // order (hero → tower → misc). Per-group rows produced ragged
        // 3+1 orphan lines with misaligned widths; a single aligned grid
        // keeps every secondary tile the same size with no orphans, and
        // the icon families still signal the grouping.
        AddStatRow(body, new System.Collections.Generic.List<(string?, string, int)>
        {
            ("/Assets/Icons/hero_health.png",  "Hero HP",  u.HeroHealth),
            ("/Assets/Icons/hero_speed.png",   "Hero Spd", u.HeroSpeed),
            ("/Assets/Icons/hero_damage.png",  "Hero Dmg", u.HeroDamage),
            ("/Assets/Icons/hero_casting.png", "Casting",  u.HeroCasting),
            ("/Assets/Icons/tower_health.png", "Tower HP",  u.TowerHealth),
            ("/Assets/Icons/tower_speed.png",  "Tower Spd", u.TowerSpeed),
            ("/Assets/Icons/tower_damage.png", "Tower Dmg", u.TowerDamage),
            ("/Assets/Icons/tower_range.png",  "Tower Rng", u.TowerRange),
            ("/Assets/Icons/weapon_knockback.png",   "Knockback",   u.Knockback),
            ("/Assets/Icons/weapon_projectiles.png", "Projectiles", u.NumberOfProjectiles),
            ("/Assets/Icons/weapon_projspeed.png",   "Proj Spd",    u.SpeedOfProjectiles),
            ("/Assets/Icons/weapon_shotspersec.png", "Shots/s",     u.ShotsPerSecond),
            ("/Assets/Icons/weapon_reload.png",      "Reload",      u.ReloadSpeed),
            ("/Assets/Icons/weapon_chargespeed.png", "Charge",      u.ChargeSpeed),
            ("/Assets/Icons/weapon_clipammo.png",    "Clip",        u.ClipAmmo),
            ("/Assets/Icons/weapon_blocking.png",    "Block",       u.Blocking),
        }, primaryRow: false, plus: true);

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

    // One DD1-style stat row: a UniformGrid of tiles spread edge-to-edge
    // across the card (4 across; >4 wraps). Any zero-value tile is dropped
    // and an all-zero row omitted. `primaryRow` = larger tiles.
    private void AddStatRow(Panel body,
                            System.Collections.Generic.List<(string? icon, string label, int value)> stats,
                            bool primaryRow, bool plus)
    {
        int liveCount = 0;
        foreach (var stat in stats)
        {
            if (stat.value != 0) liveCount++;
        }
        if (liveCount == 0) return;

        // Equal-width aligned tiles. Primary row: as many columns as live
        // stats, so the headline damage/resist tiles stretch edge-to-edge
        // across the card. Secondary grid: fixed 3 columns — wide enough
        // for "+10,000" exact (the base-stat ceiling), and every tile the
        // same size so rows stay visually consistent.
        var gridRow = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = primaryRow ? Math.Min(liveCount, 4) : 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = body.Children.Count > 0 ? new Thickness(0, 2, 0, 0) : new Thickness(0)
        };
        foreach (var (icon, label, value) in stats)
        {
            if (value == 0) continue;
            gridRow.Children.Add(MakeStatTile(icon, label, value, primaryRow, plus));
        }
        body.Children.Add(gridRow);
    }

    // Tile number formatting: base stats (ceiling ~10,000) stay exact;
    // bigger rolls compact so they fit a fixed-width tile — above 10,000
    // → "50K", a million and up → "12.5M". The tile tooltip always
    // carries the exact value.
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

    // A single stat tile: a compact inset box — icon + value on the first
    // line, the label underneath. Same visual language as the Item Dupe
    // card tiles. The Elemental sentinel and any unreadable icon fall back
    // to a small star glyph in the icon slot.
    private FrameworkElement MakeStatTile(string? iconPath, string label, int value, bool primary, bool plus)
    {
        double iconSize = primary ? 15 : 13;

        var valRow = new StackPanel { Orientation = Orientation.Horizontal };

        ImageBrush? iconBrush = GetStatIconBrush(iconPath);
        if (iconBrush != null)
        {
            valRow.Children.Add(new Border
            {
                Width = iconSize,
                Height = iconSize,
                CornerRadius = new CornerRadius(3),
                Background = iconBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
        }
        else
        {
            valRow.Children.Add(new TextBlock
            {
                Text = ((char)0x2726).ToString(), // four-pointed star = elemental/placeholder
                FontSize = iconSize - 2,
                Foreground = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
        }

        valRow.Children.Add(new TextBlock
        {
            Text = (plus && value > 0 ? "+" : "") + FormatStatValue(value),
            FontSize = primary ? 11.5 : 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var stack = new StackPanel();
        stack.Children.Add(valRow);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 8.5,
            Foreground = (Brush)FindResource("TextMutedBrush")
        });

        return new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 4, 4),
            // Exact value on hover — load-bearing once large values are
            // compacted to "50K" / "12.5M" on the tile face.
            ToolTip = label + ": " + value.ToString("N0"),
            Child = stack
        };
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

    private static string BuildSearchHaystack(CachedItem ci)
    {
        var sb = new StringBuilder(128);
        sb.Append(ci.Name ?? "").Append(' ');
        sb.Append(ci.Description ?? "").Append(' ');
        sb.Append(ci.ForgerName ?? "").Append(' ');
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
            int start = centerAddr - radiusBefore;
            if (start < 0x10000) start = 0x10000;
            int size = radiusBefore + radiusAfter;
            if (size <= 0) return null;

            byte[] block;
            try { block = Base.Instance.ReadMemory(start, size); }
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
