using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// Weapon elemental damage type. In live memory this is NOT an enum/index — it
// is a UClass* at item Address+0x5C (ItemNative.WeaponAdditionalDamage.DamageType)
// pointing at a DunDefDamageType_* class object. Verified by full memdump scan
// of 1058 forge items: the pointer for each element is identical across every
// weapon class (Apprentice/Squire/Initiate/Recruit) and familiars within one
// game session, but is engine-heap allocated so it CHANGES every game launch.
// 0x00000000 = no elemental. Special items can carry other DunDefDamageType_*
// classes (e.g. Lightning_FullMomentum) — non-canonical, preserved untouched.
internal enum ElementalType
{
	Generic,
	Fire,
	Poison,
	Lightning
}

// Element <-> session UClass* map. Fully automatic, no user calibration:
//  - A one-time background GObjects scan (per game session) finds all four
//    DunDefDamageType_* class objects by name, so every element is settable
//    immediately without the user opening anything.
//  - Opening any item also auto-learns its element (instant fallback if the
//    background scan hasn't finished or GObjects couldn't be located).
// Keyed to the live process handle; self-clears on re-attach / game restart
// so a stale pointer is never written. Thread-safe (background + UI access).
internal static class ElementalRegistry
{
	// Exact engine class names ↔ our four canonical elements. Exact match by
	// design: "DunDefDamageType_Lightning_FullMomentum" is NOT Lightning — it
	// is a distinct special class and must be preserved, never coerced.
	private static readonly Dictionary<string, ElementalType> ByName = new()
	{
		["DunDefDamageType_Generic"]   = ElementalType.Generic,
		["DunDefDamageType_Fire"]      = ElementalType.Fire,
		["DunDefDamageType_Poison"]    = ElementalType.Poison,
		["DunDefDamageType_Lightning"] = ElementalType.Lightning,
	};

	private static readonly object _lock = new();
	private static readonly Dictionary<ElementalType, int> _map = new();
	private static IntPtr _session = (IntPtr)(-1);
	private static IntPtr _primedSession = (IntPtr)(-1);

	// Clear the map when the process handle changes (re-attach / restart).
	// Caller must hold _lock.
	private static void EnsureSessionLocked()
	{
		IntPtr h = Base.Instance.Handle;
		if (h != _session) { _map.Clear(); _session = h; }
	}

	// Kick off the one-time GObjects scan for this session on a background
	// thread (idempotent per session). The UI never waits on this — per-item
	// Observe() covers anything opened before it finishes.
	public static void EnsurePrimedInBackground()
	{
		IntPtr h = Base.Instance.Handle;
		lock (_lock)
		{
			if (_primedSession == h) return;   // already primed/priming this session
			_primedSession = h;
		}
		Task.Run(() =>
		{
			try
			{
				var found = new Dictionary<ElementalType, int>();
				UE3Names.ScanGObjects((ptr, name) =>
				{
					if (name != null && ByName.TryGetValue(name, out var t) && !found.ContainsKey(t))
					{
						found[t] = ptr;
						lock (_lock)
						{
							EnsureSessionLocked();
							if (Base.Instance.Handle == h) _map[t] = ptr;
						}
					}
					return found.Count < ByName.Count;   // stop once all four seen
				});
			}
			catch { /* scan raced a teardown — Observe() still covers reads */ }
		});
	}

	// Resolve a class pointer's name; if canonical, remember it for the
	// session. Call whenever an item's elemental class pointer is read.
	// Returns the raw class name (e.g. "DunDefDamageType_Fire" or a special
	// class), or null if unresolved / pointer is 0.
	public static string? Observe(int classPtr)
	{
		if (classPtr == 0) return null;
		string? name = UE3Names.ResolveName(classPtr);
		if (name != null && ByName.TryGetValue(name, out var t))
			lock (_lock) { EnsureSessionLocked(); _map[t] = classPtr; }
		return name;
	}

	// Map an already-resolved class name to a canonical element (no re-read).
	public static bool TryGetTypeByName(string? name, out ElementalType t)
	{
		t = default;
		return name != null && ByName.TryGetValue(name, out t);
	}

	public static bool TryGetPointer(ElementalType t, out int classPtr)
	{
		lock (_lock)
		{
			EnsureSessionLocked();
			return _map.TryGetValue(t, out classPtr) && classPtr != 0;
		}
	}

	public static string LearnedSummary()
	{
		lock (_lock)
		{
			EnsureSessionLocked();
			return _map.Count == 0 ? "none yet" : string.Join(", ", _map.Keys.OrderBy(k => k.ToString()));
		}
	}
}
