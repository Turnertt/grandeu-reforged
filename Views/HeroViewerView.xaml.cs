using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Modinator.Views;

// Hero deck viewer — the Forge Viewer's sibling for heroes. Walks the
// same authoritative HeroManager chain (DD1_INTERNALS.md §3), then
// ActiveHeroes instead of ItemBoxEquipments:
//
//   playerPawn +0x22C → ADunDefPlayerController
//              +0x3B8 → Player (ULocalPlayer)
//              +0x194 → ViewportClient (UDunDefViewportClient)
//              +0xCFC → TheHeroManager (UDunDefHeroManager)
//   Heroes  = HeroManager.ActiveHeroes TArray @ +0x36C (UDunDefHero*)
//
// Per-hero fields (v10.0 SDK, DD_UDKGame_classes.hpp `class UDunDefHero`):
//   +0x0424  HeroClassDisplayName (FString — localized class name)
//   +0x0444  ClassNameColor (FLinearColor, 4 floats RGBA)
//   +0x0504  the HeroNative block (StatModifiers[10] / Level / LevelCap /
//            LevelCapDemo / Experience / … / HeroName@+0x564) — the same
//            address Hero Search edits, so ShowEditor(Genus.Hero) works.
//   +0x053C  ManaPower (the hero's banked mana)
//   +0x05B0  HeroEquipments TArray (UHeroEquipment*; ItemNative @ he+0x38)
//   +0x05C8  HeroWeaponEquipment (the held weapon's UHeroEquipment*)
//   +0x05D8  BasedOnHeroTemplate (fallback for class name/color)
//
// Read-only view: all writes happen in the existing Hero/Item editors it
// opens. All reads via Base.Instance (the sanctioned Scanner path).
public partial class HeroViewerView : UserControl
{
    private const int OFF_LOCAL_HEROES       = 0x360; // UDunDefHeroManager.LocalLoadedHeroes (full roster, stride 8)
    private const int OFF_ACTIVE_HEROES      = 0x36C; // UDunDefHeroManager.ActiveHeroes (in-play only, stride 4)
    private const int OFF_CLASS_DISPLAYNAME  = 0x424; // UDunDefHero.HeroClassDisplayName
    private const int OFF_HERO_NATIVE        = 0x504; // UDunDefHero.HeroHealthModifier — HeroNative base
    private const int OFF_HERO_MANA          = 0x53C; // UDunDefHero.ManaPower
    private const int OFF_HERO_EQUIPMENTS    = 0x5B0; // UDunDefHero.HeroEquipments
    private const int OFF_HERO_WEAPON        = 0x5C8; // UDunDefHero.HeroWeaponEquipment
    private const int OFF_HERO_TEMPLATE      = 0x5D8; // UDunDefHero.BasedOnHeroTemplate
    private const int OFF_HE_ITEMNATIVE      = 0x38;  // UHeroEquipment → inline ItemNative

    public HeroViewerView()
    {
        InitializeComponent();
    }

    // ── Scan ─────────────────────────────────────────────────────────

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        // Re-entrancy guard: a second click during the multi-second
        // recalibrate await would run a concurrent scan (racing the first
        // ForceStructuralReseed) and append duplicate hero cards.
        if (!BtnScan.IsEnabled) return;
        BtnScan.IsEnabled = false;
        CardPanel.Children.Clear();
        // Ensure the Scanner is attached to the live game before any reads
        // — matches the Forge Viewer. Without this the Hero scan could run
        // against a not-yet-attached / stale Scanner and leave attachment
        // state inconsistent for a later Calibrate.
        if (!Base.OpenProcess())
        {
            string off = GameChain.DescribeScanFailure(0);
            LblStatus.Text = off;
            UpdateEmptyState(off);
            BtnScan.IsEnabled = true;
            return;
        }
        try
        {
            int heroMgr = ResolveHeroManager();

            // Auto-recalibrate (self-healing), mirroring the Forge Viewer:
            // if the chain didn't resolve, re-derive the WorldInfo + seed
            // structurally from live memory in the background, then retry
            // once — no trip to Settings → CALIBRATE for the common
            // post-patch / post-restart miss.
            if (heroMgr == 0 && Window.GetWindow(this) is MainWindow mw)
            {
                mw.InvalidatePawnScanCache();
                heroMgr = ResolveHeroManager();
                if (heroMgr == 0)
                {
                    LblStatus.Text = "Recalibrating from live memory...";
                    await System.Threading.Tasks.Task.Run(() => mw.ForceStructuralReseed());
                    heroMgr = ResolveHeroManager();
                }
            }

            if (heroMgr == 0)
            {
                // Staged diagnosis — not running / 64-bit unsupported /
                // menu (no pawn) / chain broke.
                string why = GameChain.DescribeScanFailure(_lastResolvedPawn);
                LblStatus.Text = why;
                UpdateEmptyState(why);
                return;
            }

            // LocalLoadedHeroes (+0x360, stride 8) is the player's FULL
            // hero roster — verified live to hold every saved hero, vs
            // ActiveHeroes (+0x36C, stride 4) which is only the in-play
            // hero(es) and made the viewer look like it showed just one.
            // Fall back to ActiveHeroes if the full list is somehow empty.
            var heroes = GameChain.ReadPtrArray(heroMgr + OFF_LOCAL_HEROES, 8);
            if (heroes.Count == 0)
                heroes = ReadPtrArray(heroMgr + OFF_ACTIVE_HEROES);

            int shown = 0;
            foreach (int hero in heroes)
            {
                var card = TryBuildHeroCard(hero);
                if (card != null) { CardPanel.Children.Add(card); shown++; }
            }

            LblStatus.Text = shown > 0
                ? $"Showing {shown} hero{(shown == 1 ? "" : "es")}"
                : "No heroes found — make sure your hero deck is loaded, then rescan.";
            UpdateEmptyState("No heroes found.\nMake sure your hero deck is loaded, then rescan.");
        }
        catch (Exception ex)
        {
            LblStatus.Text = "Scan failed: " + ex.Message;
            UpdateEmptyState("Scan failed — is the game running?");
        }
        finally { BtnScan.IsEnabled = true; }
    }

    private void UpdateEmptyState(string emptyText)
    {
        bool hasCards = CardPanel.Children.Count > 0;
        EmptyState.Visibility = hasCards ? Visibility.Collapsed : Visibility.Visible;
        if (!hasCards) EmptyText.Text = emptyText;
    }

    // ── Card construction ────────────────────────────────────────────

    private Border? TryBuildHeroCard(int hero)
    {
        int nativeAddr = hero + OFF_HERO_NATIVE;
        HeroNative native;
        HeroUser u;
        try
        {
            int size = Marshal.SizeOf(typeof(HeroNative));
            native = Base.Push<HeroNative>(Base.Instance.ReadMemory(nativeAddr, size));
            u = Base.HeroToUser(native);
        }
        catch { return null; }

        // Sanity: a real hero has a plausible level. Protects against a
        // stale ActiveHeroes entry pointing at freed memory.
        if (native.Level < 0 || native.Level > 1000) return null;

        string heroName = StripColorTags(SafeReadHeroName(nativeAddr));
        if (string.IsNullOrWhiteSpace(heroName)) heroName = "(unnamed hero)";

        // Class display name (instance, archetype template as fallback).
        // One accent colour for every class — per-class colours read noisy.
        string className = ReadFString(hero + OFF_CLASS_DISPLAYNAME);
        if (string.IsNullOrWhiteSpace(className))
        {
            int tmpl = RdPtr(hero + OFF_HERO_TEMPLATE);
            if (IsGamePtr(tmpl))
                className = ReadFString(tmpl + OFF_CLASS_DISPLAYNAME);
        }
        if (string.IsNullOrWhiteSpace(className)) className = "Hero";

        // ── Shell ──
        var card = new Border
        {
            Width = 340,
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Background = (Brush)FindResource("SurfaceLightBrush"),
            ClipToBounds = true
        };
        var mainStack = new StackPanel();
        card.Child = new Grid { Children = { mainStack } };

        // ── Header band — a slightly lighter strip so the name reads as a
        //    title bar, with a class pill in the single accent colour. ──
        var headerBand = new Border
        {
            Background = (Brush)FindResource("SurfaceLighterBrush"),
            Padding = new Thickness(14, 11, 14, 11),
            CornerRadius = new CornerRadius(10, 10, 0, 0)
        };
        var header = new StackPanel();
        headerBand.Child = header;

        header.Children.Add(new TextBlock
        {
            Text = heroName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        // Class pill
        metaRow.Children.Add(new Border
        {
            Background = (Brush)FindResource("AccentSubtleBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = className,
                Foreground = (Brush)FindResource("AccentBrush"),
                FontWeight = FontWeights.Bold,
                FontSize = 10.5
            }
        });
        metaRow.Children.Add(new TextBlock
        {
            Text = $"Lv {native.Level} / {native.MaxLevel}",
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        header.Children.Add(metaRow);
        mainStack.Children.Add(headerBand);

        // ── Stats ──
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 4) };
        mainStack.Children.Add(body);
        body.Children.Add(SectionLabel("S T A T S"));

        // Row 1 — the four hero stats.
        var heroRow = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 7, 0, 0) };
        AddTile(heroRow, "/Assets/Icons/hero_health.png",  "Hero HP",  u.HeroHealth);
        AddTile(heroRow, "/Assets/Icons/hero_speed.png",   "Hero Spd", u.HeroSpeed);
        AddTile(heroRow, "/Assets/Icons/hero_damage.png",  "Hero Dmg", u.HeroDamage);
        AddTile(heroRow, "/Assets/Icons/hero_casting.png", "Casting",  u.HeroCasting);
        body.Children.Add(heroRow);

        // Row 2 — the two abilities, centered.
        var abilityRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        AddTile(abilityRow, "/Assets/Icons/mana_icon.png", "Ability 1", u.HeroSkill1);
        AddTile(abilityRow, "/Assets/Icons/mana_icon.png", "Ability 2", u.HeroSkill2);
        body.Children.Add(abilityRow);

        // Row 3 — the four tower stats.
        var towerRow = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        AddTile(towerRow, "/Assets/Icons/tower_health.png", "Tower HP",   u.TowerHealth);
        AddTile(towerRow, "/Assets/Icons/tower_speed.png",  "Tower Rate", u.TowerSpeed);
        AddTile(towerRow, "/Assets/Icons/tower_damage.png", "Tower Dmg",  u.TowerDamage);
        AddTile(towerRow, "/Assets/Icons/tower_range.png",  "Tower AOE",  u.TowerRange);
        body.Children.Add(towerRow);

        // ── Equipment ──
        int heldHe = RdPtr(hero + OFF_HERO_WEAPON);
        var equipment = ReadPtrArray(hero + OFF_HERO_EQUIPMENTS);

        var equipStack = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        equipStack.Children.Add(SectionLabel($"E Q U I P M E N T   ·   {equipment.Count}"));
        equipStack.Children.Add(new Border { Height = 7 }); // spacer

        foreach (int he in equipment)
        {
            var row = TryBuildEquipRow(he, he == heldHe);
            if (row != null) equipStack.Children.Add(row);
        }
        if (equipment.Count == 0)
            equipStack.Children.Add(new TextBlock
            {
                Text = "Nothing equipped",
                FontSize = 10.5,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("TextMutedBrush")
            });
        mainStack.Children.Add(equipStack);

        // ── Interactions: double-click the card (outside item rows)
        //    opens the hero editor. No hover tint — the card's header band
        //    already uses SurfaceLighter, so tinting the whole card to it
        //    just made the card and its sub-cards blend together. ──
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonDown += (_, e2) =>
        {
            if (e2.ClickCount == 2) OpenEditor(nativeAddr, Base.Genus.Hero, heroName);
        };

        return card;
    }

    // Compact equipment mini-card — the Forge card's little sibling:
    // a type-colored left strip, name + quality/level line, and up to
    // three headline stat chips. Double-click opens the item editor.
    private Border? TryBuildEquipRow(int he, bool held)
    {
        int addr = he + OFF_HE_ITEMNATIVE;
        ItemUser u;
        string name;
        try
        {
            int size = Marshal.SizeOf(typeof(ItemNative));
            var native = Base.Push<ItemNative>(Base.Instance.ReadMemory(addr, size));
            u = Base.ItemToUser(native);
            name = Base.ReadUni<ItemNative>(addr, "EquipmentName") ?? "";
            if (string.IsNullOrWhiteSpace(name))
                name = Base.ReadUni<ItemNative>(addr, "BaseEquipmentName") ?? "";
            name = StripColorTags(name);
            if (string.IsNullOrWhiteSpace(name))
                name = QualityDisplay.Name(u.Quality2) + " " + TypeLabel(u.EquipmentType);
        }
        catch { return null; }

        var content = new StackPanel { Margin = new Thickness(10, 7, 10, 8) };

        // Row 1: quality dot + name (+ held marker) — right: level
        var row1 = new DockPanel { LastChildFill = true };
        var lvl = new TextBlock
        {
            Text = "Lv " + u.Level,
            FontSize = 10,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(lvl, Dock.Right);
        row1.Children.Add(lvl);

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        // Small quality dot — the one bit of meaningful per-item colour.
        nameLine.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(ForgeViewerView.GetAccentColor(u.Quality2)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            ToolTip = QualityDisplay.Name(u.Quality2)
        });
        var nameText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        nameText.Inlines.Add(new System.Windows.Documents.Run(name));
        if (held)
            nameText.Inlines.Add(new System.Windows.Documents.Run("   IN HAND")
            {
                Foreground = (Brush)FindResource("AccentBrush"),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold
            });
        nameLine.Children.Add(nameText);
        row1.Children.Add(nameLine);
        content.Children.Add(row1);

        // Row 2: quality · type, then up to 3 headline stat chips
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 3, 0, 0) };
        row2.Children.Add(new TextBlock
        {
            Text = $"{QualityDisplay.Name(u.Quality2)} · {TypeLabel(u.EquipmentType)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        foreach (var (icon, label, value) in HeadlineStats(u))
            row2.Children.Add(MakeStatChip(icon, label, value));
        content.Children.Add(row2);

        var card = new Border
        {
            Background = (Brush)FindResource("SurfaceLighterBrush"),
            BorderBrush = (Brush)FindResource("SurfaceLighterBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Child = content
        };
        card.MouseEnter += (_, _) => card.BorderBrush = (Brush)FindResource("AccentBrush");
        card.MouseLeave += (_, _) => card.BorderBrush = (Brush)FindResource("SurfaceLighterBrush");
        card.MouseLeftButtonDown += (_, e2) =>
        {
            if (e2.ClickCount == 2)
            {
                OpenEditor(addr, Base.Genus.Item, name);
                e2.Handled = true; // don't bubble into the hero card's handler
            }
        };
        return card;
    }

    // The "couple main stats" for an equipment mini-card, in priority
    // order: damage items lead with Attack/Ranged/Elemental, everything
    // else with resists; remaining slots go to the biggest hero/tower
    // stat. At most 3 chips, zero values skipped.
    private static List<(string icon, string label, int value)> HeadlineStats(ItemUser u)
    {
        var picks = new List<(string icon, string label, int value)>();
        bool isDamageItem = u.EquipmentType == EquipmentType.Weapon
                         || u.EquipmentType == EquipmentType.Familiar;

        var primary = isDamageItem
            ? new (string, string, int)[]
            {
                ("/Assets/Icons/weapon_damage.png",  "Attack",    u.Damage),
                ("/Assets/Icons/weapon_ranged.png",  "Ranged",    u.RangedDamage),
                ("/Assets/Icons/resist_generic.png", "Elemental", u.ElementalDamage?.Value ?? 0),
            }
            : new (string, string, int)[]
            {
                ("/Assets/Icons/resist_generic.png",   "Generic",   u.Generic?.Value   ?? 0),
                ("/Assets/Icons/resist_poison.png",    "Poison",    u.Poison?.Value    ?? 0),
                ("/Assets/Icons/resist_fire.png",      "Fire",      u.Fire?.Value      ?? 0),
                ("/Assets/Icons/resist_lightning.png", "Lightning", u.Lightning?.Value ?? 0),
            };
        foreach (var p in primary)
        {
            if (p.Item3 != 0 && picks.Count < 3) picks.Add(p);
        }

        if (picks.Count < 3)
        {
            var secondary = new (string, string, int)[]
            {
                ("/Assets/Icons/hero_damage.png",  "Hero Dmg",  u.HeroDamage),
                ("/Assets/Icons/tower_damage.png", "Tower Dmg", u.TowerDamage),
                ("/Assets/Icons/hero_health.png",  "Hero HP",   u.HeroHealth),
                ("/Assets/Icons/tower_health.png", "Tower HP",  u.TowerHealth),
            };
            // biggest first
            Array.Sort(secondary, (a, b) => Math.Abs(b.Item3).CompareTo(Math.Abs(a.Item3)));
            foreach (var s in secondary)
                if (s.Item3 != 0 && picks.Count < 3) picks.Add(s);
        }
        return picks;
    }

    // Frozen-bitmap cache: a full roster render builds hundreds of icon
    // elements from the same ~12 PNGs; frozen BitmapImages are shareable
    // across elements, so decode/init each path once per session. A failed
    // load caches null so a bad path isn't retried per tile.
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage?> _iconCache = new();

    private static System.Windows.Media.Imaging.BitmapImage? GetIcon(string packPath)
    {
        if (_iconCache.TryGetValue(packPath, out var cached)) return cached;
        System.Windows.Media.Imaging.BitmapImage? bmp = null;
        try
        {
            var b = new System.Windows.Media.Imaging.BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri("pack://application:,,," + packPath, UriKind.Absolute);
            b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            bmp = b;
        }
        catch { }
        _iconCache[packPath] = bmp;
        return bmp;
    }

    // Tiny inline icon+value chip (no border — the mini-card is the box).
    private FrameworkElement MakeStatChip(string icon, string label, int value)
    {
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            ToolTip = label + ": " + value.ToString("N0")
        };
        var bmp = GetIcon(icon);
        if (bmp != null)
        {
            chip.Children.Add(new Image
            {
                Source = bmp,
                Width = 11, Height = 11,
                Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85
            });
        }
        chip.Children.Add(new TextBlock
        {
            Text = ForgeViewerView.FormatStatValue(value),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return chip;
    }

    // Compact inset stat tile (same look as the Forge cards). Zero values
    // are shown here (unlike forge items, a hero's 0 stat is meaningful).
    private void AddTile(Panel parent, string icon, string label, int value)
    {
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        var tileBmp = GetIcon(icon);
        if (tileBmp != null)
        {
            valRow.Children.Add(new Image
            {
                Source = tileBmp,
                Width = 13, Height = 13,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85
            });
        }
        valRow.Children.Add(new TextBlock
        {
            Text = value.ToString("N0"),
            FontSize = 11,
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

        parent.Children.Add(new Border
        {
            Background = (Brush)FindResource("SurfaceLighterBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 0, 5, 5),
            ToolTip = label + ": " + value.ToString("N0"),
            Child = stack
        });
    }

    // Spaced-caps muted section label (matches the rest of the app).
    private TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeights.Bold,
        Foreground = (Brush)FindResource("TextMutedBrush")
    };

    private void OpenEditor(int address, Base.Genus genus, string name)
    {
        try
        {
            if (Application.Current.MainWindow is MainWindow main)
                main.ShowEditor(address, genus, name);
        }
        catch (Exception ex)
        {
            Base.RaiseMessage("Failed to open editor: " + ex.Message, "Hero Viewer");
        }
    }

    // ── Memory helpers (shared GameChain; see Models/GameChain.cs) ───

    // Last pawn the chain resolution saw — feeds the staged failure message.
    private int _lastResolvedPawn;

    private int ResolveHeroManager()
    {
        if (Window.GetWindow(this) is not MainWindow main) return 0;
        _lastResolvedPawn = main.ResolvePlayerPawnAddress();
        return GameChain.ResolveHeroManager(_lastResolvedPawn);
    }

    private static List<int> ReadPtrArray(int tarrayAddr) => GameChain.ReadPtrArray(tarrayAddr);
    private static string ReadFString(int addr) => GameChain.ReadFString(addr);

    private string SafeReadHeroName(int nativeAddr)
    {
        try { return Base.ReadUni<HeroNative>(nativeAddr, "HeroName") ?? ""; }
        catch { return ""; }
    }

    // Shared display helpers — single homes in their owning views (same
    // pattern as ForgeViewerView.FormatStatValue/GetAccentColor).
    private static string StripColorTags(string s) => ItemDupeView.StripColorTags(s);
    private static string TypeLabel(EquipmentType t) => ForgeViewerView.TypeLabel(t);

    private static bool IsGamePtr(int p) => GameChain.IsGamePtr(p);
    private static int RdPtr(int addr) => GameChain.RdPtr(addr);
}
