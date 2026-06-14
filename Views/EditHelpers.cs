using System.Windows.Controls;

namespace Modinator.Views;

// Shared plumbing for the edit screens + bulk dialog: the placeholder-hint
// pattern (empty box = "keep the current value") and its parse-or-fallback
// accessors. One home — previously copied verbatim in ItemEditView,
// HeroEditView and BulkEditDialog.
internal static class EditHelpers
{
    // Blank the text box and stuff the current value into the grey placeholder.
    public static void SetHint(TextBox tb, string current)
    {
        tb.Text = "";
        Behaviors.Placeholder.SetText(tb, current);
    }

    // If the user typed something, parse it; otherwise keep the fallback value.
    public static int IntOr(FieldValidator v, TextBox tb, string label, int fallback)
        => string.IsNullOrWhiteSpace(tb.Text) ? fallback : v.Int(tb, label);

    public static byte ByteOr(FieldValidator v, TextBox tb, string label, byte fallback)
        => string.IsNullOrWhiteSpace(tb.Text) ? fallback : v.Byte(tb, label);

    public static float FloatOr(FieldValidator v, TextBox tb, string label, float fallback)
        => string.IsNullOrWhiteSpace(tb.Text) ? fallback : v.Float(tb, label);

    public static string StrOr(TextBox tb, string? fallback)
        => string.IsNullOrEmpty(tb.Text) ? (fallback ?? "") : tb.Text;
}
