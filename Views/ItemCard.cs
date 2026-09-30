using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Modinator.Views;

internal enum ItemSort
{
    Quality, MaxLevelDesc, MaxLevelAsc, LevelDesc,
    HeroDamageDesc, TowerDamageDesc, WeaponDamageDesc,
    NameAsc, BestStat, Newest, Recommended
}

// The item card and the list plumbing around it (type / sort combos, sort
// order, search text), shared by the Forge Viewer, the Templates tab and the
// Item Dupe pickers so an item looks and sorts the same everywhere.
//
// Laid out like DD1's own item panel (see DECISIONS.md, 2026-09-04):
// level and type top-left, forged-by top-right, name centered, the
// quality word leading straight into the description, then icon rows
// split by rule lines — primary stats as round icons, hero and tower
// bonuses as square tiles, weapon extras round again. Numbers sit under
// their icon. No per-tile boxes: the earlier bordered-pill grid gave
// every stat the same weight and read as a generic dashboard.
//
// Rows pack their live stats left (a zero stat is simply omitted) — the
// icon families already say which group a tile belongs to.
internal static class ItemCard
{
    internal sealed class TypeEntry
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

    internal sealed class SortEntry
    {
        public ItemSort Mode;
        public string Label;
        public SortEntry(ItemSort mode, string label) { Mode = mode; Label = label; }
        public override string ToString() => Label;
    }

    private const string SelectionMarkerTag = "SelectionMarker";

    private static readonly Dictionary<string, ImageBrush?> StatIconBrushes = new();
    private static readonly SolidColorBrush SelectedCardBrush = Frozen(Color.FromArgb(30, 88, 101, 242));

    private static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(ItemCard), new PropertyMetadata(false));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    // ── Combos ───────────────────────────────────────────────────

    public static void FillTypeCombo(ComboBox combo)
    {
        combo.Items.Add(new TypeEntry(EquipmentType.All, "All types", true));
        combo.Items.Add(new TypeEntry(EquipmentType.Weapon, "Weapon", false));
        combo.Items.Add(new TypeEntry("All Armor / Accessories",
            EquipmentType.ArmorHelmet, EquipmentType.ArmorTorso,
            EquipmentType.ArmorBoots, EquipmentType.ArmorGloves,
            EquipmentType.Hat, EquipmentType.ArmGuard,
            EquipmentType.Shield, EquipmentType.Mask));
        combo.Items.Add(new TypeEntry(EquipmentType.ArmorHelmet, "Helmet", false));
        combo.Items.Add(new TypeEntry(EquipmentType.ArmorTorso, "Torso", false));
        combo.Items.Add(new TypeEntry(EquipmentType.ArmorBoots, "Boots", false));
        combo.Items.Add(new TypeEntry(EquipmentType.ArmorGloves, "Gloves", false));
        combo.Items.Add(new TypeEntry(EquipmentType.Familiar, "Familiar", false));
        combo.Items.Add(new TypeEntry(EquipmentType.Hat, "Hat", false));
        combo.Items.Add(new TypeEntry(EquipmentType.ArmGuard, "ArmGuard", false));
        combo.Items.Add(new TypeEntry(EquipmentType.Shield, "Shield", false));
        combo.Items.Add(new TypeEntry(EquipmentType.Mask, "Mask", false));
        combo.SelectedIndex = 0;
    }

    public static void FillSortCombo(ComboBox combo)
    {
        combo.Items.Add(new SortEntry(ItemSort.Quality, "Quality (best first)"));
        combo.Items.Add(new SortEntry(ItemSort.MaxLevelDesc, "Max Level (high to low)"));
        combo.Items.Add(new SortEntry(ItemSort.MaxLevelAsc, "Max Level (low to high)"));
        combo.Items.Add(new SortEntry(ItemSort.LevelDesc, "Level (high to low)"));
        combo.Items.Add(new SortEntry(ItemSort.HeroDamageDesc, "Hero Damage (high to low)"));
        combo.Items.Add(new SortEntry(ItemSort.TowerDamageDesc, "Tower Damage (high to low)"));
        combo.Items.Add(new SortEntry(ItemSort.WeaponDamageDesc, "Weapon Damage (high to low)"));
        combo.Items.Add(new SortEntry(ItemSort.BestStat, "Best stat total"));
        combo.Items.Add(new SortEntry(ItemSort.NameAsc, "Name (A-Z)"));
        combo.SelectedIndex = 0;
    }

    // ── Sort / search ────────────────────────────────────────────

    public static int StatTotal(ItemUser u)
    {
        return u.HeroHealth + u.HeroSpeed + u.HeroDamage + u.HeroCasting + u.HeroSkill1 + u.HeroSkill2
            + u.TowerHealth + u.TowerSpeed + u.TowerDamage + u.TowerRange;
    }

    public static void Sort<T>(List<T> list, ItemSort mode) where T : ItemCardData
    {
        Comparison<T> primary = mode switch
        {
            ItemSort.MaxLevelDesc     => (a, b) => b.User.MaxLevel.CompareTo(a.User.MaxLevel),
            ItemSort.MaxLevelAsc      => (a, b) => a.User.MaxLevel.CompareTo(b.User.MaxLevel),
            ItemSort.LevelDesc        => (a, b) => b.User.Level.CompareTo(a.User.Level),
            ItemSort.HeroDamageDesc   => (a, b) => b.User.HeroDamage.CompareTo(a.User.HeroDamage),
            ItemSort.TowerDamageDesc  => (a, b) => b.User.TowerDamage.CompareTo(a.User.TowerDamage),
            ItemSort.WeaponDamageDesc => (a, b) => b.User.Damage.CompareTo(a.User.Damage),
            ItemSort.BestStat         => (a, b) => StatTotal(b.User).CompareTo(StatTotal(a.User)),
            ItemSort.NameAsc          => (a, b) => string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase),
            ItemSort.Newest           => (a, b) => (b.Saved ?? DateTime.MinValue).CompareTo(a.Saved ?? DateTime.MinValue),
            ItemSort.Recommended      => (a, b) =>
            {
                int c = b.Match.CompareTo(a.Match);
                return c != 0 ? c : QualityDisplay.Rank(b.User.Quality2).CompareTo(QualityDisplay.Rank(a.User.Quality2));
            },
            _                         => (a, b) => QualityDisplay.Rank(b.User.Quality2).CompareTo(QualityDisplay.Rank(a.User.Quality2)),
        };
        // List.Sort is unstable and most keys tie heavily (quality has ~20
        // values across ~1,000 items), so without a total order the page
        // contents reshuffled on every REFRESH. Name, then address, then key
        // makes the order deterministic across scans.
        list.Sort((a, b) =>
        {
            int c = primary(a, b);
            if (c != 0) return c;
            c = string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = ((uint)a.Address).CompareTo((uint)b.Address);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Key, b.Key);
        });
    }

    // Everything the search box matches against. Beyond the free text it
    // covers what a user actually types to find gear: quality ("ultimate",
    // "supreme"), the type both raw and as the card shows it ("ArmorBoots"
    // / "Boots"), the base (archetype) name, and the folder name.
    public static string SearchText(ItemCardData d, string? folder)
    {
        var sb = new StringBuilder(192);
        sb.Append(d.Name ?? "").Append(' ');
        sb.Append(d.BaseName ?? "").Append(' ');
        sb.Append(d.Description ?? "").Append(' ');
        sb.Append(d.ForgerName ?? "").Append(' ');
        sb.Append(QualityDisplay.Name(d.User.Quality2)).Append(' ');
        if (d.User.Quality3 != Quality3.None) sb.Append(d.User.Quality3).Append(' ');
        sb.Append(d.User.EquipmentType).Append(' ');
        sb.Append(ForgeViewerView.TypeLabel(d.User.EquipmentType)).Append(' ');
        if (!string.IsNullOrEmpty(folder)) sb.Append(folder).Append(' ');
        sb.Append("Level ").Append(d.User.Level).Append(' ');
        sb.Append("MaxLevel ").Append(d.User.MaxLevel);
        return sb.ToString();
    }

    // A saved template drawn as the item it would produce. The card title is
    // the template's own name; when the captured item is called something
    // else, that goes in the footer.
    public static ItemCardData FromTemplate(ItemTemplate t)
    {
        bool renamed = !string.Equals(t.Name, t.DisplayName, StringComparison.Ordinal);
        var d = new ItemCardData
        {
            Key = t.Id.ToString("N"),
            Name = renamed || string.IsNullOrWhiteSpace(t.EquipmentName) ? t.Name : t.EquipmentName,
            Description = t.Description,
            ForgerName = t.ForgerName,
            User = Base.ItemToUser(t.Values.Preview()),
            Source = "Templates",
            Saved = t.ModifiedUtc ?? t.CreatedUtc,
            Payload = t,
        };
        d.Footer = (renamed ? t.DisplayName + "  ·  " : "") +
                   (t.ModifiedUtc != null ? "edited " : "saved ") + d.Saved.Value.ToLocalTime().ToString("g");
        d.SearchText = SearchText(d, null) + " " + t.Name + " " + t.ItemName + " " + t.DisplayName;
        return d;
    }

    // ── Card ─────────────────────────────────────────────────────

    public static Border Build(ItemCardData d, bool isSelected)
    {
        var u = d.User;
        var qBrush = new SolidColorBrush(ForgeViewerView.GetAccentColor(u.Quality2));
        var tColor = ForgeViewerView.GetTypeColor(u.EquipmentType);
        var textPrimary   = Res("TextPrimaryBrush");
        var textSecondary = Res("TextSecondaryBrush");
        var textMuted     = Res("TextMutedBrush");

        var card = new Border
        {
            Width = 256,
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            BorderBrush = isSelected ? Res("AccentBrush") : Res("BorderBrush"),
            Background = isSelected ? SelectedCardBrush : Res("SurfaceLightBrush"),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Tag = d
        };
        card.SetValue(IsSelectedProperty, isSelected);
        // The selection check is overlaid onto this Grid — keep it.
        // Two rows: the body, then a footer pinned to the bottom. Cards in a
        // row stretch to the tallest one, so footers (and the buttons in
        // them) line up across the row whatever each card's stats are.
        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition());
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = outerGrid;
        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 0) };
        outerGrid.Children.Add(body);
        var footer = new StackPanel { Margin = new Thickness(12, 0, 12, 10) };
        Grid.SetRow(footer, 1);
        outerGrid.Children.Add(footer);

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
                Text = ForgeViewerView.TypeLabel(u.EquipmentType).ToUpperInvariant(),
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

        if (!string.IsNullOrWhiteSpace(d.ForgerName))
        {
            // The game's two-line form: a small FORGED BY label over the name,
            // right-aligned, and the name keeps its own line breaks (a colored
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
            AppendColorRuns(forger, d.ForgerName, textSecondary);
            forged.Children.Add(forger);
            Grid.SetColumn(forged, 1);
            top.Children.Add(forged);
        }
        body.Children.Add(top);

        // ── Name, centered. Custom names can carry <color> runs too. ──
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
        AppendColorRuns(name, string.IsNullOrWhiteSpace(d.Name) ? "(unnamed)" : d.Name, textPrimary);
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

        // The watermark line is lifted out of the description and drawn on
        // its own line below, outside the three-line cap — a long or colored
        // description would otherwise push it out of view, which reads as
        // "the watermark wasn't applied" when it was.
        string descText = ColorMarkup.NormalizeNewlines(d.Description);
        string? markLine = null;
        if (Watermark.IsMarked(descText))
        {
            var lines = descText.Split('\n').ToList();
            int mi = lines.FindIndex(Watermark.IsMarked);
            if (mi >= 0) { markLine = lines[mi]; lines.RemoveAt(mi); descText = string.Join("\n", lines).Trim('\n'); }
        }
        if (!string.IsNullOrWhiteSpace(descText))
        {
            desc.Inlines.Add(new System.Windows.Documents.Run(" "));
            AppendColorRuns(desc, descText, textSecondary);
        }
        body.Children.Add(desc);
        if (markLine != null)
        {
            var mark = new TextBlock
            {
                FontSize = 9.5,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 2, 4, 0)
            };
            AppendColorRuns(mark, markLine, textMuted);
            body.Children.Add(mark);
        }

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

        // ── Where it came from (templates, other players, the floor) ──
        if (!string.IsNullOrWhiteSpace(d.Footer))
        {
            footer.Children.Add(new TextBlock
            {
                Text = d.Footer,
                FontSize = 9.5,
                FontWeight = d.Recommended ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = d.Recommended ? Res("AccentBrush") : textMuted,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = d.Footer,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        // ── Selection checkmark ──
        if (isSelected)
            SetSelectionMarker(card, true);

        // ── Hover ──
        card.MouseEnter += (s, e) => { if (!IsSelected(card)) card.Background = Res("SurfaceLighterBrush"); };
        card.MouseLeave += (s, e) => { if (!IsSelected(card)) card.Background = Res("SurfaceLightBrush"); };

        return card;
    }

    public static bool IsSelected(Border card) => (bool)card.GetValue(IsSelectedProperty);

    // Toggle the "selected" look on a card without rebuilding anything else.
    // Mirrors the initial assignment inside Build().
    public static void SetSelected(Border card, bool isSelected)
    {
        card.SetValue(IsSelectedProperty, isSelected);
        card.BorderThickness = new Thickness(isSelected ? 2 : 1);
        card.BorderBrush = isSelected ? Res("AccentBrush") : Res("BorderBrush");
        card.Background = isSelected ? SelectedCardBrush : Res("SurfaceLightBrush");
        SetSelectionMarker(card, isSelected);
    }

    // A small centered button in the card's footer (pinned to the bottom).
    // The button eats its own mouse events, so clicking it never selects or
    // opens the card.
    public static Button AddFooterButton(Border card, string glyph, string text, string toolTip, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Content = content,
            FontSize = 9.5,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 9, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = toolTip
        };
        button.Click += (s, e) => { e.Handled = true; onClick(); };
        if (card.Child is Grid grid && grid.Children.Count > 1 && grid.Children[1] is StackPanel footer)
            footer.Children.Add(button);
        return button;
    }

    private static void SetSelectionMarker(Border card, bool isSelected)
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
            Background = Res("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Child = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
    }

    // DD1 <color:r,g,b> runs → colored inlines. Uncolored text takes
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
    private static void AddIconRow(Panel body, (string? icon, string label, int value)[] slots,
                                   bool round, bool plus, double iconSize, double fontSize)
    {
        bool any = false;
        foreach (var s in slots) if (s.value != 0) { any = true; break; }
        if (!any) return;

        body.Children.Add(new Border
        {
            Height = 1,
            Background = Res("BorderBrush"),
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

    // Icon over number over a small label, centered. Round for an item's own
    // stats (damage / resists / weapon extras), square for hero and tower
    // bonuses — the same visual grammar the game's panel uses.
    private static FrameworkElement MakeIconTile(string? iconPath, string label, int value,
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
                Foreground = Res("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
        }

        var number = new TextBlock
        {
            Text = (plus && value > 0 ? "+" : "") + ForgeViewerView.FormatStatValue(value),
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = Res("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        col.Children.Add(number);

        col.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 8,
            Foreground = Res("TextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        });
        return col;
    }

    private static ImageBrush? GetStatIconBrush(string? iconPath)
    {
        if (iconPath == null) return null;
        if (StatIconBrushes.TryGetValue(iconPath, out ImageBrush? cached)) return cached;

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
            StatIconBrushes[iconPath] = brush;
            return brush;
        }
        catch
        {
            StatIconBrushes[iconPath] = null;
            return null;
        }
    }
}
