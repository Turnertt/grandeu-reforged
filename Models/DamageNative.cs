using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DamageNative
{
	public int DamageType;

	public int Value;
}
