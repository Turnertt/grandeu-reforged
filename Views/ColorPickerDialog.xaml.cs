using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Modinator.Views;

public partial class ColorPickerDialog : Window
{
    internal LinearColor? Result { get; private set; }

    private float _originalA = 1f;
    private bool _suppress;
    private bool _svDragging;
    private bool _hueDragging;
    private double _currentHue; // 0-360

    public ColorPickerDialog(LinearColor? initial)
    {
        _suppress = true;
        InitializeComponent();
        _originalA = (initial != null) ? initial.Af : 1f;
        Result = initial;
        DecomposeInitial(initial);
        _suppress = false;

        SvCanvas.SizeChanged += (s, e) => LayoutSpectrum();
        HueCanvas.SizeChanged += (s, e) => LayoutHueBar();

        Loaded += (s, e) =>
        {
            LayoutSpectrum();
            LayoutHueBar();
            SyncSpectrumFromRgb();
            UpdatePreview();
        };
    }

    // ── Spectrum layout ─────────────────────────────────────────────

    private void LayoutSpectrum()
    {
        if (SvCanvas.ActualWidth <= 0 || SvCanvas.ActualHeight <= 0) return;
        double w = SvCanvas.ActualWidth;
        double h = SvCanvas.ActualHeight;

        SvWhiteOverlay.Width = w;
        SvWhiteOverlay.Height = h;
        SvWhiteOverlay.Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, 0);

        SvBlackOverlay.Width = w;
        SvBlackOverlay.Height = h;
    }

    private void LayoutHueBar()
    {
        if (HueCanvas.ActualWidth <= 0) return;
        HueIndicator.Width = HueCanvas.ActualWidth;
    }

    // ── SV Canvas (Saturation/Value square) ─────────────────────────

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

        // Position crosshair
        Canvas.SetLeft(SvCrosshair, pos.X - 6);
        Canvas.SetTop(SvCrosshair, pos.Y - 6);

        // Convert HSV to RGB
        HsvToRgb(_currentHue, s, v, out int r, out int g, out int b);
        _suppress = true;
        TxtR.Text = r.ToString();
        TxtG.Text = g.ToString();
        TxtB.Text = b.ToString();
        _suppress = false;
        UpdatePreview();
    }

    // ── Hue bar ─────────────────────────────────────────────────────

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

        // Update the SV canvas background to the pure hue
        HsvToRgb(_currentHue, 1, 1, out int hr, out int hg, out int hb);
        SvCanvas.Background = new SolidColorBrush(Color.FromRgb((byte)hr, (byte)hg, (byte)hb));

        // Recompute current color with new hue, keeping current S/V
        double w = SvCanvas.ActualWidth;
        double svH = SvCanvas.ActualHeight;
        if (w > 0 && svH > 0)
        {
            double cx = Canvas.GetLeft(SvCrosshair) + 6;
            double cy = Canvas.GetTop(SvCrosshair) + 6;
            double s = Math.Clamp(cx / w, 0, 1);
            double v = Math.Clamp(1 - cy / svH, 0, 1);
            HsvToRgb(_currentHue, s, v, out int r, out int g, out int b);
            _suppress = true;
            TxtR.Text = r.ToString();
            TxtG.Text = g.ToString();
            TxtB.Text = b.ToString();
            _suppress = false;
        }
        UpdatePreview();
    }

    // ── Sync spectrum position from RGB text boxes ──────────────────

    private void SyncSpectrumFromRgb()
    {
        int.TryParse(TxtR.Text, out int r);
        int.TryParse(TxtG.Text, out int g);
        int.TryParse(TxtB.Text, out int b);
        // Clamp for HSV math only (indicator position) — the raw values are
        // preserved in the text boxes and written back on OK without clamping.
        int rc = Math.Clamp(r, 0, 255);
        int gc = Math.Clamp(g, 0, 255);
        int bc = Math.Clamp(b, 0, 255);

        RgbToHsv(rc, gc, bc, out double hue, out double sat, out double val);
        _currentHue = hue;

        // Position hue indicator
        double hueH = HueCanvas.ActualHeight;
        if (hueH > 0)
        {
            double hueY = (hue / 360.0) * hueH;
            Canvas.SetTop(HueIndicator, Math.Clamp(hueY - 2, 0, hueH - 4));
        }

        // Set SV canvas background
        HsvToRgb(_currentHue, 1, 1, out int hr, out int hg, out int hb);
        SvCanvas.Background = new SolidColorBrush(Color.FromRgb((byte)hr, (byte)hg, (byte)hb));

        // Position crosshair
        double svW = SvCanvas.ActualWidth;
        double svH = SvCanvas.ActualHeight;
        if (svW > 0 && svH > 0)
        {
            Canvas.SetLeft(SvCrosshair, sat * svW - 6);
            Canvas.SetTop(SvCrosshair, (1 - val) * svH - 6);
        }
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void OnBaseChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        SyncSpectrumFromRgb();
        UpdatePreview();
    }

    private void OnIntensitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _suppress = true;
        double v = SliderIntensity.Value / 100.0;
        TxtIntensity.Text = v.ToString("F2");
        _suppress = false;
        UpdatePreview();
    }

    private void OnIntensityTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (!double.TryParse(TxtIntensity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
        _suppress = true;
        SliderIntensity.Value = Math.Clamp(v * 100, 0, 1000);
        _suppress = false;
        UpdatePreview();
    }

    private void OnSaturationSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _suppress = true;
        double v = SliderSaturation.Value / 100.0;
        TxtSaturation.Text = v.ToString("F2");
        _suppress = false;
        UpdatePreview();
    }

    private void OnSaturationTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (!double.TryParse(TxtSaturation.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
        _suppress = true;
        SliderSaturation.Value = Math.Clamp(v * 100, 0, 500);
        _suppress = false;
        UpdatePreview();
    }

    // ── Decompose initial LinearColor ───────────────────────────────

    private void DecomposeInitial(LinearColor? c)
    {
        if (c == null) return;

        SliderSaturation.Value = 0;
        TxtSaturation.Text = "0.00";

        float maxAbs = Math.Max(Math.Max(Math.Abs(c.Rf), Math.Abs(c.Gf)), Math.Abs(c.Bf));
        float intensity = (maxAbs > 1f) ? maxAbs : 1f;

        double iVal = Math.Clamp(intensity, 0, 10);
        TxtIntensity.Text = iVal.ToString("F2");
        SliderIntensity.Value = Math.Clamp(intensity * 100, 0, 1000);

        float scale = (intensity > 0f) ? intensity : 1f;
        int r = (int)Math.Round((c.Rf / scale) * 255f);
        int g = (int)Math.Round((c.Gf / scale) * 255f);
        int b = (int)Math.Round((c.Bf / scale) * 255f);
        // Don't clamp — DD1 items support negative values (dark glow) and
        // HDR values > 255. The intensity slider above already handles the
        // HDR case, but preserve user-entered out-of-range values verbatim.
        TxtR.Text = r.ToString();
        TxtG.Text = g.ToString();
        TxtB.Text = b.ToString();
    }

    // ── Core HDR math ───────────────────────────────────────────────

    private void ComputeFinal(out float fr, out float fg, out float fb)
    {
        int.TryParse(TxtR.Text, out int ri);
        int.TryParse(TxtG.Text, out int gi);
        int.TryParse(TxtB.Text, out int bi);
        // No clamp — negative and >255 input values are intentional for DD1
        // (glow / HDR effects). The float representation preserves them.
        float baseR = ri / 255f;
        float baseG = gi / 255f;
        float baseB = bi / 255f;

        float.TryParse(TxtIntensity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float inten);
        float.TryParse(TxtSaturation.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float sat);

        float maxBase = Math.Max(Math.Max(baseR, baseG), baseB);
        if (maxBase <= 0f) { fr = 0f; fg = 0f; fb = 0f; return; }

        if (sat <= 0f)
        {
            fr = baseR * inten; fg = baseG * inten; fb = baseB * inten;
            return;
        }

        float domR = baseR / maxBase, domG = baseG / maxBase, domB = baseB / maxBase;
        float exp = 1f + sat;
        float wR = MathF.Pow(domR, exp), wG = MathF.Pow(domG, exp), wB = MathF.Pow(domB, exp);
        float factor = inten - 1f;
        fr = baseR * (1f + factor * wR);
        fg = baseG * (1f + factor * wG);
        fb = baseB * (1f + factor * wB);
    }

    // ── Preview ─────────────────────────────────────────────────────

    private void UpdatePreview()
    {
        if (PreviewFill == null || GlowLayer == null || TxtR == null || TxtIntensity == null || TxtSaturation == null) return;

        ComputeFinal(out float fr, out float fg, out float fb);

        byte dr = ClampByte((int)Math.Round(fr * 255f));
        byte dg = ClampByte((int)Math.Round(fg * 255f));
        byte db = ClampByte((int)Math.Round(fb * 255f));
        Color displayColor = Color.FromRgb(dr, dg, db);

        PreviewFill.Background = new SolidColorBrush(displayColor);
        GlowLayer.Background = new SolidColorBrush(displayColor);

        float.TryParse(TxtIntensity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float inten);
        GlowLayer.Opacity = Math.Clamp((inten - 0.5) * 0.4, 0, 1);

        int.TryParse(TxtR.Text, out int ri);
        int.TryParse(TxtG.Text, out int gi);
        int.TryParse(TxtB.Text, out int bi);
        if (BaseColorSwatch != null)
            BaseColorSwatch.Background = new SolidColorBrush(Color.FromRgb(ClampByte(ri), ClampByte(gi), ClampByte(bi)));

        if (TxtPreviewInfo != null) TxtPreviewInfo.Text = $"R: {dr}  G: {dg}  B: {db}";
        if (TxtPreviewFloat != null) TxtPreviewFloat.Text = $"HDR: {fr:F2}, {fg:F2}, {fb:F2}";

        if (TxtIntensityHint != null)
        {
            TxtIntensityHint.Text = inten switch
            {
                <= 0.01f => "black",
                < 0.5f => "dim",
                < 1.5f => "normal",
                < 3f => "bright",
                < 6f => "glow",
                _ => "overbright"
            };
        }
    }

    // ── OK / Cancel ─────────────────────────────────────────────────

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ComputeFinal(out float fr, out float fg, out float fb);
        Result = new LinearColor();
        Result.Rf = fr;
        Result.Gf = fg;
        Result.Bf = fb;
        Result.Af = _originalA;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // ── HSV <-> RGB conversion ──────────────────────────────────────

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

    private static byte ClampByte(int n)
    {
        if (n < 0) return 0;
        if (n > 255) return 255;
        return (byte)n;
    }
}
