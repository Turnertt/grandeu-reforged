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

    // prefs.json "WatermarkEditedItems"; the Settings row for it is not
    // shown by default. While off: nothing new is marked, and an existing
    // mark can be taken off (Remove) because no edit path will put it back.
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

    // The description with every form of the mark taken out: the colored
    // or plain name, its brackets, an old "Made with " lead-in, and the one
    // line break that separated it from the item's own text. Works on the
    // text the user SEES (color runs parsed, then re-emitted), so a mark
    // colored letter by letter is found like a plain one; the rest of the
    // description keeps its colors. Unmarked input comes back untouched.
    // Never returns "" — DD1 crashes on a blank FString, so an item whose
    // whole description was the mark gets a single space.
    public static string Remove(string? description)
    {
        string current = description ?? string.Empty;
        if (!IsMarked(current)) return current;

        var runs = ColorMarkup.Parse(current);
        var plain = new StringBuilder();
        foreach (var run in runs) plain.Append(run.Text);
        string text = plain.ToString();

        var cut = new bool[text.Length];
        const string LeadIn = "Made with ";
        int from = 0;
        while (from < text.Length)
        {
            int at = text.IndexOf(Signature, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            int start = at, end = at + Signature.Length;
            if (start >= LeadIn.Length &&
                string.Compare(text, start - LeadIn.Length, LeadIn, 0, LeadIn.Length, StringComparison.OrdinalIgnoreCase) == 0)
                start -= LeadIn.Length;
            if (start > 0 && text[start - 1] == '[' && end < text.Length && text[end] == ']') { start--; end++; }
            // One separator goes with it: the break before a trailing mark,
            // else the break after a leading one.
            if (start > 0 && text[start - 1] == '\n') start--;
            else if (end < text.Length && text[end] == '\n') end++;
            for (int i = start; i < end; i++) cut[i] = true;
            from = at + Signature.Length;
        }

        var kept = new List<ColorRun>();
        int pos = 0;
        foreach (var run in runs)
        {
            var sb = new StringBuilder(run.Text.Length);
            foreach (char c in run.Text)
            {
                if (!cut[pos]) sb.Append(c);
                pos++;
            }
            if (sb.Length == 0) continue;
            kept.Add(run.HasColor ? new ColorRun(sb.ToString(), run.R, run.G, run.B) : new ColorRun(sb.ToString()));
        }
        string result = ColorMarkup.Serialize(kept).Trim('\n', '\r');
        return string.IsNullOrWhiteSpace(ColorMarkup.Strip(result)) ? " " : result;
    }

    // The same mark without the per-letter colors: 18 characters instead of
    // ~390. For a buffer the full mark can't fit into (Item Dupe writes into
    // the target's own, exact-fit description buffer). IsMarked recognises it.
    public static string ApplyCompact(string? description)
    {
        string current = description ?? string.Empty;
        if (!Enabled || IsMarked(current)) return current;
        string mark = "[" + Signature + "]";
        return current.Length == 0 ? mark : current + Separator + mark;
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
