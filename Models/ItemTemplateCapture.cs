namespace Modinator;

// Explicit capture only. The caller holds MainWindow's scan gate on a worker.
// Both Item Dupe and Item Edit use the same portable snapshot rules.
internal static class ItemTemplateCapture
{
    public static ItemTemplate Capture(int address, ItemIdentity identity, DupeSession session,
        CancellationToken cancel, Action? verifySource = null)
    {
        ItemNative Read()
        {
            cancel.ThrowIfCancellationRequested();
            session.Check();
            verifySource?.Invoke();
            var native = DupeMemory.ReadItem(address);
            if (!identity.Matches(native)) throw new InvalidOperationException(ItemIdentity.ChangedMessage);
            session.Check();
            cancel.ThrowIfCancellationRequested();
            return native;
        }

        var source = Read();
        string displayName = Clean(DupeMemory.ItemName(address));
        if (displayName.Length == 0) displayName = "Unnamed item";
        var entry = new ItemTemplate
        {
            Name = displayName.Length > 120 ? displayName[..120] : displayName,
            ItemName = displayName,
            Values = ItemDupeValues.Capture(source),
            EquipmentName = Clean(Base.ReadUni<ItemNative>(address, "EquipmentName")),
            Description = Clean(Base.ReadUni<ItemNative>(address, "Description")),
            ForgerName = Clean(Base.ReadUni<ItemNative>(address, "ForgerName"))
        };
        var resolver = new DupeReferenceResolver(cancel);
        int[] pointers = [source.EquipmentTemplate, source.DamageReductions[0].DamageType,
            source.DamageReductions[1].DamageType, source.DamageReductions[2].DamageType,
            source.DamageReductions[3].DamageType, source.WeaponAdditionalDamage.DamageType];
        entry.References = pointers.Select(resolver.Key).ToArray();
        Read(); // reject a detach, sold item or remote equipment change during resolution
        entry.Validate();
        return entry;
    }

    // An item's text buffer can hold control characters or embedded nulls,
    // which template validation rejects. They are dropped here so the
    // capture still succeeds; line breaks and tabs are kept.
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (c >= ' ' || c == '\n' || c == '\r' || c == '\t') sb.Append(c);
        return sb.ToString();
    }
}
