namespace Modinator;

// Display-only. The Quality2 enum's underlying byte (what gets written to
// ItemNative.NameIndex_QualityDescriptor) is unchanged — C# identifiers
// just can't contain '+', so the UltimatePlus / UltimatePlusPlus members
// are rendered with the in-game "+" / "++" labels for the UI.
internal static class QualityDisplay
{
    public static string Name(Quality2 q) => q switch
    {
        Quality2.UltimatePlusPlus => "Ultimate++",
        Quality2.UltimatePlus     => "Ultimate+",
        _ => q.ToString(),
    };

    // Power-order rank (Cursed lowest → Ultimate++ highest) for sorting and
    // "X & up" filters. NOT the stored byte: the low tiers' enum values are
    // reversed vs power order (Godly stores byte 0 but outranks Legendary).
    // Single home — previously two parallel switches (ForgeViewerView +
    // CloneSourcePickerDialog) had to be grown in lockstep per new tier.
    public static int Rank(Quality2 q) => q switch
    {
        Quality2.UltimatePlusPlus => 19, Quality2.UltimatePlus => 18, Quality2.Ultimate93 => 17,
        Quality2.Ultimate => 16, Quality2.Supreme => 15, Quality2.Transcendent => 14,
        Quality2.Mythical => 13, Quality2.Godly => 12, Quality2.Legendary => 11,
        Quality2.Epic => 10, Quality2.Amazing => 9, Quality2.Powerful => 8,
        Quality2.Shining => 7, Quality2.Polished => 6, Quality2.Sturdy => 5,
        Quality2.Solid => 4, Quality2.Stocky => 3, Quality2.Worn => 2,
        Quality2.Torn => 1, Quality2.Cursed => 0, _ => -1,
    };
}

// ComboBox item wrapper: the dropdown shows Name(Value) while
// SelectedItem still round-trips the real Quality2 value (and its byte).
internal sealed class Quality2Choice
{
    public Quality2 Value { get; }
    public Quality2Choice(Quality2 v) => Value = v;
    public override string ToString() => QualityDisplay.Name(Value);
}
