using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Modinator.Views.EditHelpers;

namespace Modinator.Views;

public partial class HeroEditView : UserControl
{
    private int Address;
    private HeroNative NativeData;
    private string? OriginalName;

    // Suppress live-swatch updates while we're populating the boxes in
    // ShowDetails, so we don't trigger 9 partial paints as each Text is set.
    private bool _suppressSwatch;

    public HeroEditView(int address, string name)
    {
        InitializeComponent();
        Address = address;
        StatusText.Text = "Hero - " + Base.Truncate(name);
        Loaded += (s, e) => ShowDetails();
    }

    private void ShowDetails()
    {
        _suppressSwatch = true;
        try
        {
            int length = Marshal.SizeOf(typeof(HeroNative));
            byte[] data = Base.Instance.ReadMemory(Address, length);
            NativeData = Base.Push<HeroNative>(data);
            var u = Base.HeroToUser(NativeData);
            OriginalName = Base.ReadUni<HeroNative>(Address, "HeroName");

            // Stats / name fields: show the current value as a grey hint and
            // leave the box empty so the user can type without clearing first.
            // Empty-on-save means "keep the current value".
            SetHint(TxtHeroHealth, u.HeroHealth.ToString());
            SetHint(TxtHeroSpeed, u.HeroSpeed.ToString());
            SetHint(TxtHeroDamage, u.HeroDamage.ToString());
            SetHint(TxtHeroCasting, u.HeroCasting.ToString());
            SetHint(TxtHeroSkill1, u.HeroSkill1.ToString());
            SetHint(TxtHeroSkill2, u.HeroSkill2.ToString());
            SetHint(TxtTowerHealth, u.TowerHealth.ToString());
            SetHint(TxtTowerSpeed, u.TowerSpeed.ToString());
            SetHint(TxtTowerDamage, u.TowerDamage.ToString());
            SetHint(TxtTowerRange, u.TowerRange.ToString());
            SetHint(TxtHeroName, OriginalName ?? "");
            SetHint(TxtLevel, u.Level.ToString());
            SetHint(TxtExperience, u.Experience.ToString());

            // Color R/G/B boxes stay populated — the live swatch and PICK
            // button read from them on every keystroke.
            TxtColor1R.Text = u.Color1?.R.ToString() ?? "0";
            TxtColor1G.Text = u.Color1?.G.ToString() ?? "0";
            TxtColor1B.Text = u.Color1?.B.ToString() ?? "0";
            TxtColor2R.Text = u.Color2?.R.ToString() ?? "0";
            TxtColor2G.Text = u.Color2?.G.ToString() ?? "0";
            TxtColor2B.Text = u.Color2?.B.ToString() ?? "0";
            TxtColor3R.Text = u.Color3?.R.ToString() ?? "0";
            TxtColor3G.Text = u.Color3?.G.ToString() ?? "0";
            TxtColor3B.Text = u.Color3?.B.ToString() ?? "0";
        }
        catch { Base.RaiseMessage("Failed to read hero data.", "Error"); }
        finally
        {
            _suppressSwatch = false;
            UpdateSwatch(Sw1, TxtColor1R, TxtColor1G, TxtColor1B);
            UpdateSwatch(Sw2, TxtColor2R, TxtColor2G, TxtColor2B);
            UpdateSwatch(Sw3, TxtColor3R, TxtColor3G, TxtColor3B);
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => ShowDetails();

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateBackFromEditor();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var v = new FieldValidator();

        // Start from the CURRENT native (re-read so nothing is stale), then
        // only overwrite fields the user actually typed into. Empty textbox
        // = keep as-is.
        HeroUser current;
        try
        {
            int size = Marshal.SizeOf(typeof(HeroNative));
            byte[] cur = Base.Instance.ReadMemory(Address, size);
            NativeData = Base.Push<HeroNative>(cur);
            OriginalName = Base.ReadUni<HeroNative>(Address, "HeroName");
            current = Base.HeroToUser(NativeData);
        }
        catch
        {
            Base.RaiseMessage("Failed to read hero data before write.", "Error");
            return;
        }

        var u = current;
        u.HeroHealth  = IntOr(v, TxtHeroHealth,  "Hero Health",  current.HeroHealth);
        u.HeroSpeed   = IntOr(v, TxtHeroSpeed,   "Hero Speed",   current.HeroSpeed);
        u.HeroDamage  = IntOr(v, TxtHeroDamage,  "Hero Damage",  current.HeroDamage);
        u.HeroCasting = IntOr(v, TxtHeroCasting, "Hero Casting", current.HeroCasting);
        u.HeroSkill1  = IntOr(v, TxtHeroSkill1,  "Hero Skill 1", current.HeroSkill1);
        u.HeroSkill2  = IntOr(v, TxtHeroSkill2,  "Hero Skill 2", current.HeroSkill2);
        u.TowerHealth = IntOr(v, TxtTowerHealth, "Tower Health", current.TowerHealth);
        u.TowerSpeed  = IntOr(v, TxtTowerSpeed,  "Tower Speed",  current.TowerSpeed);
        u.TowerDamage = IntOr(v, TxtTowerDamage, "Tower Damage", current.TowerDamage);
        u.TowerRange  = IntOr(v, TxtTowerRange,  "Tower Range",  current.TowerRange);
        u.Level       = IntOr(v, TxtLevel,       "Level",        current.Level);
        u.Experience  = IntOr(v, TxtExperience,  "Experience",   current.Experience);
        u.HeroName    = string.IsNullOrEmpty(TxtHeroName.Text) ? (OriginalName ?? "") : TxtHeroName.Text;

        // Colors are always populated (see note in ShowDetails), so parse straight.
        u.Color1 = ParseColor(v, TxtColor1R, TxtColor1G, TxtColor1B, "Color 1");
        u.Color2 = ParseColor(v, TxtColor2R, TxtColor2G, TxtColor2B, "Color 2");
        u.Color3 = ParseColor(v, TxtColor3R, TxtColor3G, TxtColor3B, "Color 3");

        if (!v.IsValid)
        {
            StatusText.Text = "Invalid: " + v.Report();
            v.FocusFirstError();
            return;
        }

        try
        {
            var item = Base.HeroToNative(u);
            item.MaxLevel = NativeData.MaxLevel;
            item.MaxDemoLevel = NativeData.MaxDemoLevel;
            item.R1 = NativeData.R1;
            item.R2 = NativeData.R2;
            if (u.HeroName != OriginalName && !string.IsNullOrEmpty(u.HeroName))
                item.HeroName = Base.WriteUni(Address, "HeroName", u.HeroName);
            else
                item.HeroName = NativeData.HeroName;

            byte[] bytes = Base.Push(item);
            Base.Instance.WriteMemory(Address, bytes);
            StatusText.Text = "Updated!";

            // Reset placeholders to the new values so the form reflects what
            // was just written, and the user can immediately edit again.
            ShowDetails();
        }
        catch { Base.RaiseMessage("Failed to write hero data.", "Error"); }
    }

    private static LinearColor ParseColor(FieldValidator v, TextBox r, TextBox g, TextBox b, string label)
    {
        return new LinearColor
        {
            R = v.Byte(r, $"{label} R"),
            G = v.Byte(g, $"{label} G"),
            B = v.Byte(b, $"{label} B"),
        };
    }

    // ── Color picker buttons ────────────────────────────────────────

    private void BtnPickColor1_Click(object sender, RoutedEventArgs e)
        => PickColor(TxtColor1R, TxtColor1G, TxtColor1B);

    private void BtnPickColor2_Click(object sender, RoutedEventArgs e)
        => PickColor(TxtColor2R, TxtColor2G, TxtColor2B);

    private void BtnPickColor3_Click(object sender, RoutedEventArgs e)
        => PickColor(TxtColor3R, TxtColor3G, TxtColor3B);

    private void PickColor(TextBox rBox, TextBox gBox, TextBox bBox)
    {
        try
        {
            byte r = ReadByte(rBox);
            byte g = ReadByte(gBox);
            byte b = ReadByte(bBox);

            var dlg = new RgbPickerDialog(r, g, b);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                rBox.Text = dlg.ResultR.ToString();
                gBox.Text = dlg.ResultG.ToString();
                bBox.Text = dlg.ResultB.ToString();
            }
        }
        catch (Exception ex)
        {
            Base.RaiseMessage($"Color picker failed: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}", "Picker error");
        }
    }

    // ── Live swatches — update whenever an RGB text box changes ─────
    //
    // These fire during XAML load before sibling fields are wired, so every
    // referenced element must be null-checked. _suppressSwatch also blocks
    // the burst of 9 partial paints while ShowDetails populates values.

    private void OnColor1Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressSwatch) return;
        UpdateSwatch(Sw1, TxtColor1R, TxtColor1G, TxtColor1B);
    }

    private void OnColor2Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressSwatch) return;
        UpdateSwatch(Sw2, TxtColor2R, TxtColor2G, TxtColor2B);
    }

    private void OnColor3Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressSwatch) return;
        UpdateSwatch(Sw3, TxtColor3R, TxtColor3G, TxtColor3B);
    }

    private static void UpdateSwatch(Border? sw, TextBox? r, TextBox? g, TextBox? b)
    {
        if (sw == null || r == null || g == null || b == null) return;
        try
        {
            sw.Background = new SolidColorBrush(Color.FromRgb(ReadByte(r), ReadByte(g), ReadByte(b)));
        }
        catch { /* layout-time quirks shouldn't take the app down */ }
    }

    private static byte ReadByte(TextBox tb)
    {
        if (tb == null || !int.TryParse(tb.Text, out int v)) return 0;
        return (byte)Math.Clamp(v, 0, 255);
    }

    // ── MAX stats ───────────────────────────────────────────────────
    //
    // Gear: open the hero config dialog.
    // MAX: load current in-memory values, then:
    //   - Hero/Tower stats + HeroName: always applied (if config has them)
    //   - Level / Experience: only if the hero's current value is non-zero
    //   - Config values left null: skipped entirely.
    // Fields get populated; user still hits UPDATE to commit.

    private void BtnMaxConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MaxHeroConfigDialog(MaxHeroConfig.Load());
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = MaxHeroConfig.Load();

            int size = Marshal.SizeOf(typeof(HeroNative));
            byte[] data = Base.Instance.ReadMemory(Address, size);
            NativeData = Base.Push<HeroNative>(data);
            var cur = Base.HeroToUser(NativeData);

            // Always-apply
            Set(TxtHeroHealth,  cfg.HeroHealth);
            Set(TxtHeroSpeed,   cfg.HeroSpeed);
            Set(TxtHeroDamage,  cfg.HeroDamage);
            Set(TxtHeroCasting, cfg.HeroCasting);
            Set(TxtHeroSkill1,  cfg.HeroSkill1);
            Set(TxtHeroSkill2,  cfg.HeroSkill2);
            Set(TxtTowerHealth, cfg.TowerHealth);
            Set(TxtTowerSpeed,  cfg.TowerSpeed);
            Set(TxtTowerDamage, cfg.TowerDamage);
            Set(TxtTowerRange,  cfg.TowerRange);

            // Only-if-nonzero
            SetIfNonzero(TxtLevel,      cfg.Level,      cur.Level);
            SetIfNonzero(TxtExperience, cfg.Experience, cur.Experience);

            if (!string.IsNullOrEmpty(cfg.HeroName)) TxtHeroName.Text = cfg.HeroName;

            StatusText.Text = "MAX applied — click UPDATE to save";
        }
        catch (Exception ex)
        {
            StatusText.Text = "MAX failed: " + ex.Message;
        }
    }

    private static void Set(TextBox tb, int? v)
    {
        if (v.HasValue) tb.Text = v.Value.ToString();
    }

    private static void SetIfNonzero(TextBox tb, int? cfgMax, int current)
    {
        if (cfgMax.HasValue && current != 0) tb.Text = cfgMax.Value.ToString();
    }
}
