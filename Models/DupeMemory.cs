using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Modinator;

internal readonly record struct DupeSession(int Pid, long Generation)
{
    public static DupeSession Current => new(Base.AttachedPid, Base.Instance.AttachmentGeneration);
    public void Check()
    {
        if (Pid == 0 || this != Current) throw new InvalidOperationException("The game connection changed. Pick the items again.");
    }
}

// What the Item Dupe source is, for ranking targets. Class alone is too
// coarse: one class (HeroEquipment) covers every weapon, every armor piece
// and most pets, so "same class" alone would recommend almost everything.
// WeaponType is null when unknown (template sources).
internal sealed record DupeSourceShape(string ClassPath, string ArchetypePath, int EquipmentType, int? WeaponType)
{
    // 2 = same base item, 1 = same kind, 0 = neither. `item` is the ItemNative address.
    public int Match(int item)
    {
        try
        {
            int obj = unchecked(item - 0x38);
            if (DupeMemory.ClassPath(obj) != ClassPath) return 0;
            int archetype = GameChain.RdInt(item);
            if (ArchetypePath.Length > 0 && GameChain.IsGamePtr(archetype) && GameReflection.ObjectPath(archetype) == ArchetypePath) return 2;
            if (Base.Instance.ReadMemory(obj + 0xDA, 1)[0] != EquipmentType) return 0;
            if (WeaponType is int weapon && EquipmentType == (int)global::EquipmentType.Weapon &&
                Base.Instance.ReadMemory(obj + DupeMemory.WeaponTypeObjectOffset, 1)[0] != weapon) return 0;
            return 1;
        }
        catch { return 0; }
    }
}

internal static class DupeMemory
{
    public const int WeaponTypeObjectOffset = 0x824; // UHeroEquipment.weaponType (EWeaponType)

    public static bool IsObject(int p)
    {
        if (!GameChain.IsGamePtr(p) || (p & 3) != 0) return false;
        uint vt = unchecked((uint)GameChain.RdInt(p));
        return vt >= 0x00400000 && vt <= 0x02000000 && GameChain.IsGamePtr(GameChain.RdInt(p + 0x34));
    }

    public static ItemNative ReadItem(int address)
    {
        if (!IsObject(unchecked(address - 0x38))) throw new InvalidDataException("Item object is no longer available.");
        var n = Base.Push<ItemNative>(Base.Instance.ReadMemory(address, Marshal.SizeOf<ItemNative>()));
        if (!IsObject(n.EquipmentTemplate) || !Enum.IsDefined(n.EquipmentType) || n.EquipmentType == EquipmentType.All ||
            n.Level < 0 || n.MaxEquipmentLevel < 0 || n.Level > 1000000 || n.MaxEquipmentLevel > 1000000)
            throw new InvalidDataException("Item layout could not be verified. Rescan before copying.");
        return n;
    }

    public static List<int> ReadArray(int address, int limit)
    {
        byte[] h = Base.Instance.ReadMemory(address, 12);
        int data = BitConverter.ToInt32(h, 0), count = BitConverter.ToInt32(h, 4), max = BitConverter.ToInt32(h, 8);
        if (count < 0 || count > limit || max < count || max > limit || (count > 0 && !GameChain.IsGamePtr(data)))
            throw new InvalidDataException("Equipment list is not available yet.");
        if (count == 0) return new();
        byte[] b = Base.Instance.ReadMemory(data, count * 4);
        var result = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int p = BitConverter.ToInt32(b, i * 4);
            if (GameChain.IsGamePtr(p)) result.Add(p);
        }
        return result;
    }

    public static string ReadString(int field)
    {
        byte[] h = Base.Instance.ReadMemory(field, 12);
        int p = BitConverter.ToInt32(h, 0), count = BitConverter.ToInt32(h, 4), max = BitConverter.ToInt32(h, 8);
        if (count == 0) return "";
        if (count < 0 || count > 16384 || max < count || max > 1048576 || !GameChain.IsGamePtr(p))
            throw new InvalidDataException("Text is not available yet.");
        return Encoding.Unicode.GetString(Base.Instance.ReadMemory(p, count * 2)).TrimEnd('\0');
    }

    // ── Item definition vs instance state (2026-09-27) ──────────────
    //
    // The game's own FEquipmentNetInfo carries behavior flags as part of the
    // item's DEFINITION (can be upgraded, allow renaming at max, disable
    // randomization, is secondary, UseWeaponCoreStats, the Weapon*BonusUse
    // bits, level-name/requirement rules); a copy must take those from the
    // source. The same +0xA8 word also holds INSTANCE state that belongs to
    // the target and must survive: locked, was attached, shop item, the two
    // online-verified bits, added to defender store. Masks come from the
    // game by name (UBoolProperty.BitMask); the constants are the
    // live-verified fallback for the current build.
    private static readonly (string name, uint mask)[] InstanceStateBits =
    [
        ("bIsLocked", 0x8000), ("bWasAttached", 0x10000), ("bIsShopEquipment", 0x20000),
        ("bIsNameOnlineVerified", 0x80000), ("bIsForgerNameOnlineVerified", 0x100000), ("bWasAddedToDefenderStore", 0x200000),
    ];

    // Bits of the +0xA8 word that stay with the target. `sample` is any live
    // UHeroEquipment object (used only to resolve the names).
    public static uint InstanceStateMask(int sample)
    {
        uint mask = 0;
        foreach (var (name, fallback) in InstanceStateBits)
        {
            var bf = GameReflection.BoolField(sample, name);
            mask |= bf.offset == 0xA8 ? bf.mask : fallback;
        }
        return mask;
    }

    public static int MergeFlags(int sourceFlags, int targetFlags, uint instanceMask)
        => unchecked((int)(((uint)sourceFlags & ~instanceMask) | ((uint)targetFlags & instanceMask)));

    // "UDKGame.HeroEquipment_Familiar_Melee" for a live UObject ("?" if unreadable).
    public static string ClassPath(int obj) => IsObject(obj) ? GameReflection.ObjectPath(GameChain.RdInt(obj + 0x34)) : "?";

    // The class of an item's archetype (ItemNative.EquipmentTemplate is the
    // first field at the item address). A copy makes the target claim this
    // archetype, so the target OBJECT must already be of this class: the
    // engine never changes an object's class, and familiar / crown / rune
    // subclasses carry fields beyond ItemNative the target would not have.
    public static string ArchetypeClassPath(int itemAddress) => ClassPath(GameChain.RdInt(itemAddress));

    // A template reference is "<class path> <object path>" (Key); first token.
    public static string TemplateClassPath(string reference)
    {
        int sp = reference?.IndexOf(' ') ?? -1;
        return sp > 0 ? reference![..sp] : "?";
    }

    public static string ClassLeaf(string classPath) => classPath[(classPath.LastIndexOf('.') + 1)..];

    public static string ItemName(int address)
    {
        string name = ItemNameOrEmpty(address);
        return name.Length == 0 ? "Unnamed item" : name;
    }

    // Same resolver as the Forge cards (custom → rolled base name →
    // archetype), color tags stripped, so every tab agrees on what an item
    // is called. "" when the item has no readable name (never throws) — for
    // callers with their own fallback.
    public static string ItemNameOrEmpty(int address)
    {
        try
        {
            string name = Watermark.StripColorTags(ItemNames.DisplayName(unchecked(address - 0x38)));
            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }
        catch { return ""; }
    }
}

// On-demand only; caller holds MainWindow's scan gate on a worker thread.
// SDK DD_Basic.cpp signatures + DD_Core_classes.hpp object header. No fixed
// addresses and no eager GObjects scan on view-open or a repeating refresh.
internal sealed class DupeReferenceResolver
{
    private readonly DupeSession _session = DupeSession.Current;
    private readonly CancellationToken _cancel;
    private readonly Dictionary<int, string> _names = new();
    private int _namesData, _namesCount, _objectsAddress;

    public DupeReferenceResolver(CancellationToken cancel)
    {
        _cancel = cancel;
        FindGlobals();
    }

    private void Check() { _cancel.ThrowIfCancellationRequested(); _session.Check(); }

    private void FindGlobals()
    {
        Check();
        // GameReflection already located GNames/GObjects for this attach on
        // the first Forge/Hero/floor scan; reuse them instead of sweeping the
        // 15 MB module again (which also slept 1 ms per 64 KB chunk).
        if (GameReflection.TryGetGlobals(out int namesData, out int namesCount, out int objectsHeader))
        {
            _namesData = namesData; _namesCount = namesCount; _objectsAddress = objectsHeader;
            return;
        }
        using var process = Process.GetProcessById(_session.Pid);
        var module = process.MainModule ?? throw new InvalidOperationException("Cannot read the game module.");
        int start = unchecked((int)module.BaseAddress.ToInt64());
        int size = module.ModuleMemorySize;
        if (size < 4096 || size > 128 * 1024 * 1024) throw new InvalidDataException("Unrecognized game module.");
        byte[] np = [0x8B, 0x0D, 0, 0, 0, 0, 0x83, 0x3C, 0x81, 0, 0x74];
        byte[] op = [0x8B, 0, 0, 0, 0, 0, 0x8B, 0x04, 0, 0x8B, 0x40, 0, 0x25, 0, 0x02, 0, 0];
        for (int offset = 0; offset < size && (_namesData == 0 || _objectsAddress == 0); offset += 65536)
        {
            Check();
            byte[] b;
            try { b = Base.Instance.ReadMemory(unchecked(start + offset), Math.Min(65536 + 16, size - offset)); }
            catch { continue; }
            for (int i = 0; i < b.Length - 16; i++)
            {
                if (_namesData == 0 && Matches(b, i, np, "xx????xxxxx"))
                {
                    int addr = BitConverter.ToInt32(b, i + 2);
                    if (TryHeader(addr, out int data, out int count)) { _namesData = data; _namesCount = count; }
                }
                if (_objectsAddress == 0 && Matches(b, i, op, "x?????xx?xx?xxxxx"))
                {
                    int addr = BitConverter.ToInt32(b, i + 2);
                    if (TryHeader(addr, out _, out _)) _objectsAddress = addr;
                }
            }
        }
        if (_namesData == 0) throw new InvalidOperationException("This game build's item names could not be resolved. No template was applied.");
    }

    private static bool Matches(byte[] b, int at, byte[] pattern, string mask)
    {
        for (int i = 0; i < mask.Length; i++) if (mask[i] == 'x' && b[at + i] != pattern[i]) return false;
        return true;
    }

    private static bool TryHeader(int address, out int data, out int count)
    {
        data = count = 0;
        try
        {
            var b = Base.Instance.ReadMemory(address, 12);
            data = BitConverter.ToInt32(b, 0); count = BitConverter.ToInt32(b, 4);
            int max = BitConverter.ToInt32(b, 8);
            return GameChain.IsGamePtr(data) && count > 0 && count <= 2000000 && max >= count && max <= 2000000;
        }
        catch { return false; }
    }

    private string Name(int index)
    {
        if (_names.TryGetValue(index, out string? cached)) return cached;
        if (index < 0 || index >= _namesCount) throw new InvalidDataException("Invalid object name.");
        int entry = GameChain.RdInt(unchecked(_namesData + index * 4));
        if (!GameChain.IsGamePtr(entry)) throw new InvalidDataException("Unreadable object name.");
        int flags = GameChain.RdInt(entry + 4);
        int str = (flags & 0x4000) == 0 ? entry + 0x10 : GameChain.RdInt(entry + 0x10);
        var bytes = new List<byte>();
        // Small chunks never cross an inaccessible page after an early NUL.
        for (int n = 0; n < 1024; n += 32)
        {
            byte[] part = Base.Instance.ReadMemory(unchecked(str + n), 32);
            foreach (byte c in part)
            {
                if (c == 0)
                {
                    if (bytes.Count == 0) throw new InvalidDataException("Empty object name.");
                    string name = Encoding.Latin1.GetString(bytes.ToArray());
                    _names[index] = name;
                    return name;
                }
                bytes.Add(c);
            }
        }
        throw new InvalidDataException("Object name is too long.");
    }

    private string ObjectName(byte[] h)
    {
        string name = Name(BitConverter.ToInt32(h, 0x2C));
        int number = BitConverter.ToInt32(h, 0x30);
        if (number < 0) throw new InvalidDataException("Invalid object name suffix.");
        return number == 0 ? name : name + "_" + number;
    }

    private string Path(int obj)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        while (obj != 0 && parts.Count < 32 && seen.Add(obj))
        {
            if (!DupeMemory.IsObject(obj)) throw new InvalidDataException("Item reference is no longer loaded.");
            byte[] h = Base.Instance.ReadMemory(obj, 0x38);
            parts.Add(ObjectName(h));
            obj = BitConverter.ToInt32(h, 0x28);
        }
        if (obj != 0) throw new InvalidDataException("Invalid item reference chain.");
        parts.Reverse();
        return string.Join('.', parts);
    }

    public string Key(int obj)
    {
        Check();
        if (obj == 0) return "";
        string path = Path(obj);
        string cls = Path(GameChain.RdInt(obj + 0x34));
        return cls + " " + path;
    }

    public int[] Resolve(string[] keys, IProgress<string>? progress, IEnumerable<int>? observed = null)
    {
        Check();
        var wanted = keys.Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        // Observe first: a matching target already carries most/all references.
        // Only an explicit copy action may request the slower catalogue walk.
        foreach (int obj in (observed ?? []).Where(p => p != 0).Distinct())
        {
            try { string key = Key(obj); if (wanted.Contains(key)) found[key] = obj; }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        int data = 0, count = 0;
        if (found.Count != wanted.Count && !TryHeader(_objectsAddress, out data, out count))
            throw new InvalidOperationException("This game build's item catalogue could not be resolved.");
        var leaves = wanted.Select(k => k[(k.LastIndexOf('.') + 1)..]).ToHashSet(StringComparer.Ordinal);
        // 4096-pointer chunks and no per-chunk sleep (2026-09-27): the old
        // 256-chunk loop with Thread.Sleep(1) took ~15 s over ~250k objects.
        for (int offset = 0; offset < count && found.Count < wanted.Count; offset += 4096)
        {
            Check();
            int take = Math.Min(4096, count - offset);
            byte[] pointers = Base.Instance.ReadMemory(unchecked(data + offset * 4), take * 4);
            for (int i = 0; i < take; i++)
            {
                try
                {
                    int obj = BitConverter.ToInt32(pointers, i * 4);
                    if (!GameChain.IsGamePtr(obj)) continue;
                    byte[] h = Base.Instance.ReadMemory(obj, 0x38);
                    if (!leaves.Contains(ObjectName(h))) continue;
                    string key = Key(obj);
                    if (wanted.Contains(key)) found[key] = obj;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* collected objects and unloading packages are normal */ }
            }
            progress?.Report($"Finding template references… {offset:N0} / {count:N0}");
        }
        Check();
        if (found.Count != wanted.Count)
            throw new InvalidOperationException("The template's item type or damage type is not loaded in this game. " +
                "Load an item of that type in the current game and retry. Nothing was written.");
        var result = keys.Select(k => k.Length == 0 ? 0 : found[k]).ToArray();
        for (int i = 0; i < keys.Length; i++)
            if (Key(result[i]) != keys[i]) throw new InvalidOperationException("Item references changed. Retry the copy.");
        // A damaged/hand-edited template must not turn an arbitrary UObject
        // into an equipment archetype, or a non-UClass into a damage type.
        DupeMemory.ReadItem(unchecked(result[0] + 0x38));
        for (int i = 1; i < keys.Length; i++)
            if (result[i] != 0 && !keys[i].StartsWith("Core.Class ", StringComparison.Ordinal))
                throw new InvalidDataException("A template damage reference is not a class.");
        return result;
    }
}
