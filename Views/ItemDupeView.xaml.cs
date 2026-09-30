using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Modinator.Views;

// Dedicated item-dupe tab. Two sides: SOURCE (copied from) and TARGET
// (overwritten). Both are chosen in ItemPickerDialog, which looks like the
// Forge Viewer and reads the game itself: the target from your own items,
// the source from your items, the template library, or other players' gear
// and floor drops.
//
// Transfer policy = the engine's own FEquipmentNetInfo value-set only
// (DD1_INTERNALS.md §4): start from the sacrificial (preserve
// EVERYTHING — identity, the 6 NativeArray buffers, Flags/Mystery/pad),
// copy ONLY value/archetype fields from source, write the 3 user strings
// into target-owned buffers (in-place, or the existing allocation fallback).
// Never raw-copy the
// whole ItemNative; never copy a per-instance NativeArray pointer. This
// is the lowest-crash external dupe and supersedes the old removed
// ItemEditView clone button.
public partial class ItemDupeView : UserControl
{
    private int? _sacrificialAddr;
    private int? _sourceAddr;
    // Identity of each picked item CAPTURED AT PICK TIME, from live memory.
    // Deliberately not looked up in any scan list at write time: a list is
    // replaced by the next rescan, and "address not in the list" must never
    // count as a PASS — that is the exact case the gate exists for (the item
    // was sold, so it vanished from the new scan), and it would sail straight
    // through into a write against freed, possibly reused memory.
    private ItemIdentity? _sacrificialId;
    private ItemIdentity? _sourceId;
    private DupeSession _sourceSession, _targetSession;
    private ItemTemplate? _templateSource;
    private MultiplayerItem? _remoteSource;
    private CancellationTokenSource? _operation;
    private bool _busy;

    // Read the item now and remember what it is. Returns false if it can't
    // be read — a selection we can't identify is one we must never write to.
    private static bool TryCaptureIdentity(int addr, out ItemIdentity id)
    {
        id = default;
        try
        {
            var native = DupeMemory.ReadItem(addr);
            id = ItemIdentity.Of(native);
            return true;
        }
        catch { return false; }
    }

    public ItemDupeView()
    {
        InitializeComponent();
        Unloaded += (_, _) => _operation?.Cancel();
    }

    private void BtnPickSacrificial_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Base.OpenProcess() || Window.GetWindow(this) is not MainWindow main) return;
        var picker = new ItemPickerDialog(main, ItemPickerDialog.Mode.Target, excludeAddress: _sourceAddr ?? 0,
            source: CurrentSourceShape());
        if (picker.ShowDialog() == true && picker.PickedAddress is int a)
        {
            if (!TryCaptureIdentity(a, out var id))
            {
                Base.RaiseMessage("Couldn't read that item — open the picker and choose it again.", "Item Dupe");
                return;
            }
            _sacrificialAddr = a;
            _sacrificialId = id;
            _targetSession = DupeSession.Current;
            ShowItem(true, a);
            RefreshDupeEnabled();
        }
    }

    private void BtnPickSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Base.OpenProcess() || Window.GetWindow(this) is not MainWindow main) return;
        var picker = new ItemPickerDialog(main, ItemPickerDialog.Mode.Source, excludeAddress: _sacrificialAddr ?? 0);
        if (picker.ShowDialog() != true) return;
        if (picker.PickedTemplate is ItemTemplate template) { UseTemplate(template); return; }
        if (picker.PickedRemote is MultiplayerItem remote) { UseRemote(remote); return; }
        if (picker.PickedAddress is int a)
        {
            if (!TryCaptureIdentity(a, out var id))
            {
                Base.RaiseMessage("Couldn't read that item — open the picker and choose it again.", "Item Dupe");
                return;
            }
            _sourceAddr = a;
            _sourceId = id;
            _sourceSession = DupeSession.Current;
            _templateSource = null;
            _remoteSource = null;
            ShowItem(false, a);
            RefreshDupeEnabled();
        }
    }

    private void RefreshDupeEnabled()
    {
        bool ready = _sacrificialAddr is int s && (_templateSource != null || (_sourceAddr is int src && s != src));
        string? mismatch = ready && _sacrificialAddr is int sac && CurrentSourceClassPath() is string scp ? ClassMismatch(sac, scp) : null;
        BtnDupe.IsEnabled = !_busy && ready;
        // "Add to templates" lives in each card and only shows for a live
        // item (a template source is already in the library).
        BtnSaveTemplate.Visibility = _sourceAddr != null ? Visibility.Visible : Visibility.Collapsed;
        BtnSaveTargetTemplate.Visibility = _sacrificialAddr != null ? Visibility.Visible : Visibility.Collapsed;
        BtnSaveTemplate.IsEnabled = BtnSaveTargetTemplate.IsEnabled = !_busy;
        BtnPickSource.IsEnabled = BtnPickSacrificial.IsEnabled = BtnReset.IsEnabled = !_busy;
        BtnCancelOperation.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        if (_busy) return;
        TxtStatus.Text = (_sourceAddr == null && _templateSource == null) || _sacrificialAddr == null
            ? "Pick a source and a target item."
            : (_sacrificialAddr == _sourceAddr
                ? "Source and target must be different items."
                : "Ready. OVERWRITE TARGET will replace the target item." + (mismatch != null ? " " + mismatch : ""));
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _sacrificialAddr = null;
        _sourceAddr = null;
        _sacrificialId = null;
        _sourceId = null;
        _templateSource = null;
        _remoteSource = null;
        ClearCard(true);
        ClearCard(false);
        RefreshDupeEnabled();
    }

    private void ClearCard(bool sacrificial)
    {
        TextBlock metaT = sacrificial ? TxtSacMeta : TxtSrcMeta;
        TextBlock addrT = sacrificial ? TxtSacAddr : TxtSrcAddr;
        metaT.Text = "";
        addrT.Text = "";
        (sacrificial ? SacStats : SrcStats).Children.Clear();
        // Back to the centered empty state ("No X selected" lives there now).
        (sacrificial ? SacBody : SrcBody).Visibility = Visibility.Collapsed;
        (sacrificial ? SacEmpty : SrcEmpty).Visibility = Visibility.Visible;
    }

    // Fills one card's three text blocks and swaps the empty state out
    // for the populated body.
    private void ShowItem(bool sacrificial, int addr)
    {
        TextBlock nameT = sacrificial ? TxtSacName : TxtSrcName;
        TextBlock metaT = sacrificial ? TxtSacMeta : TxtSrcMeta;
        TextBlock addrT = sacrificial ? TxtSacAddr : TxtSrcAddr;
        try
        {
            int size = Marshal.SizeOf(typeof(ItemNative));
            var native = Base.Push<ItemNative>(Base.Instance.ReadMemory(addr, size));
            var u = Base.ItemToUser(native);
            string name = ResolveDisplayName(addr);
            string forger = StripColorTags(Base.ReadUni<ItemNative>(addr, "ForgerName") ?? "");

            nameT.Text = name;
            nameT.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
            metaT.Text = $"{u.EquipmentType}  /  {QualityDisplay.Name(u.Quality2)}  /  Lvl {u.Level}" +
                         (string.IsNullOrWhiteSpace(forger) ? "" : $"  /  forged by {forger}");
            addrT.Text = Base.AddressToString(addr);
            BuildStats(sacrificial ? SacStats : SrcStats, u);
        }
        catch (Exception ex)
        {
            nameT.Text = "(could not read item)";
            nameT.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            metaT.Text = ex.Message;
            addrT.Text = "";
            (sacrificial ? SacStats : SrcStats).Children.Clear();
        }
        (sacrificial ? SacEmpty : SrcEmpty).Visibility = Visibility.Collapsed;
        (sacrificial ? SacBody : SrcBody).Visibility = Visibility.Visible;
    }

    // Renders the item's stat block — same icon set / layout the Forge
    // Viewer card uses. Zero-value rows are dropped so a card only shows
    // what the item actually has.
    private void BuildStats(Panel host, ItemUser u)
    {
        host.Children.Clear();

        AddStatGrid(host, new (string?, string, int)[]
        {
            ("/Assets/Icons/hero_health.png",  "Hero HP",   u.HeroHealth),
            ("/Assets/Icons/hero_damage.png",  "Hero Dmg",  u.HeroDamage),
            ("/Assets/Icons/hero_speed.png",   "Hero Spd",  u.HeroSpeed),
            ("/Assets/Icons/hero_casting.png", "Casting",   u.HeroCasting),
            ("/Assets/Icons/tower_health.png", "Tower HP",  u.TowerHealth),
            ("/Assets/Icons/tower_damage.png", "Tower Dmg", u.TowerDamage),
            ("/Assets/Icons/tower_range.png",  "Tower Rng", u.TowerRange),
            ("/Assets/Icons/tower_speed.png",  "Tower Spd", u.TowerSpeed),
        });

        var wpn = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddChip(wpn, "/Assets/Icons/weapon_damage.png",      "Damage",      u.Damage);
        AddChip(wpn, "/Assets/Icons/weapon_ranged.png",      "Ranged",      u.RangedDamage);
        AddChip(wpn, "/Assets/Icons/weapon_knockback.png",   "Knockback",   u.Knockback);
        AddChip(wpn, "/Assets/Icons/weapon_projectiles.png", "Projectiles", u.NumberOfProjectiles);
        AddChip(wpn, "/Assets/Icons/weapon_projspeed.png",   "Proj Spd",    u.SpeedOfProjectiles);
        AddChip(wpn, "/Assets/Icons/weapon_reload.png",      "Reload",      u.ReloadSpeed);
        AddChip(wpn, "/Assets/Icons/weapon_chargespeed.png", "Charge",      u.ChargeSpeed);
        AddChip(wpn, "/Assets/Icons/weapon_shotspersec.png", "Shots/s",     u.ShotsPerSecond);
        AddChip(wpn, "/Assets/Icons/weapon_clipammo.png",    "Clip",        u.ClipAmmo);
        AddChip(wpn, "/Assets/Icons/weapon_blocking.png",    "Block",       u.Blocking);
        if (wpn.Children.Count > 0) { AddDivider(host); host.Children.Add(wpn); }

        var res = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddChip(res, "/Assets/Icons/resist_generic.png",   "Generic",   u.Generic?.Value   ?? 0);
        AddChip(res, "/Assets/Icons/resist_poison.png",    "Poison",    u.Poison?.Value    ?? 0);
        AddChip(res, "/Assets/Icons/resist_fire.png",      "Fire",      u.Fire?.Value      ?? 0);
        AddChip(res, "/Assets/Icons/resist_lightning.png", "Lightning", u.Lightning?.Value ?? 0);
        if (res.Children.Count > 0) { AddDivider(host); host.Children.Add(res); }
    }

    private void AddStatGrid(Panel host, (string? icon, string label, int value)[] stats)
    {
        var grid = new Grid();
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        int shown = 0;
        foreach (var (icon, label, value) in stats)
        {
            if (value == 0) continue;
            int row = shown / 4, col = shown % 4;
            if (col == 0) grid.RowDefinitions.Add(new RowDefinition());
            AddIconStat(grid, row, col, icon, label, value);
            shown++;
        }
        if (shown > 0) host.Children.Add(grid);
    }

    private void AddDivider(Panel host) => host.Children.Add(new Border
    {
        Height = 1,
        Background = (Brush)FindResource("BorderBrush"),
        Opacity = 0.4,
        Margin = new Thickness(0, 8, 0, 0)
    });

    private void AddIconStat(Grid grid, int row, int col, string? iconPath, string label, int value)
    {
        var panel = new StackPanel();
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (iconPath != null)
            try
            {
                valRow.Children.Add(new Image
                {
                    Source = LoadIcon(iconPath),
                    Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
                });
            }
            catch { }
        valRow.Children.Add(new TextBlock
        {
            Text = value.ToString("N0"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(valRow);
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9.5,
            Foreground = (Brush)FindResource("TextMutedBrush")
        });

        var tile = new Border
        {
            Background = (Brush)FindResource("SurfaceLightBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 6, 6),
            Child = panel
        };
        Grid.SetRow(tile, row);
        Grid.SetColumn(tile, col);
        grid.Children.Add(tile);
    }

    private void AddChip(WrapPanel parent, string iconPath, string label, int value)
    {
        if (value == 0) return;
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        try
        {
            chip.Children.Add(new Image
            {
                Source = LoadIcon(iconPath),
                Width = 13, Height = 13, Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
            });
        }
        catch { }
        chip.Children.Add(new TextBlock
        {
            Text = $"{label} {value:N0}",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        parent.Children.Add(new Border
        {
            Background = (Brush)FindResource("SurfaceLightBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = chip
        });
    }

    private static BitmapImage LoadIcon(string iconPath)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri("pack://application:,,," + iconPath, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // DD1 rich names/forgers embed <color:r,g,b>…</color> runs; strip
    // them for display (the bytes in memory are untouched — the dupe
    // copies the raw string, not this cleaned version).
    // internal: HeroViewerView reuses it for hero/equipment names.
    internal static string StripColorTags(string s) => Watermark.StripColorTags(s);

    // Same resolver as the Forge cards and the pickers (custom name →
    // rolled base name → archetype), so an item is called the same thing
    // here as in the list it was picked from.
    private static string ResolveDisplayName(int addr)
    {
        try { return DupeMemory.ItemName(addr); }
        catch { return "(unreadable item)"; }
    }

    // Strings are written ONLY into the target's own buffers (2026-09-27).
    // Every item string buffer is exact-fit (live: length == capacity on all
    // of them), so the old "allocate a fresh buffer inside DD1" fallback ran
    // on nearly every copy — a VirtualAllocEx page the game's allocator never
    // issued, which the game later frees when the item is moved, equipped,
    // renamed, sold or unloaded. Now: fits → in place; doesn't fit → shorten
    // (prose) or blank to a single space (names, which the game regenerates
    // from the copied name index + archetype tables). Each compromise is
    // reported through `notes`. A blank FString crashes DD1: never write "".
    internal static NativeArray WriteFit(NativeArray existing, string data, string field, List<string> notes, bool allowTruncate)
    {
        if (string.IsNullOrEmpty(data)) data = " ";
        int cap = existing.MaximumLength;
        if (!GameChain.IsGamePtr(existing.Address) || cap < 2 || cap > 1048576)
        {
            notes.Add($"{field} kept (target has no text buffer)");
            return existing;
        }
        if (data.Length + 1 <= cap) return Base.WriteUniInPlace(existing, data);
        if (allowTruncate && cap - 1 >= 8)
        {
            notes.Add($"{field} shortened to {cap - 1} characters to fit");
            return Base.WriteUniInPlace(existing, data[..(cap - 1)]);
        }
        notes.Add($"{field} left blank (the target's buffer is too small for it)");
        return Base.WriteUniInPlace(existing, " ");
    }

    // A target of the source archetype's class is the safe case: a copy only
    // changes what the object CLAIMS to be; the engine never changes an
    // object's class, and Familiar_* / EventHostCrown / Rune subclasses have
    // fields beyond ItemNative that a plain HeroEquipment target does not.
    // A different class is allowed (2026-09-30) but warned about: the picker
    // lists same-class targets first as Recommended, and the overwrite
    // confirmation says the copy might be unstable. null = same class (or
    // unreadable).
    internal static string? ClassMismatch(int sacrificialAddr, string sourceClassPath)
    {
        try
        {
            string target = DupeMemory.ClassPath(unchecked(sacrificialAddr - 0x38));
            if (target == "?" || sourceClassPath == "?" || target == sourceClassPath) return null;
            return $"The target is a different kind of item than the source ({DupeMemory.ClassLeaf(target)} vs " +
                   $"{DupeMemory.ClassLeaf(sourceClassPath)}), so the copy might be unstable.";
        }
        catch { return null; }
    }

    // Class, base item, equipment type and weapon type of the current
    // source, for ranking targets in the picker. null = no source yet.
    private DupeSourceShape? CurrentSourceShape()
    {
        try
        {
            if (_templateSource is ItemTemplate t)
            {
                string reference = t.References[0];
                int space = reference.IndexOf(' ');
                int type = (int)t.Values.Preview().EquipmentType;
                return new DupeSourceShape(DupeMemory.TemplateClassPath(reference), space > 0 ? reference[(space + 1)..] : "", type, null);
            }
            if (_sourceAddr is int src)
            {
                int archetype = GameChain.RdInt(src);
                int obj = unchecked(src - 0x38);
                return new DupeSourceShape(DupeMemory.ClassPath(archetype), GameReflection.ObjectPath(archetype),
                    Base.Instance.ReadMemory(obj + 0xDA, 1)[0],
                    Base.Instance.ReadMemory(obj + DupeMemory.WeaponTypeObjectOffset, 1)[0]);
            }
        }
        catch { }
        return null;
    }

    private string? CurrentSourceClassPath()
    {
        if (_templateSource is ItemTemplate t) return DupeMemory.TemplateClassPath(t.References[0]);
        if (_sourceAddr is int src) { try { return DupeMemory.ArchetypeClassPath(src); } catch { return null; } }
        return null;
    }
}
