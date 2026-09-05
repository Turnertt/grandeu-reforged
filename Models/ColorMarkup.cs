using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Modinator;

// One coloured (or uncoloured) span of an item string.
public readonly struct ColorRun
{
    public readonly string Text;
    public readonly bool HasColor;
    public readonly byte R, G, B;

    public ColorRun(string text) { Text = text; HasColor = false; R = G = B = 0; }
    public ColorRun(string text, byte r, byte g, byte b) { Text = text; HasColor = true; R = r; G = g; B = b; }
}

// DD1 rich text: <color:r,g,b>text</color>, the same markup the game's own
// coloured item names carry. Lives in Models (no WPF) so the parse/emit rules
// are testable on their own and the UI just maps runs to brushes.
//
// Deliberately FORGIVING: these strings come out of game memory and out of
// other people's tools, so unbalanced, nested, malformed and stray tags all
// have to round-trip into something sane rather than throwing or garbling the
// user's text.
public static class ColorMarkup
{
    // Loose: matches any color tag, well-formed or not, so Strip removes
    // everything the game would treat as markup. Parse uses the same matches
    // and pulls the RGB out separately — a tag it cannot read is dropped
    // rather than shown to the user as literal text.
    private static readonly Regex TagRx =
        new("</?color[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RgbRx =
        new(@"^<color\s*:\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Plain text with every colour run removed. The bytes in game memory are
    // untouched — callers use this on a copy, for display and comparison.
    public static string Strip(string? s) => TagRx.Replace(s ?? string.Empty, string.Empty).Trim();

    public static List<ColorRun> Parse(string? markup)
    {
        var runs = new List<ColorRun>();
        string s = markup ?? string.Empty;
        if (s.Length == 0) return runs;

        // A stack, so nested runs behave and </color> restores the enclosing
        // colour instead of clearing to default.
        var open = new Stack<(byte r, byte g, byte b)?>();
        int pos = 0;

        void Emit(string text)
        {
            if (text.Length == 0) return;
            var top = open.Count > 0 ? open.Peek() : null;
            if (top is (byte r, byte g, byte b)) runs.Add(new ColorRun(text, r, g, b));
            else runs.Add(new ColorRun(text));
        }

        foreach (Match m in TagRx.Matches(s))
        {
            if (m.Index > pos) Emit(s.Substring(pos, m.Index - pos));
            pos = m.Index + m.Length;

            bool isClose = m.Value.StartsWith("</", System.StringComparison.Ordinal);
            if (isClose)
            {
                if (open.Count > 0) open.Pop();   // stray close: ignore
                continue;
            }
            Match rgb = RgbRx.Match(m.Value);
            if (rgb.Success &&
                byte.TryParse(rgb.Groups[1].Value, out byte r) &&
                byte.TryParse(rgb.Groups[2].Value, out byte g) &&
                byte.TryParse(rgb.Groups[3].Value, out byte b))
            {
                open.Push((r, g, b));
            }
            else
            {
                // Unreadable open tag (out-of-range channel, junk payload):
                // drop the tag but still push, so its </color> pops THIS and
                // doesn't clear a legitimate enclosing colour.
                open.Push(open.Count > 0 ? open.Peek() : null);
            }
        }
        if (pos < s.Length) Emit(s.Substring(pos));
        return runs;
    }

    // Runs back to markup. Adjacent runs of the same colour are merged: a
    // RichTextBox fragments text heavily as it is edited, and without merging
    // a re-saved string grows a tag pair per fragment.
    public static string Serialize(IEnumerable<ColorRun> runs)
    {
        var sb = new StringBuilder();
        bool haveOpen = false;
        byte cr = 0, cg = 0, cb = 0;

        void Close() { if (haveOpen) { sb.Append("</color>"); haveOpen = false; } }

        foreach (var run in runs)
        {
            if (string.IsNullOrEmpty(run.Text)) continue;
            if (run.HasColor)
            {
                if (haveOpen && (run.R != cr || run.G != cg || run.B != cb)) Close();
                if (!haveOpen)
                {
                    cr = run.R; cg = run.G; cb = run.B;
                    sb.Append("<color:").Append(cr).Append(',').Append(cg).Append(',').Append(cb).Append('>');
                    haveOpen = true;
                }
            }
            else Close();
            sb.Append(run.Text);
        }
        Close();
        return sb.ToString();
    }
}
