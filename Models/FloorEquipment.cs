namespace Modinator;

// A live source token, never persisted into an ItemTemplate. The catalogue
// slot plus actor header distinguishes a recycled pickup from the one chosen.
internal sealed record FloorSource(int Actor, int World, int Class, int InternalIndex,
    int NameIndex, int NameNumber, int Outer, int CatalogueHeader, int CatalogueSlot);

internal static class FloorEquipment
{
    // Field positions come from the game's own property table, by name
    // (GameReflection), so a patch that inserts a field cannot silently
    // hide every pickup. The 2026-10-23 DD1 build grew DunDefDroppedEquipment
    // by 0xC: the flag dword moved 0x308 → 0x314 and MyEquipmentObject
    // 0x318 → 0x324. The old literals then read a float whose bits looked
    // like bTransferredToItemBox, and every pickup was listed as "removed".
    // The literals below (that build, live-verified by name 2026-10-29) are
    // only the fallback when the reader isn't trusted this attach.
    //
    // DoFadeOut is NOT a removal (2026-09-27): live in the Tavern, actors
    // with it set stayed for 12+ s, were neither in the item box nor worn,
    // kept a live equipment object and sat on the floor beside the others.
    // Treating it as "removed" hid 3 of 13 floor items. It is shown as a
    // "fading" tag instead; bTransferredToItemBox / bAutoDestroyed /
    // bDeleteMe / bHidden still reject, and copy-time Verify re-checks all
    // of them against the live actor.
    private readonly record struct Flag(int Offset, uint Mask);

    private sealed record Layout(int World, int Equipment, Flag Hidden, Flag Deleted,
        Flag FadeOut, Flag Transferred, Flag AutoDestroyed, bool ByName)
    {
        public string Describe() =>
            (ByName ? "by name" : "compiled fallback (reflection unavailable)") +
            $": equipment +0x{Equipment:X}, flags +0x{Transferred.Offset:X}";
    }

    private static readonly Layout Fallback = new(0x110, 0x324, new(0xB0, 0x4), new(0xB0, 0x10),
        new(0x314, 0x08), new(0x314, 0x10), new(0x314, 0x20), ByName: false);

    // All-or-nothing: a partly resolved layout would mix this build's
    // offsets with a possibly stale literal. GameReflection caches each
    // lookup per class, so this is cheap per actor.
    private static Layout LayoutOf(int actor)
    {
        int world = GameReflection.FieldOffset(actor, "WorldInfo");
        int equipment = GameReflection.FieldOffset(actor, "MyEquipmentObject");
        Flag Bool(string name) { var (offset, mask) = GameReflection.BoolField(actor, name); return new(offset, mask); }
        var flags = new[] { Bool("bHidden"), Bool("bDeleteMe"), Bool("DoFadeOut"), Bool("bTransferredToItemBox"), Bool("bAutoDestroyed") };
        if (world <= 0 || equipment <= 0 || flags.Any(f => f.Offset <= 0)) return Fallback;
        return new(world, equipment, flags[0], flags[1], flags[2], flags[3], flags[4], ByName: true);
    }

    private static bool IsSet(int actor, Flag flag) => (Int(actor + flag.Offset) & flag.Mask) != 0;

    private static bool IsRemoved(int actor, Layout l) =>
        IsSet(actor, l.Transferred) || IsSet(actor, l.AutoDestroyed) || IsSet(actor, l.Hidden) || IsSet(actor, l.Deleted);

    public static bool IsFading(FloorSource source)
    {
        try { return IsSet(source.Actor, LayoutOf(source.Actor).FadeOut); } catch { return false; }
    }

    private static int Int(int address) => BitConverter.ToInt32(Base.Instance.ReadMemory(address, 4));

    private static void CheckActor(FloorSource source, int world)
    {
        if (world == 0 || source.World != world || !DupeMemory.IsObject(world) || !DupeMemory.IsObject(source.Actor))
            throw new InvalidOperationException("The lobby or floor item changed. Refresh floor items.");
        byte[] header = Base.Instance.ReadMemory(source.Actor, 0x38);
        var layout = LayoutOf(source.Actor);
        if (BitConverter.ToInt32(header, 0x34) != source.Class ||
            BitConverter.ToInt32(header, 4) != source.InternalIndex ||
            BitConverter.ToInt32(header, 0x2C) != source.NameIndex ||
            BitConverter.ToInt32(header, 0x30) != source.NameNumber ||
            BitConverter.ToInt32(header, 0x28) != source.Outer ||
            Int(source.Actor + layout.World) != world || IsRemoved(source.Actor, layout))
            throw new InvalidOperationException("That floor item was picked up, removed or replaced. Refresh floor items.");
        byte[] catalogue = Base.Instance.ReadMemory(source.CatalogueHeader, 12);
        int data = BitConverter.ToInt32(catalogue, 0), count = BitConverter.ToInt32(catalogue, 4), max = BitConverter.ToInt32(catalogue, 8);
        if (!GameChain.IsGamePtr(data) || count < 0 || count > 2000000 || max < count || max > 2000000 ||
            source.CatalogueSlot < 0 || source.CatalogueSlot >= count ||
            Int(unchecked(data + source.CatalogueSlot * 4)) != source.Actor)
            throw new InvalidOperationException("That floor item is no longer in the current game. Refresh floor items.");
    }

    public static void Verify(MultiplayerItem item, int world)
    {
        item.Session.Check();
        var source = item.Floor ?? throw new InvalidOperationException("No floor source was captured.");
        CheckActor(source, world);
        if (Int(source.Actor + LayoutOf(source.Actor).Equipment) != unchecked(item.Address - 0x38) ||
            !item.Identity.Matches(DupeMemory.ReadItem(item.Address)))
            throw new InvalidOperationException("That floor item's equipment changed. Refresh floor items.");
        item.Session.Check();
    }

    public static MultiplayerScan Read(int world, CancellationToken cancel,
        IReadOnlyList<MultiplayerItem>? known = null, IProgress<ScanProgress>? progress = null)
    {
        var session = DupeSession.Current;
        session.Check();
        if (!DupeMemory.IsObject(world)) throw new InvalidOperationException("Join a lobby or map, then refresh floor items.");
        var items = new List<MultiplayerItem>();
        var seen = new HashSet<int>();
        int missing = 0;
        string note = "";
        // Polling only checks already discovered sources. New drops are found
        // on selecting Floor items or explicitly pressing Refresh.
        if (known != null)
        {
            foreach (var item in known)
            {
                cancel.ThrowIfCancellationRequested();
                try { Verify(item, world); if (seen.Add(item.Address)) items.Add(item); }
                catch (OperationCanceledException) { throw; }
                catch { missing++; }
            }
        }
        else
        {
            // One fast pass over the game's object list (GameReflection —
            // ~0.3 s live, cached globals); the previous walk took ~15 s
            // because it slept 1 ms after every 256 objects. Progress is
            // reported as a fraction so the picker can draw a real bar.
            int catalogue = GameReflection.ObjectsHeader;
            var walk = new DelegateProgress<(int done, int total)>(p =>
                progress?.Report(new ScanProgress($"Finding floor items… {p.done:N0} / {p.total:N0}",
                    p.total > 0 ? (double)p.done / p.total : 0)));
            int fading = 0, removed = 0, noEquipment = 0, live = 0;
            string layoutNote = "no pickups";
            GameReflection.ForEachInstance("DunDefDroppedEquipment", (actor, actorClass, slot) =>
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var layout = LayoutOf(actor);
                    layoutNote = layout.Describe();
                    // Archetypes and other-world leftovers are normal catalogue
                    // entries: no world backref, or a different one. Not counted.
                    if (Int(actor + layout.World) != world) return;
                    live++;
                    if (IsRemoved(actor, layout)) { removed++; return; }
                    int equipmentObject = Int(actor + layout.Equipment);
                    if (!GameChain.IsGamePtr(equipmentObject)) { noEquipment++; return; }

                    byte[] h = Base.Instance.ReadMemory(actor, 0x38);
                    var source = new FloorSource(actor, world, actorClass, BitConverter.ToInt32(h, 4),
                        BitConverter.ToInt32(h, 0x2C), BitConverter.ToInt32(h, 0x30), BitConverter.ToInt32(h, 0x28), catalogue, slot);
                    CheckActor(source, world);
                    int address = unchecked(equipmentObject + 0x38);
                    var native = DupeMemory.ReadItem(address);
                    bool isFading = IsSet(actor, layout.FadeOut);
                    var item = new MultiplayerItem(address, 0, 0, ItemIdentity.Of(native), session,
                        "Floor", "Current lobby", DupeMemory.ItemName(address), native.EquipmentType.ToString(),
                        QualityDisplay.Name(native.NameIndex_QualityDescriptor), native.Level) { Floor = source, Fading = isFading };
                    Verify(item, world);
                    if (seen.Add(address)) { items.Add(item); if (isFading) fading++; }
                }
                catch (OperationCanceledException) { throw; }
                catch { missing++; }
            }, walk, cancel);
            // The status line and the session log both get the breakdown:
            // "no equipment data" on every live actor is the client-side
            // signature to look for in a remote report.
            note = Describe(items.Count, fading, removed, noEquipment, missing);
            Base.LogEvent($"Floor: {live} live pickups -> {items.Count} listed ({fading} fading), {removed} removed, " +
                          $"{noEquipment} without equipment data, {missing} unreadable; layout {layoutNote}");
        }
        cancel.ThrowIfCancellationRequested();
        session.Check();
        return new(items, 0, missing, note);
    }

    private static string Describe(int listed, int fading, int removed, int noEquipment, int unreadable)
    {
        var parts = new List<string>();
        if (fading > 0) parts.Add($"{fading} fading");
        if (removed > 0) parts.Add($"{removed} picked up or removed");
        if (noEquipment > 0) parts.Add($"{noEquipment} without equipment data");
        if (unreadable > 0) parts.Add($"{unreadable} unreadable");
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }
}

// Progress of a floor scan: a status line plus a 0..1 fraction for the bar.
internal readonly record struct ScanProgress(string Message, double Fraction);

// IProgress<T> that just invokes a delegate on the reporting thread (no
// SynchronizationContext hop — used to adapt one progress stream to another).
internal sealed class DelegateProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public DelegateProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}
