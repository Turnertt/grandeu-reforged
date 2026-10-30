namespace Modinator;

internal sealed record MultiplayerItem(int Address, int Pri, int Hero, ItemIdentity Identity,
    DupeSession Session, string Player, string HeroName, string Name, string Type, string Quality, int Level)
{
    public FloorSource? Floor { get; init; }
    // DoFadeOut set on the pickup actor: still on the floor and still a
    // valid source (2026-09-27), just shown so the user knows.
    public bool Fading { get; init; }
    public string SourceDescription => Floor != null ? (Fading ? "Floor item · current lobby · fading" : "Floor item · current lobby") : Player + " · " + HeroName;
    public string Key => $"{Floor?.Actor ?? 0:X8}:{Floor?.World ?? 0:X8}:{Pri:X8}:{Hero:X8}:{Address:X8}:{Identity.Id1}:{Identity.Id2}:{Identity.Template}";
}

// Note: a human-readable breakdown of what a floor scan skipped ("2 fading ·
// 1 picked up or removed"), empty when nothing was.
internal sealed record MultiplayerScan(List<MultiplayerItem> Items, int Players, int Unavailable, string Note = "");

internal static class MultiplayerEquipment
{
    // SDK/DD_Engine_classes.hpp: WorldInfo.GRI +3D0, GRI.PRIArray +264,
    // PlayerReplicationInfo.PlayerName +230. SDK/DD_UDKGame_classes.hpp:
    // DunDefPlayerReplicationInfo.myHero +358; hero fields via HeroFields.
    // SDK cross-checked; MP live validation pending. Each is read by name
    // (GameReflection) with these literals as the fallback when the reader
    // isn't trusted. Remote controllers are not replicated, so this never
    // walks one.
    private static int Off(int obj, string field, int fallback)
    {
        int o = GameReflection.FieldOffset(obj, field);
        return o > 0 ? o : fallback;
    }

    public static List<int> Players(int world)
    {
        if (!DupeMemory.IsObject(world)) return new();
        int gri = GameChain.RdInt(world + Off(world, "GRI", 0x3D0));
        if (!DupeMemory.IsObject(gri)) return new();
        return DupeMemory.ReadArray(gri + Off(gri, "PRIArray", 0x264), 128).Distinct().Where(DupeMemory.IsObject).ToList();
    }

    private static List<int> Equipment(int hero)
    {
        if (!DupeMemory.IsObject(hero)) throw new InvalidOperationException("Player equipment is not available yet.");
        // Same hero-content bounds as GameChain; read failure is unavailable.
        var f = HeroFields.Of(hero);
        int level = GameChain.RdInt(hero + f.Level), cap = GameChain.RdInt(hero + f.LevelCap);
        if (level < 0 || level > 1000 || cap < 1 || cap > 10000)
            throw new InvalidOperationException("Player hero layout could not be verified.");
        var items = DupeMemory.ReadArray(hero + f.Equipments, 128);
        int weapon = GameChain.RdInt(hero + f.Weapon);
        if (GameChain.IsGamePtr(weapon)) items.Add(weapon);
        return items.Distinct().ToList();
    }

    public static MultiplayerScan Read(int world, CancellationToken cancel)
    {
        var session = DupeSession.Current;
        session.Check();
        var players = Players(world);
        var result = new List<MultiplayerItem>();
        int missing = 0;
        foreach (int pri in players)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                int hero = GameChain.RdInt(pri + Off(pri, "myHero", 0x358));
                string playerName = Watermark.StripColorTags(DupeMemory.ReadString(pri + Off(pri, "PlayerName", 0x230)));
                string heroName = Watermark.StripColorTags(DupeMemory.ReadString(hero + HeroFields.Of(hero).Name));
                foreach (int equipment in Equipment(hero))
                {
                    try
                    {
                        int address = unchecked(equipment + 0x38);
                        var n = DupeMemory.ReadItem(address);
                        var id = ItemIdentity.Of(n);
                        string name = DupeMemory.ItemName(address);
                        if (!id.Matches(DupeMemory.ReadItem(address))) continue;
                        result.Add(new(address, pri, hero, id, session, playerName, heroName,
                            name, n.EquipmentType.ToString(), QualityDisplay.Name(n.NameIndex_QualityDescriptor), n.Level));
                    }
                    catch { missing++; }
                }
            }
            catch { missing++; }
        }
        session.Check();
        return new(result, players.Count, missing);
    }

    public static void Verify(MultiplayerItem item, int world)
    {
        if (item.Floor != null) { FloorEquipment.Verify(item, world); return; }
        item.Session.Check();
        if (!Players(world).Contains(item.Pri) || GameChain.RdInt(item.Pri + Off(item.Pri, "myHero", 0x358)) != item.Hero ||
            !Equipment(item.Hero).Contains(unchecked(item.Address - 0x38)) ||
            !item.Identity.Matches(DupeMemory.ReadItem(item.Address)))
            throw new InvalidOperationException("That player left, changed heroes, or unequipped the item. Pick their equipment again.");
    }
}
