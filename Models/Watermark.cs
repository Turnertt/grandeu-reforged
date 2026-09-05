using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Modinator;

// Appends a small "made with" mark to the END of an item's Description
// whenever this tool edits that item.
//
// WHY Description AND NOT ForgerName
// The equipment object carries an online name-verification bitfield at
// object +0x00A8 — the field this project calls ItemNative.Flags (+0x70,
// since ItemNative starts at UObject+0x38). The SDK declares
// bIsNameOnlineVerified and bIsForgerNameOnlineVerified inside it, backed by
// CheckNameVerification / STATIC_CheckOnlineNameVerification /
// LocalCustomForgerNameVerified and a UProfanityFilter on the HeroManager.
// Writing a custom forger name from outside the process sets the text but
// never the verified bit, and the game then refuses to sell the item.
// EquipmentDescription (@0x0134) has NO verification bit anywhere in the SDK,
// so it sits outside that gate. Verified against SDK_20260904_174357.
//
// The mark is idempotent: the check runs against the COLOUR-STRIPPED text, so
// a palette change can never cause a second append, and an item is only ever
// grown once.
internal static class Watermark
{
    // The colour-free signature. This is what idempotency tests for — never
    // the rendered (coloured, bracketed) form. Deliberately the BARE name,
    // without brackets or any lead-in: it stays a substring of every form the
    // mark has ever taken ("Made with Grandeu Reforged", bracketed, at the
    // front), so items marked by an earlier build are still recognised and
    // never get marked a second time. Keep it that way if the framing changes.
    public const string Signature = "Grandeu Reforged";

    private const string Rainbow = "Grandeu Reforged";

    // Sits between the item's existing description and the mark. The mark is
    // ALSO bracketed — belt and braces on purpose: whether DD1 renders a
    // literal newline in a description is unverified, and the brackets keep
    // the mark visually separate from the item's own text even if it doesn't.
    private const string Separator = "\n";

    // Refuse to grow a description past this. Nothing in DD1 is known to cap
    // it (FEquipmentSaveInfo.Description is a variable-length FString), but an
    // unbounded append path on a field we re-read every edit deserves a stop.
    // 8192, not 1024: a description coloured per letter in the colour editor
    // costs ~26 characters a letter, so 1024 silently skipped the mark on
    // exactly the items people had just customised. ReadUni reads up to
    // 16384, so this stays well inside what the rest of the tool handles.
    private const int MaxTotalChars = 8192;

    private static readonly string Mark = BuildMark();

    // Escape hatch: prefs.json "WatermarkEditedItems": false. Deliberately no
    // UI — this is not meant to read as a headline feature.
    public static bool Enabled => Prefs.Current.WatermarkEditedItems;

    // Strip DD1 colour runs from a string for display / comparison. The bytes
    // in game memory are untouched — callers use this on a copy. One
    // implementation, in ColorMarkup, shared with the colour editor.
    public static string StripColorTags(string? s) => ColorMarkup.Strip(s);

    public static bool IsMarked(string? description) =>
        StripColorTags(description).Contains(Signature, StringComparison.OrdinalIgnoreCase);

    // Returns the description to write. APPENDS ONLY — the caller's existing
    // text is never replaced or reordered; the mark follows it on its own
    // line. Returns the input unchanged when the mark is disabled, already
    // present, or would overflow the cap, so callers can compare against the
    // input to see whether anything changed.
    public static string Apply(string? description)
    {
        string current = description ?? string.Empty;
        if (!Enabled) return current;
        if (IsMarked(current)) return current;

        // No leading separator on an empty description — don't leave the item
        // with a blank first line.
        string tail = current.Length == 0 ? Mark : Separator + Mark;
        if (current.Length + tail.Length > MaxTotalChars) return current;
        return current + tail;
    }

    // A per-letter hue sweep across "Grandeu Reforged", in brackets. Spaces
    // stay untagged: no visible colour on whitespace, and it keeps the string
    // shorter (the mark is ~390 chars as it is).
    private static string BuildMark()
    {
        int visible = 0;
        foreach (char c in Rainbow)
            if (c != ' ') visible++;

        var sb = new StringBuilder(visible * 28 + 2);
        sb.Append('[');

        int n = 0;
        foreach (char c in Rainbow)
        {
            if (c == ' ') { sb.Append(' '); continue; }
            // 0°–300°: red through magenta without wrapping back to red.
            var (r, g, b) = HueToRgb(visible > 1 ? n * 300.0 / (visible - 1) : 0.0);
            n++;
            sb.Append("<color:").Append(r).Append(',').Append(g).Append(',').Append(b).Append('>')
              .Append(c).Append("</color>");
        }
        // Brackets left uncoloured: they frame the mark, they aren't part of it.
        return sb.Append(']').ToString();
    }

    // Full saturation / full value HSV → RGB.
    private static (int R, int G, int B) HueToRgb(double degrees)
    {
        double h = (degrees % 360.0) / 60.0;
        double x = 1.0 - Math.Abs(h % 2.0 - 1.0);
        double r, g, b;
        switch ((int)h)
        {
            case 0:  r = 1; g = x; b = 0; break;
            case 1:  r = x; g = 1; b = 0; break;
            case 2:  r = 0; g = 1; b = x; break;
            case 3:  r = 0; g = x; b = 1; break;
            case 4:  r = x; g = 0; b = 1; break;
            default: r = 1; g = 0; b = x; break;
        }
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }
}
