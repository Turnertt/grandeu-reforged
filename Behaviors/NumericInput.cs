using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Modinator.Behaviors;

public enum NumericMode
{
    None,
    Int,
    UInt,
    Byte,
    Float,
}

// Input-time gate for numeric TextBoxes. Blocks keystrokes and pastes whose
// result wouldn't be a prefix of a valid number. Full parse/range checks
// happen later in FieldValidator on Update.
public static class NumericInput
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(NumericMode),
            typeof(NumericInput),
            new PropertyMetadata(NumericMode.None, OnModeChanged));

    public static void SetMode(DependencyObject d, NumericMode v) => d.SetValue(ModeProperty, v);
    public static NumericMode GetMode(DependencyObject d) => (NumericMode)d.GetValue(ModeProperty);

    private static readonly Regex IntRx = new(@"^-?[\d,]*$", RegexOptions.Compiled);
    private static readonly Regex UIntRx = new(@"^[\d,]*$", RegexOptions.Compiled);
    private static readonly Regex ByteRx = new(@"^\d{0,3}$", RegexOptions.Compiled);
    private static readonly Regex FloatRx = new(@"^-?[\d,]*\.?\d*$", RegexOptions.Compiled);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        tb.PreviewTextInput -= OnPreviewTextInput;
        tb.PreviewKeyDown -= OnPreviewKeyDown;
        tb.TextChanged -= OnTextChanged;
        DataObject.RemovePastingHandler(tb, OnPaste);

        if ((NumericMode)e.NewValue == NumericMode.None) return;

        tb.PreviewTextInput += OnPreviewTextInput;
        tb.PreviewKeyDown += OnPreviewKeyDown;
        tb.TextChanged += OnTextChanged;
        DataObject.AddPastingHandler(tb, OnPaste);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox tb) return;
        string proposed = BuildProposed(tb, e.Text);
        if (!IsAcceptable(proposed, GetMode(tb))) e.Handled = true;
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb) { e.CancelCommand(); return; }
        if (!e.DataObject.GetDataPresent(typeof(string))) { e.CancelCommand(); return; }
        string pasted = (string)e.DataObject.GetData(typeof(string));
        string proposed = BuildProposed(tb, pasted);
        if (!IsAcceptable(proposed, GetMode(tb))) e.CancelCommand();
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // Clear any validation-error border once the user starts correcting.
        if (sender is TextBox tb) tb.ClearValue(TextBox.BorderBrushProperty);
    }

    private static string BuildProposed(TextBox tb, string insert)
    {
        int start = tb.SelectionStart;
        int len = tb.SelectionLength;
        string text = tb.Text ?? string.Empty;
        return text.Substring(0, start) + insert + text.Substring(start + len);
    }

    private static bool IsAcceptable(string s, NumericMode mode) => mode switch
    {
        NumericMode.Int => IntRx.IsMatch(s),
        NumericMode.UInt => UIntRx.IsMatch(s),
        NumericMode.Byte => ByteRx.IsMatch(s),
        NumericMode.Float => FloatRx.IsMatch(s),
        _ => true,
    };
}
