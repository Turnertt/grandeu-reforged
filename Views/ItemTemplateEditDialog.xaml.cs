using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Modinator.Behaviors;

namespace Modinator.Views;

// An offline editor: no attach, native conversion, or game writes. All fields
// are explicit; engine references and identity are never editable here.
// Colors and text use the item editor's own controls: a swatch, the color
// picker and R/G/B boxes for the two color overrides, and the color-text
// editor button beside the name, description and forger name.
public partial class ItemTemplateEditDialog : Window
{
    private readonly ItemTemplate _original;
    private readonly List<Action<ItemTemplate>> _readFields = new();
    private readonly Dictionary<string, StackPanel> _sections = new();
    private readonly Dictionary<string, RadioButton> _tabs = new();
    internal ItemTemplate? Saved { get; private set; }

    internal ItemTemplateEditDialog(ItemTemplate template, bool isNew = false)
    {
        _original = template.Copy();
        InitializeComponent();
        TemplateName.Text = template.Name;
        BaseItem.Text = "Captured item: " + template.ItemName;
        if (isNew)
        {
            Heading.Text = "Save item template";
            BtnSave.Content = "SAVE TEMPLATE";
            BtnSaveNew.Visibility = Visibility.Collapsed;
        }
        BuildFields();
        foreach (string name in _sections.Keys)
        {
            var tab = new RadioButton
            {
                Style = (Style)FindResource("TabButton"),
                GroupName = "TemplateSections",
                Content = name,
                Tag = TemplateVisuals.SectionGlyph(name)
            };
            string section = name;
            tab.Checked += (_, _) => ShowSection(section);
            _tabs.Add(name, tab);
            Tabs.Children.Add(tab);
        }
        _tabs.Values.First().IsChecked = true;
    }

    private void ShowSection(string name)
    {
        foreach (var pair in _sections)
            pair.Value.Visibility = pair.Key == name ? Visibility.Visible : Visibility.Collapsed;
        EditorScroll.ScrollToTop();
    }

    private Panel Section(string name, string help, bool stacked = false)
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        var caption = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(caption);
        Panel grid = stacked ? new StackPanel() : new UniformGrid { Columns = 2 };
        var surface = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 14, 2, 2), Child = grid };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        panel.Children.Add(surface);
        _sections.Add(name, panel);
        Fields.Children.Add(panel);
        return grid;
    }

    // `trailing` sits to the right of the box (the color-editor button).
    private TextBox Field(Panel parent, string label, string value, bool multiline = false, FrameworkElement? trailing = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var box = new TextBox { Text = value, MinHeight = 32, ToolTip = label };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 110;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        panel.Children.Add(caption);
        var input = new DockPanel();
        var icon = TemplateVisuals.Icon(label);
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        input.Children.Add(icon);
        if (trailing != null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            input.Children.Add(trailing);
        }
        input.Children.Add(box);
        panel.Children.Add(input);
        parent.Children.Add(panel);
        return box;
    }

    private TextBox Number(Panel panel, string label, int value, Action<ItemTemplate, int> set, int min = int.MinValue, int max = int.MaxValue)
    {
        var box = Field(panel, label, value.ToString(CultureInfo.CurrentCulture));
        string original = box.Text;
        _readFields.Add(t =>
        {
            if (box.Text == original) return; // untouched captured values round-trip exactly
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) || n < min || n > max)
                Invalid(box, label + $": enter a whole number between {min} and {max}.");
            set(t, n);
        });
        return box;
    }

    private TextBox Decimal(Panel panel, string label, float value, Action<ItemTemplate, float> set)
    {
        var box = Field(panel, label, value.ToString("R", CultureInfo.CurrentCulture));
        string original = box.Text;
        _readFields.Add(t =>
        {
            if (box.Text == original) return;
            if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float n) || !float.IsFinite(n))
                Invalid(box, label + ": enter a finite number.");
            set(t, n);
        });
        return box;
    }

    private TextBox Scalar(Panel panel, string label, string key, bool fractional = false, int min = int.MinValue, int max = int.MaxValue)
    {
        return fractional
            ? Decimal(panel, label, _original.Values.Scalars[key].GetSingle(), (t, n) => t.Values.Scalars[key] = JsonSerializer.SerializeToElement(n))
            : Number(panel, label, _original.Values.Scalars[key].GetInt32(), (t, n) => t.Values.Scalars[key] = JsonSerializer.SerializeToElement(n), min, max);
    }

    // Values the game's save does not keep (DD1_INTERNALS.md §8c): they are
    // rebuilt from the item's base type on load, so editing them would only
    // last for the session. Shown greyed out, not editable.
    private void DisplayOnly(TextBox box)
    {
        box.IsReadOnly = true;
        box.SetResourceReference(TextBox.BackgroundProperty, "SurfaceLightBrush");
        box.ToolTip = "Display only: the game doesn't save this. It is recalculated from the item's base type when the item loads, so a change here would not last.";
    }

    private void BuildFields()
    {
        var bonuses = Section("Item bonuses", "Hero and tower bonuses carried by this item.");
        string[] names = ["Hero health", "Hero speed", "Hero damage", "Hero casting", "Ability 1", "Ability 2", "Tower health", "Tower attack rate", "Tower damage", "Tower range"];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            Number(bonuses, names[i], _original.Values.Stats[i], (t, n) => t.Values.Stats[index] = n);
        }

        var combat = Section("Combat", "Saved item values, before any hero, set or difficulty modifiers. Damage types stay as captured.");
        Scalar(combat, "Damage", nameof(ItemNative.WeaponDamageBonus));
        Scalar(combat, "Ranged damage", nameof(ItemNative.WeaponAltDamageBonus));
        Number(combat, "Elemental damage", _original.Values.ElementalDamage, (t, n) => t.Values.ElementalDamage = n);
        string[] resistances = ["Generic resistance", "Poison resistance", "Fire resistance", "Lightning resistance"];
        for (int i = 0; i < resistances.Length; i++)
        {
            int index = i;
            Number(combat, resistances[i], _original.Values.Resistances[i], (t, n) => t.Values.Resistances[index] = n, -99, 99);
        }
        Scalar(combat, "Blocking", nameof(ItemNative.WeaponBlockingBonus), min: -128, max: 128);
        Scalar(combat, "Knockback", nameof(ItemNative.WeaponKnockbackBonus), min: -128, max: 128);
        Scalar(combat, "Charge speed", nameof(ItemNative.WeaponChargeSpeedBonus), min: -128, max: 128);
        Scalar(combat, "Shots per second", nameof(ItemNative.WeaponShotsPerSecondBonus), min: -128, max: 128);
        Scalar(combat, "Projectiles", nameof(ItemNative.WeaponNumberOfProjectilesBonus), min: -128, max: 128);
        Scalar(combat, "Projectile speed", nameof(ItemNative.WeaponSpeedOfProjectilesBonus));
        Scalar(combat, "Clip ammo", nameof(ItemNative.WeaponClipAmmoBonus));
        Scalar(combat, "Reload speed", nameof(ItemNative.WeaponReloadSpeedBonus), min: -128, max: 128);
        Scalar(combat, "Swing speed multiplier", nameof(ItemNative.WeaponSwingSpeedMultiplier), true);
        DisplayOnly(Scalar(combat, "Elemental multiplier", nameof(ItemNative.MaxRandomElementalDamageMultiplier), true));

        var upgrades = Section("Upgrades", "These are current upgraded values, not reconstructed original rolls.");
        Scalar(upgrades, "Current level", nameof(ItemNative.Level), min: 0, max: 1000000);
        Scalar(upgrades, "Maximum level", nameof(ItemNative.MaxEquipmentLevel), min: 0, max: 1000000);
        Scalar(upgrades, "Stored mana", nameof(ItemNative.StoredMana));
        Scalar(upgrades, "Level requirement", nameof(ItemNative.ManualLR), min: 0, max: 255);
        DisplayOnly(Scalar(upgrades, "Requirement override", nameof(ItemNative.RequirementLevelOverride)));
        DisplayOnly(Scalar(upgrades, "Extra resistance upgrade points", nameof(ItemNative.AdditionalAllowedUpgradeResistancePoints)));
        Scalar(upgrades, "Minimum sell value", nameof(ItemNative.MinimumSellWorth));
        Scalar(upgrades, "Maximum sell value", nameof(ItemNative.MaximumSellWorth));
        DisplayOnly(Scalar(upgrades, "Shop minimum value", nameof(ItemNative.ShopMinimumSellWorth)));
        DisplayOnly(Scalar(upgrades, "Rating", nameof(ItemNative.MyRating), true));
        DisplayOnly(Scalar(upgrades, "Rating percent", nameof(ItemNative.MyRatingPercent), true));

        var appearance = Section("Appearance", "Size and the two color overrides, edited like in the item editor: pick a color or type R / G / B (0–255; values above 255 glow). Captured palette selections and base item stay fixed.", stacked: true);
        Scalar(appearance, "Size multiplier", nameof(ItemNative.WeaponDrawScaleMultiplier), true);
        ColorBlock(appearance, "Color 1 Override", primary: true);
        ColorBlock(appearance, "Color 2 Override", primary: false);

        var text = Section("Item text", "In-game text is separate from the library name. Use the palette button to color part of a text; in the single-line boxes a line break is written as \\n. The usual description watermark is added when copying.", stacked: true);
        AddText(text, "Item name", "Item Name", _original.EquipmentName, (t, s) => t.EquipmentName = s);
        AddText(text, "Description", "Description", _original.Description, (t, s) => t.Description = s, multiline: true);
        var forger = AddText(text, "Forger name", "Forger Name", _original.ForgerName, (t, s) => t.ForgerName = s, isForgerName: true);
        var warn = new TextBlock
        {
            Text = "Setting a forger name makes the item unsellable — the game only accepts names it verified itself. Clear this field to make it sellable again.",
            FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, -6, 12, 12)
        };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        void UpdateWarn() => warn.Visibility = string.IsNullOrWhiteSpace(ColorMarkup.Strip(forger.Text)) ? Visibility.Collapsed : Visibility.Visible;
        forger.TextChanged += (_, _) => UpdateWarn();
        UpdateWarn();
        text.Children.Add(warn);
    }

    // The item editor's color override block: title, swatch, Pick… and the
    // R / G / B boxes. The template file stores [A, R, G, B] floats; they are
    // only rewritten when the boxes differ from what they were seeded with
    // (alpha then becomes 1, as the item editor writes it), so an untouched
    // color round-trips exactly. While neither override is set the boxes
    // show the captured default color (templates saved since 2026-09-30
    // carry it) instead of black — the same rule as ItemColors.Shown.
    private static bool ColorSet(float[] c) => c[1] != 0f || c[2] != 0f || c[3] != 0f;

    private void ColorBlock(Panel parent, string title, bool primary)
    {
        var source = _original.Values;
        float[] stored = primary ? source.PrimaryColor : source.SecondaryColor;
        float[]? captured = primary ? source.DefaultPrimaryColor : source.DefaultSecondaryColor;
        if (!ColorSet(source.PrimaryColor) && !ColorSet(source.SecondaryColor) && captured is { Length: 4 })
            stored = captured;
        static int Channel(float f) => (int)Math.Round(Math.Clamp(f * 255f, -1000000f, 1000000f));
        var seed = (R: Channel(stored[1]), G: Channel(stored[2]), B: Channel(stored[3]));

        var block = new StackPanel { Margin = new Thickness(0, 0, 12, 14) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var pick = new Button { Content = "Pick...", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(pick, Dock.Right);
        var preview = new Border { Width = 32, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0) };
        preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(preview, Dock.Right);
        var caption = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        header.Children.Add(pick);
        header.Children.Add(preview);
        header.Children.Add(caption);
        block.Children.Add(header);

        var grid = new UniformGrid { Columns = 3 };
        var boxes = new TextBox[3];
        string[] names = ["R", "G", "B"];
        int[] values = [seed.R, seed.G, seed.B];
        for (int i = 0; i < 3; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 2 ? 0 : 4, 0) };
            var label = new TextBlock { Text = names[i], FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var box = new TextBox { Text = values[i].ToString(CultureInfo.CurrentCulture), MinHeight = 32, ToolTip = title + " " + names[i] };
            NumericInput.SetMode(box, NumericMode.Int);
            System.Windows.Automation.AutomationProperties.SetName(box, title + " " + names[i]);
            cell.Children.Add(label);
            cell.Children.Add(box);
            grid.Children.Add(cell);
            boxes[i] = box;
        }
        block.Children.Add(grid);
        parent.Children.Add(block);

        // Tolerant parsing — the preview updates on every keystroke.
        void UpdatePreview()
        {
            byte C(TextBox b) => (byte)Math.Clamp(int.TryParse(b.Text, out int n) ? n : 0, 0, 255);
            preview.Background = new SolidColorBrush(Color.FromRgb(C(boxes[0]), C(boxes[1]), C(boxes[2])));
        }
        foreach (var box in boxes) box.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        pick.Click += (_, _) =>
        {
            var initial = new LinearColor();
            int.TryParse(boxes[0].Text, out int r); initial.R = r;
            int.TryParse(boxes[1].Text, out int g); initial.G = g;
            int.TryParse(boxes[2].Text, out int b); initial.B = b;
            var dialog = new ColorPickerDialog(initial) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                boxes[0].Text = dialog.Result.R.ToString();
                boxes[1].Text = dialog.Result.G.ToString();
                boxes[2].Text = dialog.Result.B.ToString();
            }
        };

        _readFields.Add(t =>
        {
            var now = new int[3];
            for (int i = 0; i < 3; i++)
                if (!int.TryParse(boxes[i].Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out now[i]))
                    Invalid(boxes[i], title + " " + names[i] + ": enter a whole number.");
            if ((now[0], now[1], now[2]) == seed) return;
            float[] target = primary ? t.Values.PrimaryColor : t.Values.SecondaryColor;
            target[0] = 1f;
            target[1] = now[0] / 255f;
            target[2] = now[1] / 255f;
            target[3] = now[2] / 255f;
        });
    }

    // A text field with the item editor's color-text button. Single-line
    // boxes show a line break as the literal \n (the editor's convention);
    // it is turned back into a real newline on save. Untouched text
    // round-trips exactly.
    private TextBox AddText(Panel panel, string label, string editorTitle, string value, Action<ItemTemplate, string> set,
                            bool multiline = false, bool isForgerName = false)
    {
        var palette = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "\uE790",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Width = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            Height = multiline ? 32 : double.NaN,
            ToolTip = "Color editor — pick colors for this text."
        };
        string shown = multiline ? value : value.Replace("\r\n", "\n").Replace("\n", "\\n");
        var box = Field(panel, label, shown, multiline, palette);
        box.MaxLength = 8192;
        palette.Click += (_, _) =>
        {
            var dialog = new ColorTextDialog(box.Text ?? "", editorTitle, isForgerName) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            box.Text = multiline ? dialog.ResultMarkup : dialog.ResultMarkup.Replace("\n", "\\n");
        };
        _readFields.Add(t => set(t, box.Text == shown ? value : ColorMarkup.NormalizeNewlines(box.Text)));
        return box;
    }

    private void Invalid(TextBox box, string message)
    {
        foreach (var pair in _sections)
            if (pair.Value.IsAncestorOf(box)) { _tabs[pair.Key].IsChecked = true; break; }
        box.BringIntoView();
        box.Focus();
        box.SelectAll();
        throw new InvalidDataException(message);
    }

    // Always rebuild a fresh candidate. A failed field validation or disk save
    // cannot mutate the selected library entry, even after a second attempt.
    private ItemTemplate ReadDraft(bool asNew)
    {
        var candidate = _original.Copy(asNew);
        candidate.Name = TemplateName.Text.Trim();
        if (candidate.Name.Length == 0) Invalid(TemplateName, "Enter a template name.");
        foreach (var read in _readFields) read(candidate);
        // One color edited on a template with no overrides: the game would
        // draw the other black, so give it its captured default.
        var before = _original.Values;
        var now = candidate.Values;
        if (!ColorSet(before.PrimaryColor) && !ColorSet(before.SecondaryColor) &&
            ColorSet(now.PrimaryColor) != ColorSet(now.SecondaryColor))
        {
            if (!ColorSet(now.PrimaryColor) && now.DefaultPrimaryColor is { Length: 4 } p)
                now.PrimaryColor = [1f, p[1], p[2], p[3]];
            else if (!ColorSet(now.SecondaryColor) && now.DefaultSecondaryColor is { Length: 4 } s)
                now.SecondaryColor = [1f, s[1], s[2], s[3]];
        }
        int level = candidate.Values.Scalars[nameof(ItemNative.Level)].GetInt32();
        int max = candidate.Values.Scalars[nameof(ItemNative.MaxEquipmentLevel)].GetInt32();
        if (level < 0 || level > 1000000 || max < 0 || max > 1000000)
        {
            _tabs["Upgrades"].IsChecked = true;
            throw new InvalidDataException("Current level and maximum level must each be between 0 and 1000000.");
        }
        candidate.ModifiedUtc = DateTime.UtcNow;
        candidate.Validate();
        return candidate;
    }

    private void Save(bool asNew)
    {
        try
        {
            var candidate = ReadDraft(asNew);
            ItemTemplateLibrary.Save(candidate);
            Saved = candidate;
            DialogResult = true;
            // After this dialog has closed, so the toast lands in the window
            // the user is returned to. One place for every "add / edit
            // template" path.
            string saved = candidate.Name;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () => Toast.Show("“" + saved + "” is in your template library.", ToastKind.Success, "Template saved"));
        }
        catch (Exception ex) { Status.Text = "Template not saved: " + ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveNew_Click(object sender, RoutedEventArgs e) => Save(true);
}
