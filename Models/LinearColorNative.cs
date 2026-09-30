using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LinearColorNative
{
	// UE3 FLinearColor: R, G, B, A floats, 0..1. This struct was declared
	// (A, R, G, B) from the original decompile until 2026-09-27; that was
	// not the game's layout but an artifact of ItemNative sitting 4 bytes
	// early from PrimaryColorSets onward (see ItemNative.R2). With the
	// struct realigned, item and hero colors share the same real layout.
	public float R;

	public float G;

	public float B;

	public float A;

	public override string ToString()
	{
		return $"{R:N1}, {G:N1}, {B:N1}";
	}
}
