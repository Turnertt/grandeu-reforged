using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HeroNative
{
	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
	public int[] StatModifiers;

	public int Level;

	public int MaxLevel;

	public int MaxDemoLevel;

	public int Experience;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
	public byte[] R1;

	public NativeArray HeroName;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
	public byte[] R2;

	public HeroColorNative Color1;

	public HeroColorNative Color2;

	public HeroColorNative Color3;
}
