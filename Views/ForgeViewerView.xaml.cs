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

    private class QualityEntry
    {
        public int MinRank;
        public string Label;
        public QualityEntry(int minRank, string label) { MinRank = minRank; Label = label; }
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

    private const int PageSize = 60;

    private List<int> forgeResults = new();
    private List<CachedItem> cachedItems = new();
    // Addresses (he+0x38) that came from a hero's HeroEquipments rather
    // than the forge ItemBox — used to tag snapshot items so the picker's
    // Source dropdown can filter Forge vs Hero.
    private readonly HashSet<int> _heroResultAddrs = new();

    // Snapshot of the most recent forge read, exposed so other views (the
    // Clone Source picker) can surface the "real items" list without having
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
        _suppressFilterEvent = true;
        PopulateSourceCombo();
        PopulateTypeCombo();
        PopulateQualityCombo();
        PopulateSortCombo();
        _suppressFilterEvent = false;
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

    private void PopulateQualityCombo()
    {
        CboQuality.Items.Add(new QualityEntry(-1, "Any"));
        CboQuality.Items.Add(new QualityEntry(16, "Ultimate"));
        CboQuality.Items.Add(new QualityEntry(15, "Supreme+"));
        CboQuality.Items.Add(new QualityEntry(14, "Transcendent+"));
        CboQuality.Items.Add(new QualityEntry(13, "Mythical+"));
        CboQuality.Items.Add(new QualityEntry(12, "Godly+"));
        CboQuality.Items.Add(new QualityEntry(11, "Legendary+"));
        CboQuality.Items.Add(new QualityEntry(10, "Epic+"));
        CboQuality.Items.Add(new QualityEntry(9, "Amazing+"));
        CboQuality.SelectedIndex = 0;
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

    private void RunForgeScan()
    {
        if (!Base.OpenProcess()) return;

        BtnScan.IsEnabled = false;
        LblStatus.Text = "Reading items...";
        try
        {
            List<int>? items = EnumerateItemAddresses();
            if (items == null)
            {
                Base.RaiseMessage(
                    "Could not reach the forge / hero items.\r\n\r\n" +
                    "Make sure the game is running and your hero is in a map or the tavern " +
                    "(items aren't loaded in menus / during loading).",
                    "Forge Viewer");
                LblStatus.Text = "Items not reachable.";
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

    private int ResolveHeroManager()
    {
        if (Window.GetWindow(this) is not Modinator.MainWindow main) return 0;
        int pawn = main.ResolvePlayerPawnAddress();
        if (!IsGamePtr(pawn)) return 0;
        int controller = RdPtr(pawn + 0x22C);
        int player     = RdPtr(controller + 0x3B8);
        int vpClient   = RdPtr(player + 0x194);
        return RdPtr(vpClient + 0xCFC);
    }

    // Reads a UE3 TArray<T*> header (data ptr, Num, Max) at `tarrayAddr`
    // and returns the live element pointers. Defensive Num cap; bad reads
    // yield an empty list (a hero with no equipment, etc.).
    private static List<int> ReadPtrArray(int tarrayAddr)
    {
        var result = new List<int>();
        int dataPtr = RdPtr(tarrayAddr);
        int num     = RdInt(tarrayAddr + 4);
        if (!IsGamePtr(dataPtr) || num <= 0 || num > 200000) return result;

        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, num * 4); }
        catch { return result; }
        if (arr == null || arr.Length < num * 4) return result;

        for (int i = 0; i < num; i++)
        {
            int p = BitConverter.ToInt32(arr, i * 4);
            if (IsGamePtr(p)) result.Add(p);
        }
        return result;
    }

    // DD1 is LARGEADDRESSAWARE on WOW64 — heap sits anywhere in
    // [0x01000000, 0xFFFE0000). Matches MainWindow.IsHeapPtr.
    private static bool IsGamePtr(int p)
        => (uint)p >= 0x01000000u && (uint)p < 0xFFFE0000u;

    private static int RdPtr(int addr)
    {
        if (!IsGamePtr(addr)) return 0;
        try
        {
            byte[] b = Base.Instance.ReadMemory(addr, 4);
            return (b != null && b.Length >= 4) ? BitConverter.ToInt32(b, 0) : 0;
        }
        catch { return 0; }
    }

    private static int RdInt(int addr)
    {
        try
        {
            byte[] b = Base.Instance.ReadMemory(addr, 4);
            return (b != null && b.Length >= 4) ? BitConverter.ToInt32(b, 0) : 0;
        }
        catch { return 0; }
    }

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
        if (CboQuality.Items.Count > 0) CboQuality.SelectedIndex = 0;
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

                cachedItems.Add(new CachedItem
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
                });
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
        var scoped = cachedItems.ToList();
        var groups = scoped
            .GroupBy(ci => ci.FolderID)
            .Select(g => new { FolderID = g.Key, Count = g.Count() })
            .OrderBy(g => g.FolderID)
            .ToList();

        CboFolder.Items.Add(new FolderEntry(int.MinValue, "All folders (" + scoped.Count + ")"));
        foreach (var g in groups)
        {
            string folderName;
            if (_folderNames.TryGetValue(g.FolderID, out string? realName) && !string.IsNullOrEmpty(realName))
                folderName = realName + "  \u2014  " + g.Count + " item" + (g.Count == 1 ? "" : "s");
            else
                folderName = "Folder " + g.FolderID + "  \u2014  " + g.Count + " item" + (g.Count == 1 ? "" : "s");
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
        card.Background = isSelected
            ? new SolidColorBrush(Color.FromArgb(30, 88, 101, 242))
            : (Brush)FindResource("SurfaceLightBrush");
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

        if (CboQuality.SelectedItem is QualityEntry qual && qual.MinRank >= 0)
            q = q.Where(ci => QualityRank(ci.User.Quality2) >= qual.MinRank);

        string? needle = TxtSearch.Text;
        if (!string.IsNullOrWhiteSpace(needle))
        {
            needle = needle.Trim();
            q = q.Where(ci => BuildSearchHaystack(ci).Contains(needle, StringComparison.OrdinalIgnoreCase));
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

        BtnPrev.IsEnabled = currentPage > 0;
        BtnNext.IsEnabled = currentPage < totalPages - 1;
        LblPage.Text = "Page " + (currentPage + 1) + " / " + totalPages;
        UpdateBulkButton();

        string sel = selectionMode && selectedAddresses.Count > 0
            ? "  \u2014  " + selectedAddresses.Count + " selected" : "";
        string hint = selectionMode && selectedAddresses.Count == 0
            ? "  \u2014  Ctrl+click to select" : "";
        LblStatus.Text = "Showing " + (end - start) + " of " + list.Count + " filtered" + sel + hint;
    }

    private Border CreateCard(CachedItem ci, bool selectionMode)
    {
        bool isSelected = selectionMode && selectedAddresses.Contains(ci.Address);
        var qColor = GetAccentColor(ci.User.Quality2);
        var qBrush = new SolidColorBrush(qColor);

        string qualityText = ci.User.Quality2.ToString();
        if (ci.User.Quality3 != Quality3.None)
            qualityText += "  \u00b7  " + ci.User.Quality3;

        // ── Card shell ──
        var card = new Border
        {
            Width = 310,
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            BorderBrush = isSelected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
            Background = (Brush)FindResource("SurfaceLightBrush"),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Tag = ci.Address
        };
        if (isSelected)
            card.Background = new SolidColorBrush(Color.FromArgb(30, 88, 101, 242));

        var outerGrid = new Grid();
        card.Child = outerGrid;

        var mainStack = new StackPanel();
        outerGrid.Children.Add(mainStack);

        // ── Quality header band — gradient from quality color ──
        var headerBorder = new Border
        {
            Padding = new Thickness(14, 8, 14, 8),
            Background = new LinearGradientBrush(
                Color.FromArgb(50, qColor.R, qColor.G, qColor.B),
                Color.FromArgb(0, qColor.R, qColor.G, qColor.B),
                0)
        };
        var headerStack = new StackPanel();
        headerBorder.Child = headerStack;

        // Name
        headerStack.Children.Add(new TextBlock
        {
            Text = ci.Name ?? "(unnamed)",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        // Quality + Type row
        var qualRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        qualRow.Children.Add(new TextBlock
        {
            Text = qualityText,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = qBrush
        });
        var typeBadge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(40, qColor.R, qColor.G, qColor.B)),
            Padding = new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = ci.User.EquipmentType.ToString(),
                FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = qBrush
            }
        };
        DockPanel.SetDock(typeBadge, Dock.Right);
        qualRow.Children.Insert(0, typeBadge);
        headerStack.Children.Add(qualRow);
        // Level
        headerStack.Children.Add(new TextBlock
        {
            Text = "Level " + ci.User.Level + " / " + ci.User.MaxLevel,
            FontSize = 10, Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        mainStack.Children.Add(headerBorder);

        // ── Stats body ──
        var body = new StackPanel { Margin = new Thickness(14, 6, 14, 10) };
        mainStack.Children.Add(body);

        // Hero Stats
        var heroGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition());
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition());
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition());
        heroGrid.RowDefinitions.Add(new RowDefinition());
        heroGrid.RowDefinitions.Add(new RowDefinition());
        AddIconStat(heroGrid, 0, 0, "/Assets/Icons/hero_health.png", "Hero HP", ci.User.HeroHealth);
        AddIconStat(heroGrid, 0, 1, "/Assets/Icons/hero_damage.png", "Hero Dmg", ci.User.HeroDamage);
        AddIconStat(heroGrid, 0, 2, "/Assets/Icons/hero_speed.png", "Hero Spd", ci.User.HeroSpeed);
        AddIconStat(heroGrid, 1, 0, "/Assets/Icons/hero_casting.png", "Casting", ci.User.HeroCasting);
        AddIconStat(heroGrid, 1, 1, null, "Skill 1", ci.User.HeroSkill1);
        AddIconStat(heroGrid, 1, 2, null, "Skill 2", ci.User.HeroSkill2);
        body.Children.Add(heroGrid);

        // Tower Stats
        var towerGrid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        towerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        towerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        towerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        towerGrid.RowDefinitions.Add(new RowDefinition());
        AddIconStat(towerGrid, 0, 0, "/Assets/Icons/tower_health.png", "Tower HP", ci.User.TowerHealth);
        AddIconStat(towerGrid, 0, 1, "/Assets/Icons/tower_damage.png", "Tower Dmg", ci.User.TowerDamage);
        AddIconStat(towerGrid, 0, 2, "/Assets/Icons/tower_range.png", "Tower Rng", ci.User.TowerRange);
        body.Children.Add(towerGrid);

        // Weapon stats (if any non-zero)
        bool hasWeapon = ci.User.Damage != 0 || ci.User.RangedDamage != 0 || ci.User.Knockback != 0
            || ci.User.NumberOfProjectiles != 0 || ci.User.ReloadSpeed != 0;
        if (hasWeapon)
        {
            body.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 2, 0, 4), Opacity = 0.4 });
            var wpnRow = new WrapPanel();
            if (ci.User.Damage != 0) AddChip(wpnRow, "/Assets/Icons/weapon_damage.png", "Damage", ci.User.Damage);
            if (ci.User.RangedDamage != 0) AddChip(wpnRow, "/Assets/Icons/weapon_ranged.png", "Ranged", ci.User.RangedDamage);
            if (ci.User.Knockback != 0) AddChip(wpnRow, "/Assets/Icons/weapon_knockback.png", "Knockback", ci.User.Knockback);
            if (ci.User.NumberOfProjectiles != 0) AddChip(wpnRow, "/Assets/Icons/weapon_projectiles.png", "Projectiles", ci.User.NumberOfProjectiles);
            if (ci.User.ReloadSpeed != 0) AddChip(wpnRow, "/Assets/Icons/weapon_reload.png", "Reload", ci.User.ReloadSpeed);
            if (ci.User.ChargeSpeed != 0) AddChip(wpnRow, "/Assets/Icons/weapon_chargespeed.png", "Charge", ci.User.ChargeSpeed);
            if (ci.User.ShotsPerSecond != 0) AddChip(wpnRow, "/Assets/Icons/weapon_shotspersec.png", "Shots/s", ci.User.ShotsPerSecond);
            if (ci.User.ClipAmmo != 0) AddChip(wpnRow, "/Assets/Icons/weapon_clipammo.png", "Clip", ci.User.ClipAmmo);
            if (ci.User.Blocking != 0) AddChip(wpnRow, "/Assets/Icons/weapon_blocking.png", "Block", ci.User.Blocking);
            body.Children.Add(wpnRow);
        }

        // Resistance
        bool hasResist = (ci.User.Generic?.Value ?? 0) != 0 || (ci.User.Fire?.Value ?? 0) != 0
            || (ci.User.Poison?.Value ?? 0) != 0 || (ci.User.Lightning?.Value ?? 0) != 0;
        if (hasResist)
        {
            body.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 2, 0, 4), Opacity = 0.4 });
            var resistRow = new WrapPanel();
            if ((ci.User.Generic?.Value ?? 0) != 0) AddChip(resistRow, "/Assets/Icons/resist_generic.png", "Generic", ci.User.Generic!.Value);
            if ((ci.User.Poison?.Value ?? 0) != 0) AddChip(resistRow, "/Assets/Icons/resist_poison.png", "Poison", ci.User.Poison!.Value);
            if ((ci.User.Fire?.Value ?? 0) != 0) AddChip(resistRow, "/Assets/Icons/resist_fire.png", "Fire", ci.User.Fire!.Value);
            if ((ci.User.Lightning?.Value ?? 0) != 0) AddChip(resistRow, "/Assets/Icons/resist_lightning.png", "Lightning", ci.User.Lightning!.Value);
            body.Children.Add(resistRow);
        }

        // Description
        if (!string.IsNullOrWhiteSpace(ci.Description))
        {
            body.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 3, 0, 4), Opacity = 0.3 });
            body.Children.Add(new TextBlock
            {
                Text = ci.Description,
                FontSize = 9.5, FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34
            });
        }

        // ── Selection checkmark ──
        if (isSelected)
        {
            outerGrid.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0),
                Child = new TextBlock { Text = "\u2713", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        }

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
        card.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) OpenItemEditor(ci.Address, ci.Name); };

        return card;
    }

    private void AddIconStat(Grid grid, int row, int col, string? iconPath, string label, int value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 1, 4, 1) };
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (iconPath != null)
        {
            try
            {
                valRow.Children.Add(new Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath, UriKind.Relative)),
                    Width = 13, Height = 13, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
                });
            }
            catch { }
        }
        valRow.Children.Add(new TextBlock
        {
            Text = value.ToString("N0"),
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(valRow);
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(iconPath != null ? 16 : 0, 0, 0, 0)
        });
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, col);
        grid.Children.Add(panel);
    }

    private void AddChip(WrapPanel parent, string iconPath, string label, int value)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            Padding = new Thickness(5, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 3)
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        try
        {
            row.Children.Add(new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath, UriKind.Relative)),
                Width = 11, Height = 11, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center
            });
        }
        catch { }
        row.Children.Add(new TextBlock { Text = label + " ", FontSize = 9, Foreground = (Brush)FindResource("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = value.ToString("N0"), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        chip.Child = row;
        parent.Children.Add(chip);
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
        return q switch
        {
            Quality2.Ultimate => 16,
            Quality2.Supreme => 15,
            Quality2.Transcendent => 14,
            Quality2.Mythical => 13,
            Quality2.Godly => 12,
            Quality2.Legendary => 11,
            Quality2.Epic => 10,
            Quality2.Amazing => 9,
            Quality2.Powerful => 8,
            Quality2.Shining => 7,
            Quality2.Polished => 6,
            Quality2.Sturdy => 5,
            Quality2.Solid => 4,
            Quality2.Stocky => 3,
            Quality2.Worn => 2,
            Quality2.Torn => 1,
            Quality2.Cursed => 0,
            _ => -1
        };
    }

    private static Color GetAccentColor(Quality2 q)
    {
        return q switch
        {
            Quality2.Ultimate => Color.FromRgb(200, 140, 20),
            Quality2.Supreme => Color.FromRgb(130, 70, 190),
            Quality2.Transcendent => Color.FromRgb(40, 140, 200),
            Quality2.Mythical => Color.FromRgb(200, 50, 80),
            Quality2.Godly => Color.FromRgb(200, 160, 30),
            Quality2.Legendary => Color.FromRgb(200, 110, 30),
            Quality2.Epic => Color.FromRgb(140, 60, 200),
            Quality2.Amazing => Color.FromRgb(50, 160, 70),
            Quality2.Powerful => Color.FromRgb(80, 150, 90),
            Quality2.Shining => Color.FromRgb(170, 160, 50),
            Quality2.Polished or Quality2.Sturdy or Quality2.Solid or Quality2.Stocky
                => Color.FromRgb(120, 125, 135),
            Quality2.Worn or Quality2.Torn => Color.FromRgb(100, 100, 105),
            Quality2.Cursed => Color.FromRgb(80, 30, 80),
            _ => Color.FromRgb(120, 125, 135)
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
