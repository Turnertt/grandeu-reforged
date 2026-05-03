using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Modinator.Views;

// Save-time field validator. Each accessor parses one TextBox. On failure it
// paints the box red, records a message, and returns a harmless fallback so
// calling code can keep flowing. The caller checks IsValid at the end and
// aborts the write if anything failed. This replaces the old pattern of
// silently collapsing unparseable input to 0.
public sealed class FieldValidator
{
    private readonly List<(TextBox Box, string Message)> _errors = new();

    public bool IsValid => _errors.Count == 0;

    public int ErrorCount => _errors.Count;

    public int Int(TextBox tb, string label)
    {
        string t = (tb.Text ?? string.Empty).Trim();
        if (t.Length == 0) return Fail(tb, label, "required", 0);
        if (int.TryParse(
                t,
                NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out int v))
        {
            Ok(tb);
            return v;
        }
        return Fail(tb, label, $"'{t}' is not a valid integer", 0);
    }

    public uint UInt(TextBox tb, string label)
    {
        string t = (tb.Text ?? string.Empty).Trim();
        if (t.Length == 0) return (uint)Fail(tb, label, "required", 0);
        if (uint.TryParse(
                t,
                NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out uint v))
        {
            Ok(tb);
            return v;
        }
        return (uint)Fail(tb, label, $"'{t}' is not a valid unsigned integer", 0);
    }

    public byte Byte(TextBox tb, string label)
    {
        string t = (tb.Text ?? string.Empty).Trim();
        if (t.Length == 0) return (byte)Fail(tb, label, "required", 0);
        if (byte.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v))
        {
            Ok(tb);
            return v;
        }
        return (byte)Fail(tb, label, $"'{t}' is not a valid byte (0-255)", 0);
    }

    public float Float(TextBox tb, string label)
    {
        string t = (tb.Text ?? string.Empty).Trim();
        if (t.Length == 0) return Fail(tb, label, "required", 0f);
        if (float.TryParse(
                t,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out float v))
        {
            Ok(tb);
            return v;
        }
        return Fail(tb, label, $"'{t}' is not a valid number", 0f);
    }

    public string Report()
    {
        if (_errors.Count == 0) return string.Empty;
        if (_errors.Count == 1) return _errors[0].Message;
        return $"{_errors.Count} invalid fields — {_errors[0].Message}";
    }

    public void FocusFirstError()
    {
        var first = _errors.FirstOrDefault().Box;
        if (first == null) return;

        // BringIntoView must run after layout so the ScrollViewer has up-to-date
        // positions; dispatching at Loaded priority defers past the current pass.
        first.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            first.BringIntoView();
            first.Focus();
            first.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private int Fail(TextBox tb, string label, string reason, int fallback)
    {
        MarkError(tb);
        _errors.Add((tb, $"{label}: {reason}"));
        return fallback;
    }

    private float Fail(TextBox tb, string label, string reason, float fallback)
    {
        MarkError(tb);
        _errors.Add((tb, $"{label}: {reason}"));
        return fallback;
    }

    private static void Ok(TextBox tb)
    {
        tb.ClearValue(TextBox.BorderBrushProperty);
        tb.ClearValue(TextBox.BorderThicknessProperty);
        tb.ClearValue(TextBox.BackgroundProperty);
    }

    private static void MarkError(TextBox tb)
    {
        var brush = Application.Current?.TryFindResource("DangerBrush") as Brush
                    ?? Brushes.Red;
        tb.BorderBrush = brush;
        tb.BorderThickness = new Thickness(2);
        // Subtle red wash so the error reads even when the box isn't focused,
        // with a one-shot 250 ms pulse so the eye lands on it. Fresh brush
        // every time — animating a frozen/shared brush throws.
        var restingColor = Color.FromArgb(0x33, 0xED, 0x42, 0x45);
        var flashColor   = Color.FromArgb(0xAA, 0xED, 0x42, 0x45);
        var bg = new SolidColorBrush(flashColor);
        tb.Background = bg;
        var anim = new System.Windows.Media.Animation.ColorAnimation(
            flashColor, restingColor,
            new Duration(TimeSpan.FromMilliseconds(250)))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
        };
        bg.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
