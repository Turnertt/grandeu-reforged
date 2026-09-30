namespace Modinator;

// Item color overrides vs. the item's default colors.
//
// An item carries two color set tables (TArray<FLinearColor>, 16 bytes per
// entry) and two selected indices; while BOTH overrides are 0,0,0 the game
// draws it with the selected entries. As soon as EITHER override is set
// the game uses the overrides for both colors, so an override left at 0,0,0
// then really is black.
internal static class ItemColors
{
    public static bool IsSet(LinearColorNative c) => c.R != 0f || c.G != 0f || c.B != 0f;

    // The selected color set entry (alpha 1). Read-only; false when the item
    // has no readable table or the index is outside it.
    public static bool TryDefault(in ItemNative n, bool primary, out LinearColorNative entry)
    {
        entry = default;
        try
        {
            var table = primary ? n.PrimaryColorSets : n.SecondaryColorSets;
            int index = primary ? n.PrimaryColorSet : n.SecondaryColorSet;
            if (!GameChain.IsGamePtr(table.Address) || table.CurrentLength is <= 0 or > 256 || index >= table.CurrentLength)
                return false;
            byte[] b = Base.Instance.ReadMemory(table.Address + index * 16, 16);
            entry = new LinearColorNative
            {
                R = BitConverter.ToSingle(b, 0),
                G = BitConverter.ToSingle(b, 4),
                B = BitConverter.ToSingle(b, 8),
                A = 1f
            };
            return float.IsFinite(entry.R) && float.IsFinite(entry.G) && float.IsFinite(entry.B);
        }
        catch { return false; }
    }

    // The color the editors show: the default entry while neither override
    // is set, otherwise the override as stored (black included — that is
    // what the game draws once the other color is overridden). The editors
    // only write a color whose boxes were changed from this seed.
    public static LinearColor Shown(in ItemNative n, bool primary)
    {
        var over = primary ? n.PrimaryColorOverride : n.SecondaryColorOverride;
        if (!IsSet(n.PrimaryColorOverride) && !IsSet(n.SecondaryColorOverride) && TryDefault(n, primary, out var entry))
            return Base.LinearColorToUser(entry);
        return Base.LinearColorToUser(over);
    }

    // Call after putting an edited override into `n`. When exactly one color
    // was edited on an item that had no overrides, the other one is given
    // its default entry as an explicit override, so it keeps its look
    // instead of turning black. An item that already had an override, or an
    // edit of both colors, is left exactly as it was set.
    public static void KeepOtherDefault(ref ItemNative n, in ItemNative before, bool primaryEdited, bool secondaryEdited)
    {
        if (primaryEdited == secondaryEdited) return;
        if (IsSet(before.PrimaryColorOverride) || IsSet(before.SecondaryColorOverride)) return;
        if (primaryEdited)
        {
            if (TryDefault(before, primary: false, out var entry)) n.SecondaryColorOverride = entry;
        }
        else if (TryDefault(before, primary: true, out var entry))
            n.PrimaryColorOverride = entry;
    }
}
