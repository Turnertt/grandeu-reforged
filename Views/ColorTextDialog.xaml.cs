using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Modinator.Views;

// Visual editor for DD1's <color:r,g,b> runs. Backed by a RichTextBox so text
// is coloured in place — select, click a swatch, done — instead of asking
// anyone to hand-write tags.
//
// The document is the source of truth while the dialog is open: markup is
// parsed in on load and serialised back out on APPLY (ColorMarkup owns both
// directions).
public partial class ColorTextDialog : Window
{
    // Result markup. Only meaningful when ShowDialog() returned true.
    public string ResultMarkup { get; private set; } = string.Empty;

    // The theme's normal text colour doubles as "no colour": text at this
    // colour serialises WITHOUT a tag, so the game renders it in whatever it
    // uses by default. That is what REMOVE COLOUR restores.
    private readonly Color _defaultColor;

    private static readonly (string Name, byte R, byte G, byte B)[] Palette =
    {
        ("White",   255, 255, 255), ("Silver",  190, 195, 205),
        ("Red",     220,  60,  50), ("Orange",  240, 140,  30),
        ("Gold",    235, 200,  60), ("Yellow",  250, 245, 110),
        ("Lime",    140, 220,  70), ("Green",    70, 175,  95),
        ("Teal",     60, 195, 180), ("Cyan",    100, 215, 245),
        ("Blue",     70, 130, 200), ("Indigo",  100, 110, 225),
        ("Violet",  150,  90, 200), ("Magenta", 225,  90, 200),
        ("Pink",    245, 150, 185), ("Brown",   160, 110,  70),
        ("Slate",   120, 125, 135), ("Black",     0,   0,   0),
    };

    public ColorTextDialog(string markup, string fieldTitle, bool isForgerName)
    {
        InitializeComponent();

        _defaultColor = ResolveDefaultColor();
        LblTitle.Text = fieldTitle;
        WarnBar.Visibility = isForgerName ? Visibility.Visible : Visibility.Collapsed;

        BuildSwatches();
        LoadMarkup(markup);
        UpdateLength();

        Loaded += (_, _) => { Editor.Focus(); Editor.SelectAll(); };
    }

    private Color ResolveDefaultColor()
    {
        try
        {
            if (TryFindResource("TextPrimaryBrush") is SolidColorBrush b) return b.Color;
        }
        catch { }
        return Colors.White;
    }

    private void BuildSwatches()
    {
        foreach (var (name, r, g, b) in Palette)
        {
            Color color = Color.FromRgb(r, g, b);
            var chip = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name + " — " + r + "," + g + "," + b,
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                TxtR.Text = r.ToString();
                TxtG.Text = g.ToString();
                TxtB.Text = b.ToString();
                ApplyColorToSelection(color);
            };
            Swatches.Children.Add(chip);
        }
    }

    // -- markup <-> document -------------------------------------------

    private void LoadMarkup(string markup)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        var para = new Paragraph { Margin = new Thickness(0) };
        var defaultBrush = new SolidColorBrush(_defaultColor);

        foreach (ColorRun run in ColorMarkup.Parse(markup))
        {
            Brush brush = run.HasColor
                ? new SolidColorBrush(Color.FromRgb(run.R, run.G, run.B))
                : defaultBrush;

            // DD1 descriptions can hold newlines. Keep them as LineBreaks
            // inside ONE paragraph so serialising can't invent blank lines.
            string[] lines = run.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) para.Inlines.Add(new LineBreak());
                if (lines[i].Length > 0)
                    para.Inlines.Add(new Run(lines[i]) { Foreground = brush });
            }
        }

        doc.Blocks.Add(para);
        Editor.Document = doc;
    }

    private string SerializeDocument()
    {
        var runs = new List<ColorRun>();
        bool firstBlock = true;
        foreach (Block block in Editor.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            // The user pressed Enter and WPF made a new paragraph — that is a
            // newline as far as the game string is concerned.
            if (!firstBlock) runs.Add(new ColorRun("\n"));
            firstBlock = false;
            CollectInlines(p.Inlines, runs, _defaultColor);
        }
        return ColorMarkup.Serialize(runs);
    }

    // `inherited` carries a Span's colour down to child Runs that don't set
    // their own. Without it, colouring a selection that WPF chose to wrap in
    // a Span would serialise as uncoloured.
    private void CollectInlines(InlineCollection inlines, List<ColorRun> runs, Color inherited)
    {
        foreach (Inline il in inlines)
        {
            Color own = il.Foreground is SolidColorBrush sb ? sb.Color : inherited;
            switch (il)
            {
                case Run r when r.Text.Length > 0:
                    runs.Add(own == _defaultColor
                        ? new ColorRun(r.Text)
                        : new ColorRun(r.Text, own.R, own.G, own.B));
                    break;
                case LineBreak:
                    runs.Add(new ColorRun("\n"));
                    break;
                case Span s:
                    CollectInlines(s.Inlines, runs, own);
                    break;
            }
        }
    }

    // -- colour actions -------------------------------------------------

    private void ApplyColorToSelection(Color color)
    {
        if (Editor.Selection.IsEmpty)
        {
            LblLength.Text = "Select some text first, then pick a color.";
            return;
        }
        Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
        UpdateLength();
    }

    private void BtnApplyRgb_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRgb(out Color c))
        {
            LblLength.Text = "R, G and B each need to be a whole number from 0 to 255.";
            return;
        }
        ApplyColorToSelection(c);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
        => ApplyColorToSelection(_defaultColor);

    // One hue per letter across the selection. Whitespace is skipped: there is
    // no visible colour on a space, and it keeps the markup shorter.
    private void BtnRainbow_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Selection;
        if (sel.IsEmpty)
        {
            LblLength.Text = "Select some text first, then press RAINBOW.";
            return;
        }

        var cells = new List<TextRange>();
        TextPointer? p = sel.Start.GetInsertionPosition(LogicalDirection.Forward);
        while (p != null && p.CompareTo(sel.End) < 0)
        {
            TextPointer? next = p.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null || next.CompareTo(sel.End) > 0) break;
            var cell = new TextRange(p, next);
            if (!string.IsNullOrWhiteSpace(cell.Text)) cells.Add(cell);
            p = next;
        }
        if (cells.Count == 0) return;

        for (int i = 0; i < cells.Count; i++)
        {
            double deg = cells.Count > 1 ? i * 300.0 / (cells.Count - 1) : 0.0;
            cells[i].ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(HueToRgb(deg)));
        }
        UpdateLength();
    }

    private bool TryReadRgb(out Color color)
    {
        color = Colors.White;
        if (!byte.TryParse((TxtR.Text ?? "").Trim(), out byte r)) return false;
        if (!byte.TryParse((TxtG.Text ?? "").Trim(), out byte g)) return false;
        if (!byte.TryParse((TxtB.Text ?? "").Trim(), out byte b)) return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    // Full saturation / value HSV sweep - matches Models/Watermark.cs.
    private static Color HueToRgb(double degrees)
    {
        double h = (degrees % 360.0) / 60.0;
        double x = 1.0 - Math.Abs(h % 2.0 - 1.0);
        double r, g, b;
        switch ((int)h)
        {
            case 0: r = 1; g = x; b = 0; break;
            case 1: r = x; g = 1; b = 0; break;
            case 2: r = 0; g = 1; b = x; break;
            case 3: r = 0; g = x; b = 1; break;
            case 4: r = x; g = 0; b = 1; break;
            default: r = 1; g = 0; b = x; break;
        }
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    // -- status ----------------------------------------------------------

    private void UpdateLength()
    {
        try
        {
            string markup = SerializeDocument();
            string plain = ColorMarkup.Strip(markup);
            string note = plain.Length + " characters of text · " + markup.Length + " stored with tags";
            // Colour markup is verbose (~26 characters per coloured letter)
            // and the result is written into game memory, so flag runaway
            // growth here rather than letting it surprise anyone later.
            if (markup.Length > 1024) note += "  —  very long; consider coloring fewer letters";
            LblLength.Text = note;
        }
        catch { LblLength.Text = string.Empty; }
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // Mirror the selection's colour into the R/G/B boxes, so picking up an
        // existing colour to reuse elsewhere is one click.
        try
        {
            object v = Editor.Selection.GetPropertyValue(TextElement.ForegroundProperty);
            if (v is SolidColorBrush b)
            {
                TxtR.Text = b.Color.R.ToString();
                TxtG.Text = b.Color.G.ToString();
                TxtB.Text = b.Color.B.ToString();
            }
        }
        catch { }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => UpdateLength();

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ResultMarkup = SerializeDocument();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
