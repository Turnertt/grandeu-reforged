using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

// Resolves a UE3 UObject*/UClass* to its name by reading DunDefGame's own
// GNames table, and enumerates GObjects — exactly what the DD_NewSDK DLL does.
// This is what lets the elemental dropdown name a DunDefDamageType_* class
// pointer (and discover all four up-front) with ZERO user calibration.
//
// All offsets/patterns are from the DLL's own working code (DD_Basic.cpp/.hpp,
// DD_Core_classes.hpp) and verified live against DunDefGame:
//   GNames   = *(u32*)( AOB("8B 0D ?? ?? ?? ?? 83 3C 81 00 74") +2 )
//              TArray<FNameEntry*> : Data@+0x0, Num@+0x4
//   GObjects = *(u32*)( AOB("8B ?? ?? ?? ?? ?? 8B 04 ?? 8B 40 ?? 25 ?? 02 ?? ??") +2 )
//              TArray<UObject*>    : Data@+0x0, Num@+0x4
//   obj.Name = FName@+0x2C  -> Index i32@+0x2C, Number i32@+0x30
//   FNameEntry = *(u32*)(GNames.Data + Index*4); Flags@+0x4;
//   string = (Flags&0x4000) ? *(u32*)(entry+0x10) : entry+0x10   (ANSI, NUL-term)
//
// Both AOBs are version-fragile (a code-moving Steam patch breaks them). Every
// path degrades to null/no-op on any failure — reads only, never throws into
// the UI; a blank name is the worst case, never a crash or a bad write.
internal static class UE3Names
{
	private static readonly byte[] GnPat = { 0x8B, 0x0D, 0, 0, 0, 0, 0x83, 0x3C, 0x81, 0x00, 0x74 };
	private static readonly bool[] GnFix = { true, true, false, false, false, false, true, true, true, true, true };

	private static readonly byte[] GoPat = { 0x8B, 0, 0, 0, 0, 0, 0x8B, 0x04, 0, 0x8B, 0x40, 0, 0x25, 0, 0x02, 0, 0 };
	private static readonly bool[] GoFix = { true, false, false, false, false, false, true, true, false, true, true, false, true, false, true, false, false };

	private static IntPtr _session = (IntPtr)(-1);
	private static bool _ok;
	private static long _namesData;
	private static int _namesNum;
	private static long _objData;
	private static int _objNum;

	private static uint RU32(long addr)
		=> BitConverter.ToUInt32(Base.Instance.ReadMemory(unchecked((int)addr), 4), 0);

	private static void EnsureSession()
	{
		IntPtr h = Base.Instance.Handle;
		if (h == _session) return;
		_session = h;
		_ok = false; _namesData = _objData = 0; _namesNum = _objNum = 0;
		try
		{
			Process? p = Process.GetProcessesByName("DunDefGame").FirstOrDefault(x => !x.HasExited);
			if (p?.MainModule == null) return;
			long baseAddr = p.MainModule.BaseAddress.ToInt64();
			int size = p.MainModule.ModuleMemorySize;

			long gn = AobScan(baseAddr, size, GnPat, GnFix);
			if (gn == 0) return;
			long gnAddr = RU32(gn + 2);
			_namesData = RU32(gnAddr);
			_namesNum = unchecked((int)RU32(gnAddr + 4));
			if (_namesData == 0 || _namesNum <= 0 || _namesNum > 5_000_000) return;

			long go = AobScan(baseAddr, size, GoPat, GoFix);
			if (go != 0)
			{
				long goAddr = RU32(go + 2);
				_objData = RU32(goAddr);
				_objNum = unchecked((int)RU32(goAddr + 4));
				if (_objData == 0 || _objNum <= 0 || _objNum > 20_000_000) { _objData = 0; _objNum = 0; }
			}
			_ok = true; // GNames is enough for name resolution; GObjects optional
		}
		catch { /* read raced / module gone / patched — stay not-ok */ }
	}

	private static long AobScan(long baseAddr, int size, byte[] pat, bool[] fix)
	{
		const int chunk = 0x10000;
		int tail = pat.Length - 1;
		for (int off = 0; off < size; off += chunk)
		{
			int len = Math.Min(chunk + tail, size - off);
			byte[] buf;
			try { buf = Base.Instance.ReadMemory(unchecked((int)(baseAddr + off)), len); }
			catch { continue; }
			for (int i = 0; i <= buf.Length - pat.Length; i++)
			{
				bool hit = true;
				for (int j = 0; j < pat.Length; j++)
					if (fix[j] && buf[i + j] != pat[j]) { hit = false; break; }
				if (hit) return baseAddr + off + i;
			}
		}
		return 0;
	}

	// GNames index/number -> string (no object read).
	private static string? ResolveByIndex(int index, int number)
	{
		if (index < 0 || index >= _namesNum) return null;
		long entry = RU32(_namesData + (long)index * 4);
		if (entry == 0) return null;
		uint flags = RU32(entry + 4);
		long strAddr = (flags & 0x4000) != 0 ? RU32(entry + 0x10) : entry + 0x10;
		byte[] raw = Base.Instance.ReadMemory(unchecked((int)strAddr), 128);
		int z = Array.IndexOf(raw, (byte)0);
		if (z < 0) z = raw.Length;
		if (z == 0) return null;
		string s = System.Text.Encoding.ASCII.GetString(raw, 0, z);
		return number > 0 ? s + "_" + number : s;
	}

	// UObject* -> name, or null on any failure / null pointer.
	public static string? ResolveName(int objPtr)
	{
		if (objPtr == 0) return null;
		EnsureSession();
		if (!_ok) return null;
		try
		{
			long op = (uint)objPtr;
			return ResolveByIndex(unchecked((int)RU32(op + 0x2C)), unchecked((int)RU32(op + 0x30)));
		}
		catch { return null; }
	}

	// Iterate every live GObject, calling visit(objPtr, name). Return false
	// from visit to stop early. Caches index→name so duplicate names aren't
	// re-resolved. Heavy (hundreds of k objects) — call from a background
	// thread. No-op if GObjects could not be located.
	public static void ScanGObjects(Func<int, string?, bool> visit)
	{
		EnsureSession();
		if (!_ok || _objData == 0 || _objNum <= 0) return;
		byte[] arr;
		try { arr = Base.Instance.ReadMemory(unchecked((int)_objData), _objNum * 4); }
		catch { return; }
		var cache = new Dictionary<long, string?>();
		for (int i = 0; i < _objNum; i++)
		{
			uint obj = BitConverter.ToUInt32(arr, i * 4);
			if (obj == 0) continue;
			string? name;
			try
			{
				long op = obj;
				int idx = unchecked((int)RU32(op + 0x2C));
				int num = unchecked((int)RU32(op + 0x30));
				long key = ((long)idx << 32) | (uint)num;
				if (!cache.TryGetValue(key, out name))
				{
					name = ResolveByIndex(idx, num);
					cache[key] = name;
				}
			}
			catch { continue; }
			if (!visit(unchecked((int)obj), name)) return;
		}
	}
}
