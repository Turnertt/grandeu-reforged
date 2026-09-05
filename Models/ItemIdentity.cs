namespace Modinator;

// Identity of the item that lived at an address when it was scanned or
// opened, so a later WRITE can confirm the same item is still there.
//
// Why: the Forge cache / snapshot and an open editor hold raw addresses.
// UHeroEquipment objects don't move, but they are freed when the item is
// sold, dropped, transferred or the box is re-serialized — and the freed
// memory is pooled and reused by the next UObject. A "read before write"
// against a reused address succeeds and then writes an item layout over
// whatever lives there now. Comparing identity first turns that into a
// refused write with a "refresh" message.
//
// Identity = EquipmentID1/2 when the cached item had either set (real
// instances; a dupe preserves both, an edit that changes them re-captures
// on Refresh), else the archetype pointer (template/shop items, which
// carry no IDs). Deliberately NOT the address-relative UObject vtable:
// editor addresses can also come from raw memory-scan hits.
internal readonly struct ItemIdentity
{
    public readonly int Template;
    public readonly int Id1;
    public readonly int Id2;

    public ItemIdentity(int template, int id1, int id2)
    {
        Template = template; Id1 = id1; Id2 = id2;
    }

    public static ItemIdentity Of(in ItemNative n)
        => new(n.EquipmentTemplate, n.EquipmentID1, n.EquipmentID2);

    public bool HasIds => Id1 != 0 || Id2 != 0;

    public bool Matches(in ItemNative live)
    {
        if (HasIds) return live.EquipmentID1 == Id1 && live.EquipmentID2 == Id2;
        return live.EquipmentTemplate == Template;
    }

    // User-facing reason a write was refused. One home so the editor, the
    // bulk dialog and the dupe tab say the same thing.
    public const string ChangedMessage =
        "This item changed or moved in memory since it was loaded (sold, dropped, " +
        "or the game reloaded it). Refresh / rescan to see what is there now before editing.";
}
