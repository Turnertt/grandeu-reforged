namespace Modinator;

// UDunDefHero field positions by name (GameReflection), all-or-nothing,
// with the live-verified literals (2026-09-27, DD1_INTERNALS §8b) as the
// fallback when the reader isn't trusted this attach. Native is the
// HeroNative block base (HeroHealthModifier). Same tier as the HeroManager
// fields that moved in 2026-06 (+0xC); none of these has moved yet.
// GameChain.LooksLikeHero keeps its own literals: it is the content gate
// for hero-array discovery, runs on arbitrary candidate pointers, and is
// the path used when reflection is off anyway.
internal static class HeroFields
{
    internal sealed record Layout(int ClassDisplayName, int ClassNameColor, int Native, int Level, int LevelCap,
        int Name, int Equipments, int Weapon, int Template, bool ByName)
    {
        public string Describe() =>
            (ByName ? "by name" : "compiled fallback (reflection unavailable)") +
            $": native +0x{Native:X}, equipments +0x{Equipments:X}, weapon +0x{Weapon:X}";
    }

    public static readonly Layout Fallback = new(0x424, 0x444, 0x504, 0x52C, 0x530, 0x564, 0x5B0, 0x5C8, 0x5D8, ByName: false);

    private static readonly object _gate = new();
    private static (int pid, long gen, string text) _logged;

    // GameReflection caches each lookup per class, so this is cheap per hero
    // after the first. A partly resolved layout would mix this build's
    // offsets with a possibly stale literal, so any miss is the fallback.
    public static Layout Of(int hero)
    {
        // No hero yet (a joining player's PRI): nothing to resolve, and
        // nothing to log — the fallback line would claim reflection is off.
        if (!GameChain.IsGamePtr(hero)) return Fallback;
        int Off(string name) => GameReflection.FieldOffset(hero, name);
        var l = new Layout(Off("HeroClassDisplayName"), Off("ClassNameColor"), Off("HeroHealthModifier"),
            Off("HeroLevel"), Off("HeroLevelCap"), Off("HeroName"), Off("HeroEquipments"),
            Off("HeroWeaponEquipment"), Off("BasedOnHeroTemplate"), ByName: true);
        if (l.ClassDisplayName <= 0 || l.ClassNameColor <= 0 || l.Native <= 0 || l.Level <= 0 || l.LevelCap <= 0 ||
            l.Name <= 0 || l.Equipments <= 0 || l.Weapon <= 0 || l.Template <= 0)
            l = Fallback;
        Note(l);
        return l;
    }

    // One "Hero layout:" log line per attach, and again only if it changes.
    private static void Note(Layout l)
    {
        var now = (Base.AttachedPid, Base.Instance.AttachmentGeneration, l.Describe());
        lock (_gate)
        {
            if (now == _logged) return;
            _logged = now;
        }
        Base.LogEvent("Hero layout: " + now.Item3);
    }
}
