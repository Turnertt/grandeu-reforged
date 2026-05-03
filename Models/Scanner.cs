using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

internal class Scanner
{
	public delegate void ProgressChangeDG(int value);

	public struct PAGE
	{
		private IntPtr _Base;

		private int _Size;

		public IntPtr Base => _Base;

		public int Size => _Size;

		public PAGE(IntPtr @base, int size)
		{
			this = default(PAGE);
			_Base = @base;
			_Size = size;
		}
	}

#if ARM64_BUILD
	// 64-bit MEMORY_BASIC_INFORMATION layout. The target (DunDefGame.exe) is a
	// 32-bit emulated process under Prism, but VirtualQueryEx returns the
	// caller's-bitness struct, so on ARM64 we must use the 48-byte layout with
	// 8-byte pointers, the PartitionId word, and a SIZE_T RegionSize.
	[StructLayout(LayoutKind.Sequential)]
	private struct INFORMATION
	{
		public IntPtr BaseAddress;
		public IntPtr AllocationBase;
		public uint AllocationProtect;
		public ushort PartitionId;
		private ushort _pad;
		public IntPtr RegionSizeNative;
		public uint State;
		public uint Protect;
		public uint Type;
		public int RegionSize => (int)RegionSizeNative.ToInt64();
	}
#else
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private struct INFORMATION
	{
		public IntPtr BaseAddress;

		public IntPtr AllocationBase;

		public uint AllocationProtect;

		public int RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;
	}
#endif

	public ProgressChangeDG ProgressChangeEvent;

	private List<PAGE> _Pages;

	private List<int> _Results;

	private int PID;

	private byte[] _Mask;

	private byte[] _Search;

	private byte[] _Data;

	private bool HandleMask;

	private int MaskIndex;

	public IntPtr Handle { get; set; }

	public PAGE[] Pages => _Pages.ToArray();

	public int[] Results
	{
		get
		{
			return _Results.ToArray();
		}
		set
		{
			_Results = new List<int>(value);
		}
	}

	public bool Scanning { get; set; }

	public Scanner(ProgressChangeDG progressChange)
	{
		_Pages = new List<PAGE>();
		_Results = new List<int>();
		ProgressChangeEvent = progressChange;
	}

	public void OpenProcess(int processId)
	{
		PID = processId;
		Handle = OpenProcess(1080u, inherit: false, processId);
		if (Handle == IntPtr.Zero)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			if (lastWin32Error == 5)
			{
				throw new UnauthorizedAccessException("Please run this program as an administrator.");
			}
			throw new Exception(lastWin32Error.ToString());
		}
		else
		{
			_Data = new byte[0];
		}
	}

	public void CloseProcess()
	{
		CloseHandle(Handle);
		Handle = IntPtr.Zero;
		_Pages.Clear();
		_Results.Clear();
		_Mask = null;
		_Data = null;
		_Search = null;
	}

	public void ScanPages()
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		_Pages.Clear();
		Scanning = true;
		int num = default(int);
		while (true)
		{
			INFORMATION information = default(INFORMATION);
			if (QueryEx(Handle, num, ref information, Marshal.SizeOf<INFORMATION>()) == 0)
			{
				break;
			}
			if ((ulong)information.State == 4096 && (ulong)information.Type == 131072 && (ulong)information.Protect == 4 && information.RegionSize != 0)
			{
				List<PAGE> pages = _Pages;
				PAGE item = new PAGE(information.BaseAddress, information.RegionSize);
				pages.Add(item);
			}
			if ((long)(uint)(information.BaseAddress.ToInt32() + information.RegionSize) > 2147483647L)
			{
				break;
			}
			num = information.BaseAddress.ToInt32() + information.RegionSize;
		}
		Scanning = false;
	}

	public void FirstScan(byte[] search, int index = 0, int step = 4, byte[] mask = null)
	{
		CheckParameters(search, mask);
		if (step == 0)
		{
			throw new ArgumentOutOfRangeException();
		}
		_Results.Clear();
		Scanning = true;
		ProgressChangeEvent(0);
		int num = Pages.Length - 1;
		int length = default(int);
		for (int i = 0; i <= num; i++)
		{
			int size = Pages[i].Size;
			if (size >= search.Length + index)
			{
				int num2 = Pages[i].Base.ToInt32();
				Array.Resize(ref _Data, size);
				if (ReadMem(Handle, num2, _Data, _Data.Length, ref length))
				{
					int num3 = length - search.Length;
					for (int j = index; ((step >> 31) ^ j) <= ((step >> 31) ^ num3); j += step)
					{
						if (ScanData(j))
						{
							_Results.Add(num2 + j);
						}
					}
				}
			}
			ProgressChangeEvent((int)Math.Round((double)(i + 1) / (double)Pages.Length * 100.0));
		}
		Scanning = false;
		ProgressChangeEvent(100);
	}

	public void NextScan(byte[] search, byte[] mask = null)
	{
		CheckParameters(search, mask);
		if (_Results.Count == 0)
		{
			throw new ArgumentOutOfRangeException();
		}
		ProgressChangeEvent(0);
		int count = _Results.Count;
		Scanning = true;
		Array.Resize(ref _Data, search.Length);
		bool flag = default(bool);
		int num2 = default(int);
		int num4 = default(int);
		int length = default(int);
		while (!flag && _Results.Count != 0)
		{
			flag = true;
			int num = num2;
			int num3 = _Results.Count - 1;
			for (int i = num; i <= num3; i++)
			{
				num2 = i;
				num4++;
				flag = ReadMem(Handle, _Results[i], _Data, _Data.Length, ref length) && ScanData(0);
				if (!flag)
				{
					_Results.RemoveAt(i);
					break;
				}
				ProgressChangeEvent((int)Math.Round((double)num4 / (double)count * 100.0));
			}
		}
		Scanning = false;
		ProgressChangeEvent(100);
	}

	private bool ScanData(int offset)
	{
		if (HandleMask)
		{
			int maskIndex = MaskIndex;
			int num = _Search.Length - 1;
			for (int i = maskIndex; i <= num; i++)
			{
				if (_Mask[i] == byte.MaxValue && _Data[offset + i] != _Search[i])
				{
					return false;
				}
			}
		}
		else
		{
			int num2 = _Search.Length - 1;
			for (int j = 0; j <= num2; j++)
			{
				if (_Data[offset + j] != _Search[j])
				{
					return false;
				}
			}
		}
		return true;
	}

	private void CheckParameters(byte[] search, byte[] mask)
	{
		_Search = search;
		_Mask = mask;
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		if (search.Length == 0)
		{
			throw new ArgumentOutOfRangeException();
		}
		if (mask != null)
		{
			if (search.Length != mask.Length)
			{
				throw new ArgumentOutOfRangeException();
			}
			if (!CheckMask(mask))
			{
				throw new FormatException();
			}
			HandleMask = true;
		}
		else
		{
			HandleMask = false;
		}
	}

	private bool CheckMask(byte[] mask)
	{
		int num = mask.Length - 1;
		for (int i = 0; i <= num; i++)
		{
			if (mask[i] == byte.MaxValue)
			{
				MaskIndex = i;
				return true;
			}
		}
		return false;
	}

	public byte[] ReadMemory(int address, int length)
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		// NOTE: negative int32 addresses are valid on LARGEADDRESSAWARE processes
		// like DunDefGame.exe, which allocates in the 2–4 GB range. Do not reject
		// them — ReadProcessMemory handles the full address space.
		if (length < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		byte[] array = new byte[length - 1 + 1];
		int length2 = default(int);
		if (!ReadMem(Handle, address, array, array.Length, ref length2))
		{
			throw new Exception(Marshal.GetLastWin32Error().ToString());
		}
		return array;
	}

	public void WriteMemory(int address, byte[] data)
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		// NOTE: negative int32 addresses are valid on LARGEADDRESSAWARE processes
		// like DunDefGame.exe (allocates in 2-4 GB range). Do not reject them.
		if (data.Length == 0)
		{
			throw new ArgumentOutOfRangeException();
		}
		int length = default(int);
		if (!WriteMem(Handle, address, data, data.Length, ref length))
		{
			throw new Exception(Marshal.GetLastWin32Error().ToString());
		}
	}

	public int Alloc(int length)
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		if (length < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		int num = AllocEx(Handle, 0, length, 12288, 4).ToInt32();
		if (num == 0)
		{
			throw new Exception(Marshal.GetLastWin32Error().ToString());
		}
		return num;
	}

	public void Free(int address)
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		if (address < 0)
		{
			throw new ArgumentOutOfRangeException();
		}
		if (!FreeEx(Handle, address, 0, 32768))
		{
			throw new Exception(Marshal.GetLastWin32Error().ToString());
		}
	}

	public void Suspend()
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		Process processById = Process.GetProcessById(PID);
		foreach (ProcessThread thread in processById.Threads)
		{
			IntPtr intPtr = OpenThread(2u, inherit: false, thread.Id);
			if (!(intPtr == IntPtr.Zero))
			{
				SuspendThread(intPtr);
			}
		}
	}

	public void Resume()
	{
		if (Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException();
		}
		Process processById = Process.GetProcessById(PID);
		foreach (ProcessThread thread in processById.Threads)
		{
			IntPtr intPtr = OpenThread(2u, inherit: false, thread.Id);
			if (!(intPtr == IntPtr.Zero))
			{
				ResumeThread(intPtr);
			}
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, int process);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenThread(uint access, bool inherit, int thread);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int SuspendThread(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int ResumeThread(IntPtr handle);

#if ARM64_BUILD
	// ARM64 native -> emulated-x86 target. Zero-extend int@base via uint to a
	// 64-bit IntPtr; sign-extension would push DD1's high-half (>= 2 GB)
	// addresses into kernel space and the call would be rejected.
	private static IntPtr Z(int v) => (IntPtr)(uint)v;

	[DllImport("kernel32.dll", EntryPoint = "VirtualQueryEx", SetLastError = true)]
	[SuppressUnmanagedCodeSecurity]
	private static extern UIntPtr QueryExNative(IntPtr handle, IntPtr addr, ref INFORMATION info, UIntPtr length);

	private static int QueryEx(IntPtr handle, int @base, ref INFORMATION info, int length)
		=> (int)QueryExNative(handle, Z(@base), ref info, (UIntPtr)length).ToUInt64();

	[DllImport("kernel32.dll", EntryPoint = "VirtualAllocEx", SetLastError = true)]
	private static extern IntPtr AllocExNative(IntPtr handle, IntPtr addr, UIntPtr length, int type, int protect);

	private static IntPtr AllocEx(IntPtr handle, int address, int length, int type, int protect)
		=> AllocExNative(handle, Z(address), (UIntPtr)length, type, protect);

	[DllImport("kernel32.dll", EntryPoint = "VirtualFreeEx", SetLastError = true)]
	private static extern bool FreeExNative(IntPtr handle, IntPtr addr, UIntPtr length, int type);

	private static bool FreeEx(IntPtr handle, int address, int length, int type)
		=> FreeExNative(handle, Z(address), (UIntPtr)length, type);

	[DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
	[SuppressUnmanagedCodeSecurity]
	private static extern bool ReadMemNative(IntPtr handle, IntPtr addr, byte[] data, UIntPtr nSize, out UIntPtr nRead);

	public static bool ReadMem(IntPtr handle, int @base, byte[] data, int dataLength, ref int length)
	{
		bool ok = ReadMemNative(handle, Z(@base), data, (UIntPtr)dataLength, out var nRead);
		length = (int)nRead.ToUInt64();
		return ok;
	}

	[DllImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
	[SuppressUnmanagedCodeSecurity]
	private static extern bool WriteMemNative(IntPtr handle, IntPtr addr, byte[] data, UIntPtr nSize, out UIntPtr nWritten);

	public static bool WriteMem(IntPtr handle, int @base, byte[] data, int dataLength, ref int length)
	{
		bool ok = WriteMemNative(handle, Z(@base), data, (UIntPtr)dataLength, out var nWritten);
		length = (int)nWritten.ToUInt64();
		return ok;
	}
#else
	[DllImport("kernel32.dll", EntryPoint = "VirtualQueryEx")]
	[SuppressUnmanagedCodeSecurity]
	private static extern int QueryEx(IntPtr handle, int @base, ref INFORMATION information, int length);

	[DllImport("kernel32.dll", EntryPoint = "VirtualAllocEx")]
	private static extern IntPtr AllocEx(IntPtr handle, int address, int length, int type, int protect);

	[DllImport("kernel32.dll", EntryPoint = "VirtualFreeEx")]
	private static extern bool FreeEx(IntPtr handle, int address, int length, int type);

	[DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
	[SuppressUnmanagedCodeSecurity]
	public static extern bool ReadMem(IntPtr handle, int @base, byte[] data, int dataLength, ref int length);

	[DllImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
	[SuppressUnmanagedCodeSecurity]
	public static extern bool WriteMem(IntPtr handle, int @base, byte[] data, int dataLength, ref int length);
#endif

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);
}
