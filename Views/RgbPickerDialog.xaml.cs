using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Modinator.Views;

// Simple RGB picker modeled after the item color picker's spectrum + hue-bar
// UI, but without HDR, glow, or saturation boost — DD1 clamps at 0-255 and
// anything beyond that renders wrong. Inputs and outputs are plain byte R/G/B.
public partial class RgbPickerDialog : Window
{
    public byte ResultR { get; private set; }
    public byte ResultG { get; private set; }
    public byte ResultB { get; private set; }

    // Default TRUE so XAML-load-time events fire into a no-op. Cleared at the
    // end of the constructor once all state is consistent.
    private bool _suppress = true;
    private bool _svDragging;
    private bool _hueDragging;
    private double _currentHue; // 0-360

    public RgbPickerDialog(byte r, byte g, byte b)
    {
        InitializeComponent();

        ResultR = r; ResultG = g; ResultB = b;
        TxtR.Text = r.ToString();
        TxtG.Text = g.ToString();
        TxtB.Text = b.ToString();

        SvCanvas.SizeChanged += (_, _) => LayoutSpectrum();
        HueCanvas.SizeChanged += (_, _) => LayoutHueBar();

        Loaded += (_, _) =>
        {
            LayoutSpectrum();
            LayoutHueBar();
            SyncSpectrumFromRgb();
            UpdatePreview();
        };

        _suppress = false;
    }

    // ── Spectrum/Hue layout — keep the overlays and indicator sized ─
    // Called on every SizeChanged and once on Loaded. Without this the SV
    // overlays stay at 0x0 and nothing is visible.

    private void LayoutSpectrum()
    {
        if (SvWhiteOverlay == null || SvBlackOverlay == null) return;
        double w = SvCanvas.ActualWidth;
        double h = SvCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        SvWhiteOverlay.Width = w; SvWhiteOverlay.Height = h;
        SvBlackOverlay.Width = w; SvBlackOverlay.Height = h;
    }

    private void LayoutHueBar()
    {
        if (HueIndicator == null) return;
        double w = HueCanvas.ActualWidth;
        if (w <= 0) return;
        HueIndicator.Width = w;
    }

    // ── SV canvas drag handling ─────────────────────────────────────

    private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _svDragging = true;
        SvCanvas.CaptureMouse();
        PickSv(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_svDragging) return;
        PickSv(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _svDragging = false;
        SvCanvas.ReleaseMouseCapture();
    }

    private void PickSv(Point pos)
    {
        double w = SvCanvas.ActualWidth;
        double h = SvCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double s = Math.Clamp(pos.X / w, 0, 1);
        double v = Math.Clamp(1 - pos.Y / h, 0, 1);
        Canvas.SetLeft(SvCrosshair, pos.X - 6);
        Canvas.SetTop(SvCrosshair, pos.Y - 6);

        HsvToRgb(_currentHue, s, v, out int r, out int g, out int bl);
        _suppress = true;
        TxtR.Text = r.ToString();
        TxtG.Text = g.ToString();
        TxtB.Text = bl.ToString();
        _suppress = false;
        UpdatePreview();
    }

    // ── Hue bar drag handling ───────────────────────────────────────

    private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _hueDragging = true;
        HueCanvas.CaptureMouse();
        PickHue(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_hueDragging) return;
        PickHue(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _hueDragging = false;
        HueCanvas.ReleaseMouseCapture();
    }

    private void PickHue(Point pos)
    {
        double h = HueCanvas.ActualHeight;
        if (h <= 0) return;

        _currentHue = Math.Clamp(pos.Y / h, 0, 1) * 360;
        Canvas.SetTop(HueIndicator, Math.Clamp(pos.Y - 2, 0, h - 4));

        HsvToRgb(_currentHue, 1, 1, out int hr, out int hg, out int hb);
        SvCanvas.Background = new SolidColorBrush(Color.FromRgb((byte)hr, (byte)hg, (byte)hb));

        // Recompute RGB at current crosshair position under the new hue
        double w = SvCanvas.ActualWidth;
        double svH = SvCanvas.ActualHeight;
        if (w > 0 && svH > 0)
        {
            double cx = Canvas.GetLeft(SvCrosshair) + 6;
            double cy = Canvas.GetTop(SvCrosshair) + 6;
            double s = Math.Clamp(cx / w, 0, 1);
            double v = Math.Clamp(1 - cy / svH, 0, 1);
            HsvToRgb(_currentHue, s, v, out int r, out int g, out int bl);
            _suppress = true;
            TxtR.Text = r.ToString();
            TxtG.Text = g.ToString();
            TxtB.Text = bl.ToString();
            _suppress = false;
        }
        UpdatePreview();
    }

    // ── RGB text inputs → update crosshair / hue indicator ──────────

    private void OnBaseChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        SyncSpectrumFromRgb();
        UpdatePreview();
    }

    private void SyncSpectrumFromRgb()
    {
        int r = Clamp255(TxtR);
        int g = Clamp255(TxtG);
        int b = Clamp255(TxtB);

        RgbToHsv(r, g, b, out double hue, out double sat, out double val);
        _currentHue = hue;

        double hueH = HueCanvas?.ActualHeight ?? 0;
        if (hueH > 0 && HueIndicator != null)
        {
            double hueY = (hue / 360.0) * hueH;
            Canvas.SetTop(HueIndicator, Math.Clamp(hueY - 2, 0, hueH - 4));
        }

        HsvToRgb(_currentHue, 1, 1, out int hr, out int hg, out int hb);
        if (SvCanvas != null)
            SvCanvas.Background = new SolidColorBrush(Color.FromRgb((byte)hr, (byte)hg, (byte)hb));

        double svW = SvCanvas?.ActualWidth ?? 0;
        double svH = SvCanvas?.ActualHeight ?? 0;
        if (svW > 0 && svH > 0 && SvCrosshair != null)
        {
            Canvas.SetLeft(SvCrosshair, sat * svW - 6);
            Canvas.SetTop(SvCrosshair, (1 - val) * svH - 6);
        }
    }

    // ── Hex field → RGB ─────────────────────────────────────────────

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (TxtHex == null) return;
        string s = TxtHex.Text.Trim().TrimStart('#');
        if (s.Length != 6) return;
        if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)) return;
        if (!byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)) return;
        if (!byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) return;

        _suppress = true;
        TxtR.Text = r.ToString();
        TxtG.Text = g.ToString();
        TxtB.Text = b.ToString();
        _suppress = false;
        SyncSpectrumFromRgb();
        UpdatePreview(skipHex: true);
    }

    // ── Preview + hex sync ──────────────────────────────────────────

    private void UpdatePreview(bool skipHex = false)
    {
        if (TxtR == null || TxtG == null || TxtB == null) return;
        byte r = (byte)Clamp255(TxtR);
        byte g = (byte)Clamp255(TxtG);
        byte b = (byte)Clamp255(TxtB);

        if (PreviewFill != null)
            PreviewFill.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (TxtPreviewInfo != null)
            TxtPreviewInfo.Text = $"R: {r}  G: {g}  B: {b}";
        string hex = $"{r:X2}{g:X2}{b:X2}";
        if (TxtPreviewHex != null)
            TxtPreviewHex.Text = "#" + hex;

        if (!skipHex && TxtHex != null)
        {
            _suppress = true;
            TxtHex.Text = hex;
            _suppress = false;
        }
    }

    private static int Clamp255(TextBox? tb)
    {
        if (tb == null) return 0;
        if (!int.TryParse(tb.Text, out int v)) return 0;
        return Math.Clamp(v, 0, 255);
    }

    // ── OK / Cancel ─────────────────────────────────────────────────

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ResultR = (byte)Clamp255(TxtR);
        ResultG = (byte)Clamp255(TxtG);
        ResultB = (byte)Clamp255(TxtB);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ── HSV ↔ RGB helpers ───────────────────────────────────────────

    private static void HsvToRgb(double h, double s, double v, out int r, out int g, out int b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double r1, g1, b1;

        if (h < 60)       { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else              { r1 = c; g1 = 0; b1 = x; }

        r = (int)Math.Round((r1 + m) * 255);
        g = (int)Math.Round((g1 + m) * 255);
        b = (int)Math.Round((b1 + m) * 255);
    }

    private static void RgbToHsv(int r, int g, int b, out double h, out double s, out double v)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        v = max;
        s = (max > 0) ? delta / max : 0;

        if (delta == 0) { h = 0; return; }

        if (max == rf)      h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
        else                h = 60 * (((rf - gf) / delta) + 4);

        if (h < 0) h += 360;
    }
}
