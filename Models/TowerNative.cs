using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TowerNative
{
	public int CurrentHP;

	public int MaxHP;

	public float AttackDamage;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
	public byte[] R0;

	public float AttackRate;

	public float R1;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
	public byte[] R2;

	public float AttackRange;

	public float AttackArc;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 52)]
	public byte[] R3;

	public float UpgradeLevel;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
	public byte[] R4;

	public int MaxUpgrades;
}
